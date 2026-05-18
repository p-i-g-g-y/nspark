using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NSpark.GraphQL;
using NSpark.Models;
using NSpark.Proto;
using uniffi.spark_frost;

namespace NSpark.Services;

/// <inheritdoc/>
public static class WithdrawalService
{
    private const uint TimeLockInterval = 100;
    private const uint DirectTimelockOffset = 50;

    /// <summary>
    /// Get a fee estimate for an on-chain withdrawal (cooperative exit).
    /// </summary>
    public static async Task<FeeQuote> GetFeeQuoteAsync(
        this SparkWallet wallet,
        string[] leafIds,
        string onChainAddress,
        CancellationToken ct = default)
    {
        var variables = new Dictionary<string, object>
        {
            ["leaf_external_ids"] = leafIds,
            ["withdrawal_address"] = onChainAddress,
        };

        var response = await wallet.SspClient.ExecuteAsync<CoopExitFeeEstimateResponse>(
            Mutations.CoopExitFeeEstimate, variables, ct).ConfigureAwait(false);

        // The SSP returns each fee as a CurrencyAmount with an `original_unit` discriminator and
        // we MUST honour that unit — earlier NSpark builds blindly divided by 1000 (assumed the
        // unit was always MILLISATOSHI) and reported a 1606-sat fee as 2 sats whenever the SSP
        // answered in SATOSHI. The conversion factors below mirror Lightspark's reference
        // `amount_as_msats` (python-sdk/lightspark/utils/currency_amount.py), scaled down by
        // 1000 to land in sats. Fees < 1 sat (only possible for sub-sat units like NANOBITCOIN
        // or MILLISATOSHI) round UP — under-quoting could cause the send to fail at execute time.
        var fast = response.CoopExitFeeEstimates.SpeedFast;
        var totalFeeSats = ToSats(fast.UserFee) + ToSats(fast.L1BroadcastFee);
        return new FeeQuote(FeeSats: totalFeeSats, FeeRateSatsPerVbyte: 0);

        static long ToSats(CoopExitFeeValue v)
        {
            // Match Lightspark's CurrencyUnit enum casing exactly — the SSP echoes those strings.
            return (v.OriginalUnit ?? string.Empty).ToUpperInvariant() switch
            {
                "SATOSHI" => v.OriginalValue,
                "MILLISATOSHI" => (v.OriginalValue + 999) / 1000,
                "BITCOIN" => v.OriginalValue * 100_000_000L,
                "MILLIBITCOIN" => v.OriginalValue * 100_000L,
                "MICROBITCOIN" => v.OriginalValue * 100L,
                "NANOBITCOIN" => (v.OriginalValue + 9) / 10,
                // Unknown unit (e.g. fiat or a future Lightspark-added value): treat as sats so
                // we still produce *some* answer. The SSP doesn't return fiat units for L1 fees
                // in practice, but defaulting to sats matches what Swift does on the same path.
                _ => v.OriginalValue,
            };
        }
    }

    /// <summary>
    /// Withdraw from Spark to an on-chain Bitcoin address (cooperative exit).
    /// </summary>
    /// <returns>The L1 transaction ID.</returns>
    public static async Task<string> WithdrawAsync(
        this SparkWallet wallet,
        string onChainAddress,
        long amountSats,
        CancellationToken ct = default)
    {
        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);

        // Step 1: Select leaves covering amount (exact match or swap)
        var selectedLeaves = await wallet.SelectLeavesWithSwapAsync(amountSats, ct).ConfigureAwait(false);
        var leafIds = selectedLeaves.Select(l => l.Id).ToArray();

        // Step 2: Request coop exit from SSP
        var transferId = Guid.NewGuid().ToString().ToLowerInvariant();

        var sspResponse = await wallet.SspClient.ExecuteAsync<RequestCoopExitResponse>(
            Mutations.RequestCoopExit,
            new Dictionary<string, object>
            {
                ["leaf_external_ids"] = leafIds,
                ["withdrawal_address"] = onChainAddress,
                ["exit_speed"] = "FAST",
                ["withdraw_all"] = true,
                ["user_outbound_transfer_external_id"] = transferId,
            },
            ct).ConfigureAwait(false);

        var connectorTxHex = sspResponse.RequestCoopExit.Request.RawConnectorTransaction;
        var coopExitTxid = sspResponse.RequestCoopExit.Request.CoopExitTxid;
        var connectorTxBytes = Convert.FromHexString(connectorTxHex);
        var connectorTxId = ComputeTxId(connectorTxBytes);

        // Step 3: Build LeafRefundTxSigningJobs with connector inputs
        var receiverPubKey = Convert.FromHexString(wallet.Client.Options.SspIdentityPublicKeyHex);
        var expiryTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
            DateTimeOffset.UtcNow.AddDays(7).AddMinutes(5));

        var signingJobs = new List<LeafRefundTxSigningJob>();
        var leafDataList = new List<LeafSigningData>();

        for (int i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            var node = leaf.Node;
            var signingKey = wallet.Signer.DeriveLeafSigningKey(leaf.Id);
            var signingPubKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();

            // Get current sequence and decrement
            var refundTxBytes = node.RefundTx.Length > 0
                ? node.RefundTx.ToByteArray()
                : node.NodeTx.ToByteArray();
            var rawSequence = ClaimService.ParseInputSequence(refundTxBytes);
            var currentTimelock = rawSequence & 0xFFFF;
            var bit30 = rawSequence & (1u << 30);
            var nextTimelock = currentTimelock - TimeLockInterval;
            var cpfpSequence = bit30 | nextTimelock;
            var directSequence = bit30 | (nextTimelock + DirectTimelockOffset);

            var cpfpNodeTx = node.NodeTx.ToByteArray();
            var directNodeTx = node.DirectTx.Length > 0 ? node.DirectTx.ToByteArray() : null;
            var isZeroNode = IsZeroTimelockNode(cpfpNodeTx);

            // Build refund txs (single input)
            var refundTrio = SparkFrostMethods.ConstructRefundTxTrio(
                cpfpNodeTx: cpfpNodeTx,
                directNodeTx: directNodeTx,
                vout: 0,
                receivingPubkey: receiverPubKey,
                network: networkStr,
                sequence: cpfpSequence,
                directSequence: directSequence,
                // SSP validates all three refund outputs on coop-exit and rejects with
                // "expected value X on output 0" if the standard fee isn't deducted.
                feeSats: SparkConstants.DefaultRefundFeeSats);

            // Add connector input to each refund tx
            var connectorInput = MakeConnectorInputBytes(connectorTxId, (uint)i);
            var cpfpRefundWithConnector = AddInputToRawTx(refundTrio.@cpfpRefund.@tx, connectorInput);

            byte[]? directRefundWithConnector = null;
            if (refundTrio.@directRefund != null && !isZeroNode)
            {
                directRefundWithConnector = AddInputToRawTx(refundTrio.@directRefund.@tx, connectorInput);
            }

            var directFromCpfpRefundWithConnector = AddInputToRawTx(
                refundTrio.@directFromCpfpRefund.@tx, connectorInput);

            // Generate FROST nonce commitments
            var keyPackage = new KeyPackage(
                secretKey: signingKey,
                publicKey: signingPubKey,
                verifyingKey: verifyingKey);
            var cpfpNonce = SparkFrostMethods.FrostNonce(keyPackage);
            var directNonce = SparkFrostMethods.FrostNonce(keyPackage);
            var directFromCpfpNonce = SparkFrostMethods.FrostNonce(keyPackage);

            // Build SigningJob for each refund tx (unsigned — just commitment)
            var cpfpSigningJob = new SigningJob
            {
                SigningPublicKey = ByteString.CopyFrom(signingPubKey),
                RawTx = ByteString.CopyFrom(cpfpRefundWithConnector),
                SigningNonceCommitment = new Proto.Common.SigningCommitment
                {
                    Hiding = ByteString.CopyFrom(cpfpNonce.@commitment.@hiding),
                    Binding = ByteString.CopyFrom(cpfpNonce.@commitment.@binding),
                },
            };

            var directFromCpfpSigningJob = new SigningJob
            {
                SigningPublicKey = ByteString.CopyFrom(signingPubKey),
                RawTx = ByteString.CopyFrom(directFromCpfpRefundWithConnector),
                SigningNonceCommitment = new Proto.Common.SigningCommitment
                {
                    Hiding = ByteString.CopyFrom(directFromCpfpNonce.@commitment.@hiding),
                    Binding = ByteString.CopyFrom(directFromCpfpNonce.@commitment.@binding),
                },
            };

            var leafJob = new LeafRefundTxSigningJob
            {
                LeafId = leaf.Id,
                RefundTxSigningJob = cpfpSigningJob,
                DirectFromCpfpRefundTxSigningJob = directFromCpfpSigningJob,
            };

            if (directRefundWithConnector != null)
            {
                leafJob.DirectRefundTxSigningJob = new SigningJob
                {
                    SigningPublicKey = ByteString.CopyFrom(signingPubKey),
                    RawTx = ByteString.CopyFrom(directRefundWithConnector),
                    SigningNonceCommitment = new Proto.Common.SigningCommitment
                    {
                        Hiding = ByteString.CopyFrom(directNonce.@commitment.@hiding),
                        Binding = ByteString.CopyFrom(directNonce.@commitment.@binding),
                    },
                };
            }

            signingJobs.Add(leafJob);
            leafDataList.Add(new LeafSigningData(
                leaf.Id, signingKey, verifyingKey,
                cpfpRefundWithConnector, directRefundWithConnector, directFromCpfpRefundWithConnector,
                cpfpNonce, directNonce, directFromCpfpNonce,
                cpfpNodeTx, directNodeTx, i));
        }

        // Step 4: Call cooperative_exit_v2
        var coopExitTxidBytes = Convert.FromHexString(coopExitTxid);
        Array.Reverse(coopExitTxidBytes);

        var transferRequest = new StartTransferRequest
        {
            TransferId = transferId,
            OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.Signer.IdentityPublicKey),
            ReceiverIdentityPublicKey = ByteString.CopyFrom(receiverPubKey),
            ExpiryTime = expiryTime,
        };
        transferRequest.LeavesToSend.AddRange(signingJobs);

        var exitResponse = await coordinatorClient.cooperative_exit_v2Async(
            new CooperativeExitRequest
            {
                Transfer = transferRequest,
                ExitId = Guid.NewGuid().ToString().ToLowerInvariant(),
                ExitTxid = ByteString.CopyFrom(coopExitTxidBytes),
                ConnectorTx = ByteString.CopyFrom(connectorTxBytes),
            },
            headers,
            cancellationToken: ct);

        // Step 5: Sign FROST with SO signing results and aggregate
        var cpfpSignatures = new List<UserSignedTxSigningJob>();
        var directSignatures = new List<UserSignedTxSigningJob>();
        var directFromCpfpSignatures = new List<UserSignedTxSigningJob>();

        foreach (var result in exitResponse.SigningResults)
        {
            var leafData = leafDataList.First(d => d.LeafId == result.LeafId);
            var connectorPrevOut = ParseTxOutput(connectorTxBytes, (uint)leafData.ConnectorOutputIndex);

            // Sign CPFP refund (multi-input: node output + connector output)
            var cpfpNodeOutput = ParseTxOutput(leafData.CpfpNodeTx, 0);
            var cpfpSighash = SparkFrostMethods.ComputeMultiInputSighashUniffi(
                tx: leafData.CpfpRefundTx,
                inputIndex: 0,
                prevOutScripts: [cpfpNodeOutput.Script, connectorPrevOut.Script],
                prevOutValues: [cpfpNodeOutput.Value, connectorPrevOut.Value]);

            var cpfpAgg = FrostSigningHelper.SignAndAggregateFrost(
                cpfpSighash, leafData.SigningKey, leafData.VerifyingKey,
                leafData.CpfpNonce, result.RefundTxSigningResult);

            var signingPubKey = SparkFrostMethods.GetPublicKeyBytes(leafData.SigningKey, compressed: true);
            cpfpSignatures.Add(new UserSignedTxSigningJob
            {
                LeafId = result.LeafId,
                SigningPublicKey = ByteString.CopyFrom(signingPubKey),
                RawTx = ByteString.CopyFrom(leafData.CpfpRefundTx),
                UserSignature = ByteString.CopyFrom(cpfpAgg),
            });

            // Sign direct refund (if exists)
            if (leafData.DirectRefundTx != null && leafData.DirectNodeTx != null
                && result.DirectRefundTxSigningResult != null)
            {
                var directNodeOutput = ParseTxOutput(leafData.DirectNodeTx, 0);
                var directSighash = SparkFrostMethods.ComputeMultiInputSighashUniffi(
                    tx: leafData.DirectRefundTx,
                    inputIndex: 0,
                    prevOutScripts: [directNodeOutput.Script, connectorPrevOut.Script],
                    prevOutValues: [directNodeOutput.Value, connectorPrevOut.Value]);

                var directAgg = FrostSigningHelper.SignAndAggregateFrost(
                    directSighash, leafData.SigningKey, leafData.VerifyingKey,
                    leafData.DirectNonce, result.DirectRefundTxSigningResult);

                directSignatures.Add(new UserSignedTxSigningJob
                {
                    LeafId = result.LeafId,
                    SigningPublicKey = ByteString.CopyFrom(signingPubKey),
                    RawTx = ByteString.CopyFrom(leafData.DirectRefundTx),
                    UserSignature = ByteString.CopyFrom(directAgg),
                });
            }

            // Sign directFromCpfp refund
            var dcfpSighash = SparkFrostMethods.ComputeMultiInputSighashUniffi(
                tx: leafData.DirectFromCpfpRefundTx,
                inputIndex: 0,
                prevOutScripts: [cpfpNodeOutput.Script, connectorPrevOut.Script],
                prevOutValues: [cpfpNodeOutput.Value, connectorPrevOut.Value]);

            var dcfpAgg = FrostSigningHelper.SignAndAggregateFrost(
                dcfpSighash, leafData.SigningKey, leafData.VerifyingKey,
                leafData.DirectFromCpfpNonce, result.DirectFromCpfpRefundTxSigningResult);

            directFromCpfpSignatures.Add(new UserSignedTxSigningJob
            {
                LeafId = result.LeafId,
                SigningPublicKey = ByteString.CopyFrom(signingPubKey),
                RawTx = ByteString.CopyFrom(leafData.DirectFromCpfpRefundTx),
                UserSignature = ByteString.CopyFrom(dcfpAgg),
            });
        }

        // Step 6: Prepare key tweaks (transfer leaves to SSP)
        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct);
        var soOperators = soListResponse.SigningOperators;
        var soCount = (uint)soOperators.Count;
        var threshold = Math.Max(2, (soCount + 2) / 2);

        var perSoTweaks = new Dictionary<string, SendLeafKeyTweaks>();
        foreach (var (soId, _) in soOperators)
        {
            perSoTweaks[soId] = new SendLeafKeyTweaks();
        }

        foreach (var leaf in selectedLeaves)
        {
            var oldSigningKey = wallet.Signer.DeriveLeafSigningKey(leaf.Id);
            var newRandomKey = SparkFrostMethods.RandomSecretKeyBytes();
            var keyTweak = ClaimService.SubtractPrivateKeys(oldSigningKey, newRandomKey);
            var vssShares = SparkFrostMethods.SplitSecretWithProofsUniffi(
                keyTweak, threshold: threshold, numShares: soCount);
            var secretCipher = SparkFrostMethods.EncryptEcies(newRandomKey, receiverPubKey);

            var sigPayload = Encoding.UTF8.GetBytes(leaf.Id + transferId);
            sigPayload = [.. sigPayload, .. secretCipher];
            var tweakSig = wallet.Signer.SignCompactWithIdentityKey(SHA256.HashData(sigPayload));

            var pubkeySharesTweak = new Dictionary<string, ByteString>();
            foreach (var (soId, soInfo) in soOperators)
            {
                var matchedShare = vssShares.First(s => s.@index == soInfo.Index + 1);
                pubkeySharesTweak[soId] = ByteString.CopyFrom(
                    SparkFrostMethods.GetPublicKeyBytes(matchedShare.@share, compressed: true));
            }

            foreach (var (soId, soInfo) in soOperators)
            {
                var share = vssShares.First(s => s.@index == soInfo.Index + 1);
                var leafTweak = new SendLeafKeyTweak
                {
                    LeafId = leaf.Id,
                    SecretShareTweak = new SecretShare { SecretShare_ = ByteString.CopyFrom(share.@share) },
                    SecretCipher = ByteString.CopyFrom(secretCipher),
                    Signature = ByteString.CopyFrom(tweakSig),
                };
                foreach (var proof in share.@proofs)
                {
                    leafTweak.SecretShareTweak.Proofs.Add(ByteString.CopyFrom(proof));
                }

                foreach (var (k, v) in pubkeySharesTweak)
                {
                    leafTweak.PubkeySharesTweak.Add(k, v);
                }

                perSoTweaks[soId].LeavesToSend.Add(leafTweak);
            }
        }

        // Encrypt per-SO key tweak packages
        var soConfigs = wallet.Client.Options.SigningOperators;
        var keyTweakPackage = new Dictionary<string, ByteString>();
        foreach (var (soId, _) in soOperators)
        {
            var tweaksBytes = perSoTweaks[soId].ToByteArray();
            var soConfig = soConfigs.First(c => c.Identifier == soId);
            var soIdentityPubKey = Convert.FromHexString(soConfig.IdentityPublicKeyHex);
            var encrypted = SparkFrostMethods.EncryptEcies(tweaksBytes, soIdentityPubKey);
            keyTweakPackage[soId] = ByteString.CopyFrom(encrypted);
        }

        // Build TransferPackage with aggregated signatures + key tweaks
        var transferPackage = new TransferPackage { HashVariant = HashVariant.V2 };
        foreach (var job in cpfpSignatures)
        {
            transferPackage.LeavesToSend.Add(job);
        }

        foreach (var job in directSignatures)
        {
            transferPackage.DirectLeavesToSend.Add(job);
        }

        foreach (var job in directFromCpfpSignatures)
        {
            transferPackage.DirectFromCpfpLeavesToSend.Add(job);
        }

        foreach (var (soId, cipher) in keyTweakPackage)
        {
            transferPackage.KeyTweakPackage.Add(soId, cipher);
        }

        // Sign transfer package
        var transferIdBytes = Convert.FromHexString(transferId.Replace("-", ""));
        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(transferIdBytes)
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        var packageSignature = wallet.Signer.SignWithIdentityKey(packageHash);
        transferPackage.UserSignature = ByteString.CopyFrom(packageSignature);

        // Step 7: Finalize transfer with transfer package
        await coordinatorClient.finalize_transfer_with_transfer_packageAsync(
            new FinalizeTransferWithTransferPackageRequest
            {
                TransferId = exitResponse.Transfer.Id,
                OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.Signer.IdentityPublicKey),
                TransferPackage = transferPackage,
            },
            headers,
            cancellationToken: ct);

        // Step 8: Complete coop exit via SSP
        await wallet.SspClient.ExecuteAsync<CompleteCoopExitResponse>(
            Mutations.CompleteCoopExit,
            new Dictionary<string, object>
            {
                ["user_outbound_transfer_external_id"] = exitResponse.Transfer.Id,
            },
            ct).ConfigureAwait(false);

        return coopExitTxid;
    }

    // ── Raw tx helpers ──

    private sealed record LeafSigningData(
        string LeafId,
        byte[] SigningKey,
        byte[] VerifyingKey,
        byte[] CpfpRefundTx,
        byte[]? DirectRefundTx,
        byte[] DirectFromCpfpRefundTx,
        NonceResult CpfpNonce,
        NonceResult DirectNonce,
        NonceResult DirectFromCpfpNonce,
        byte[] CpfpNodeTx,
        byte[]? DirectNodeTx,
        int ConnectorOutputIndex);

    /// <summary>
    /// Compute txid from raw transaction bytes (double SHA-256 of witness-stripped serialization).
    /// Returns bytes in internal byte order (used as prevout hash in inputs).
    /// </summary>
    private static byte[] ComputeTxId(byte[] rawTx)
    {
        var strippedTx = StripWitness(rawTx);
        var hash1 = SHA256.HashData(strippedTx);
        return SHA256.HashData(hash1);
    }

    /// <summary>
    /// Strip witness data from a segwit transaction to get legacy serialization for txid.
    /// </summary>
    private static byte[] StripWitness(byte[] rawTx)
    {
        int offset = 4; // skip version
        bool hasWitness = rawTx.Length > 5 && rawTx[offset] == 0x00 && rawTx[offset + 1] == 0x01;
        if (!hasWitness)
        {
            return rawTx;
        }

        using var result = new MemoryStream();
        result.Write(rawTx, 0, 4); // version

        offset += 2; // skip marker + flag

        // Parse inputs
        var (inputCount, inputCountLen) = ReadVarInt(rawTx, offset);
        int inputCountStart = offset;
        offset += inputCountLen;

        for (ulong j = 0; j < inputCount; j++)
        {
            offset += 36; // txid + vout
            var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, offset);
            offset += scriptLenLen + (int)scriptLen + 4; // script + sequence
        }

        int afterInputs = offset;

        // Parse outputs
        var (outputCount, outputCountLen) = ReadVarInt(rawTx, offset);
        offset += outputCountLen;
        for (ulong j = 0; j < outputCount; j++)
        {
            offset += 8; // value
            var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, offset);
            offset += scriptLenLen + (int)scriptLen;
        }

        int afterOutputs = offset;

        // result = version + inputs + outputs + locktime
        result.Write(rawTx, inputCountStart, afterOutputs - inputCountStart);
        result.Write(rawTx, rawTx.Length - 4, 4); // locktime

        return result.ToArray();
    }

    /// <summary>
    /// Parse a tx output (script + value) at a given vout index.
    /// </summary>
    internal static (byte[] Script, ulong Value) ParseTxOutput(byte[] rawTx, uint vout)
    {
        int offset = 4; // skip version
        if (rawTx.Length > 5 && rawTx[offset] == 0x00 && rawTx[offset + 1] == 0x01)
        {
            offset += 2; // skip segwit marker + flag
        }

        // Skip inputs
        var (inputCount, inputCountLen) = ReadVarInt(rawTx, offset);
        offset += inputCountLen;
        for (ulong j = 0; j < inputCount; j++)
        {
            offset += 36;
            var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, offset);
            offset += scriptLenLen + (int)scriptLen + 4;
        }

        // Parse outputs
        var (_, outputCountLen) = ReadVarInt(rawTx, offset);
        offset += outputCountLen;

        for (uint i = 0; i <= vout; i++)
        {
            ulong value = BitConverter.ToUInt64(rawTx, offset);
            offset += 8;
            var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, offset);
            offset += scriptLenLen;
            var script = new byte[scriptLen];
            Array.Copy(rawTx, offset, script, 0, (int)scriptLen);
            offset += (int)scriptLen;

            if (i == vout)
            {
                return (script, value);
            }
        }

        throw new InvalidOperationException($"vout {vout} not found in transaction");
    }

    /// <summary>
    /// Check if a node tx has zero timelock (sequence lower 16 bits == 0).
    /// </summary>
    private static bool IsZeroTimelockNode(byte[] nodeTx)
    {
        var seq = ClaimService.ParseInputSequence(nodeTx);
        return (seq & 0xFFFF) == 0;
    }

    /// <summary>
    /// Create raw bytes for a connector input: txid(32) + vout(4) + empty scriptSig(1) + sequence(4).
    /// </summary>
    private static byte[] MakeConnectorInputBytes(byte[] txId, uint vout)
    {
        var input = new byte[32 + 4 + 1 + 4]; // 41 bytes
        // txid already in internal byte order
        Array.Copy(txId, 0, input, 0, 32);
        // vout LE
        BitConverter.TryWriteBytes(input.AsSpan(32), vout);
        // scriptSig length = 0 (already zero)
        // sequence = 0xFFFFFFFF
        BitConverter.TryWriteBytes(input.AsSpan(37), 0xFFFFFFFFu);
        return input;
    }

    /// <summary>
    /// Add an input to a raw Bitcoin transaction, bumping the input count varint and
    /// handling witness data if present.
    /// </summary>
    private static byte[] AddInputToRawTx(byte[] rawTx, byte[] input)
    {
        int offset = 4; // skip version
        bool hasWitness = rawTx.Length > 5 && rawTx[offset] == 0x00 && rawTx[offset + 1] == 0x01;
        if (hasWitness)
        {
            offset += 2;
        }

        // Read input count
        var (inputCount, inputCountLen) = ReadVarInt(rawTx, offset);
        int inputCountOffset = offset;
        offset += inputCountLen;

        // Find end of all inputs
        for (ulong j = 0; j < inputCount; j++)
        {
            offset += 36; // txid + vout
            var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, offset);
            offset += scriptLenLen + (int)scriptLen + 4; // script + sequence
        }
        int afterInputs = offset;

        using var result = new MemoryStream();

        if (hasWitness)
        {
            result.Write(rawTx, 0, 4); // version
            result.Write([0x00, 0x01]); // marker + flag
        }
        else
        {
            result.Write(rawTx, 0, 4); // version
        }

        // New input count
        var newCountBytes = EncodeVarInt(inputCount + 1);
        result.Write(newCountBytes);
        // Existing inputs (skip old input count bytes)
        result.Write(rawTx, inputCountOffset + inputCountLen, afterInputs - (inputCountOffset + inputCountLen));
        // New connector input
        result.Write(input);

        if (hasWitness)
        {
            // Find end of outputs
            int outOffset = afterInputs;
            var (outputCount, outputCountLen) = ReadVarInt(rawTx, outOffset);
            outOffset += outputCountLen;
            for (ulong j = 0; j < outputCount; j++)
            {
                outOffset += 8;
                var (scriptLen, scriptLenLen) = ReadVarInt(rawTx, outOffset);
                outOffset += scriptLenLen + (int)scriptLen;
            }
            int afterOutputs = outOffset;

            // Outputs
            result.Write(rawTx, afterInputs, afterOutputs - afterInputs);

            // Existing witness data
            for (ulong j = 0; j < inputCount; j++)
            {
                var (witnessCount, witnessCountLen) = ReadVarInt(rawTx, outOffset);
                int witnessStart = outOffset;
                outOffset += witnessCountLen;
                for (ulong k = 0; k < witnessCount; k++)
                {
                    var (itemLen, itemLenLen) = ReadVarInt(rawTx, outOffset);
                    outOffset += itemLenLen + (int)itemLen;
                }
                result.Write(rawTx, witnessStart, outOffset - witnessStart);
            }
            // Empty witness for new connector input
            result.WriteByte(0x00);

            // Locktime
            result.Write(rawTx, rawTx.Length - 4, 4);
        }
        else
        {
            // Rest of tx (outputs + locktime)
            result.Write(rawTx, afterInputs, rawTx.Length - afterInputs);
        }

        return result.ToArray();
    }

    internal static (ulong value, int bytesRead) ReadVarInt(byte[] data, int offset)
    {
        var first = data[offset];
        return first switch
        {
            < 0xFD => (first, 1),
            0xFD => (BitConverter.ToUInt16(data, offset + 1), 3),
            0xFE => (BitConverter.ToUInt32(data, offset + 1), 5),
            _ => (BitConverter.ToUInt64(data, offset + 1), 9),
        };
    }

    internal static byte[] EncodeVarInt(ulong value)
    {
        if (value < 0xFD)
        {
            return [(byte)value];
        }

        if (value <= 0xFFFF)
        {
            var buf = new byte[3];
            buf[0] = 0xFD;
            BitConverter.TryWriteBytes(buf.AsSpan(1), (ushort)value);
            return buf;
        }
        if (value <= 0xFFFFFFFF)
        {
            var buf = new byte[5];
            buf[0] = 0xFE;
            BitConverter.TryWriteBytes(buf.AsSpan(1), (uint)value);
            return buf;
        }
        {
            var buf = new byte[9];
            buf[0] = 0xFF;
            BitConverter.TryWriteBytes(buf.AsSpan(1), value);
            return buf;
        }
    }
}
