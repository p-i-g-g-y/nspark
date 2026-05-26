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

        // The SSP returns each fee as a CurrencyAmount with an `original_unit` discriminator.
        // Convert via the shared helper so all three SSP fee call sites (this one, lightning
        // send fee estimate, lightning send status) use identical dispatch.
        var fast = response.CoopExitFeeEstimates.SpeedFast;
        var totalFeeSats =
            CurrencyAmountExtensions.ToSats(fast.UserFee.OriginalValue, fast.UserFee.OriginalUnit) +
            CurrencyAmountExtensions.ToSats(fast.L1BroadcastFee.OriginalValue, fast.L1BroadcastFee.OriginalUnit);
        return new FeeQuote(FeeSats: totalFeeSats, FeeRateSatsPerVbyte: 0);
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

            // Generate three FROST nonce commitments via the signer (phase 1). The actual
            // sighashes aren't known yet — the SO returns the final tx after combining the
            // connector — so we commit to nonces now and sign later.
            var cpfpNonce = await wallet.Signer.GenerateLeafFrostNonceAsync(leaf.Id, ct).ConfigureAwait(false);
            var directNonce = await wallet.Signer.GenerateLeafFrostNonceAsync(leaf.Id, ct).ConfigureAwait(false);
            var directFromCpfpNonce = await wallet.Signer.GenerateLeafFrostNonceAsync(leaf.Id, ct).ConfigureAwait(false);
            var signingPubKey = cpfpNonce.PublicKey;

            // Build SigningJob for each refund tx (unsigned — just commitment)
            var cpfpSigningJob = new SigningJob
            {
                SigningPublicKey = ByteString.CopyFrom(signingPubKey),
                RawTx = ByteString.CopyFrom(cpfpRefundWithConnector),
                SigningNonceCommitment = new Proto.Common.SigningCommitment
                {
                    Hiding = ByteString.CopyFrom(cpfpNonce.Commitment.Hiding),
                    Binding = ByteString.CopyFrom(cpfpNonce.Commitment.Binding),
                },
            };

            var directFromCpfpSigningJob = new SigningJob
            {
                SigningPublicKey = ByteString.CopyFrom(signingPubKey),
                RawTx = ByteString.CopyFrom(directFromCpfpRefundWithConnector),
                SigningNonceCommitment = new Proto.Common.SigningCommitment
                {
                    Hiding = ByteString.CopyFrom(directFromCpfpNonce.Commitment.Hiding),
                    Binding = ByteString.CopyFrom(directFromCpfpNonce.Commitment.Binding),
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
                        Hiding = ByteString.CopyFrom(directNonce.Commitment.Hiding),
                        Binding = ByteString.CopyFrom(directNonce.Commitment.Binding),
                    },
                };
            }

            signingJobs.Add(leafJob);
            leafDataList.Add(new LeafSigningData(
                leaf.Id, signingPubKey, verifyingKey,
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
            OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
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
            var signingPubKey = leafData.SigningPublicKey;

            // Sign CPFP refund (multi-input: node output + connector output)
            var cpfpNodeOutput = ParseTxOutput(leafData.CpfpNodeTx, 0);
            var cpfpSighash = SparkFrostMethods.ComputeMultiInputSighashUniffi(
                tx: leafData.CpfpRefundTx,
                inputIndex: 0,
                prevOutScripts: [cpfpNodeOutput.Script, connectorPrevOut.Script],
                prevOutValues: [cpfpNodeOutput.Value, connectorPrevOut.Value]);

            var cpfpAgg = await SignAndAggregateAsync(
                wallet.Signer, leafData.LeafId, leafData.CpfpNonce,
                cpfpSighash, leafData.VerifyingKey,
                result.RefundTxSigningResult, ct).ConfigureAwait(false);

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

                var directAgg = await SignAndAggregateAsync(
                    wallet.Signer, leafData.LeafId, leafData.DirectNonce,
                    directSighash, leafData.VerifyingKey,
                    result.DirectRefundTxSigningResult, ct).ConfigureAwait(false);

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

            var dcfpAgg = await SignAndAggregateAsync(
                wallet.Signer, leafData.LeafId, leafData.DirectFromCpfpNonce,
                dcfpSighash, leafData.VerifyingKey,
                result.DirectFromCpfpRefundTxSigningResult, ct).ConfigureAwait(false);

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
        var threshold = (uint)Math.Max(2, (soCount + 2) / 2);

        var perSoTweaks = new Dictionary<string, SendLeafKeyTweaks>();
        foreach (var (soId, _) in soOperators)
        {
            perSoTweaks[soId] = new SendLeafKeyTweaks();
        }

        foreach (var leaf in selectedLeaves)
        {
            var tweak = await wallet.Signer.ComputeLeafTweakSharesAsync(
                leaf.Id, receiverPubKey, threshold, soCount, ct).ConfigureAwait(false);
            var secretCipher = tweak.SecretCipher;

            var sigPayload = Encoding.UTF8.GetBytes(leaf.Id + transferId);
            sigPayload = [.. sigPayload, .. secretCipher];
            var tweakSig = await wallet.Signer.SignCompactWithIdentityKeyAsync(
                SHA256.HashData(sigPayload), ct).ConfigureAwait(false);

            var pubkeySharesTweak = new Dictionary<string, ByteString>();
            foreach (var (soId, soInfo) in soOperators)
            {
                var matchedShare = tweak.Shares.First(s => s.Index == soInfo.Index + 1);
                pubkeySharesTweak[soId] = ByteString.CopyFrom(
                    SparkFrostMethods.GetPublicKeyBytes(matchedShare.Share, compressed: true));
            }

            foreach (var (soId, soInfo) in soOperators)
            {
                var share = tweak.Shares.First(s => s.Index == soInfo.Index + 1);
                var leafTweak = new SendLeafKeyTweak
                {
                    LeafId = leaf.Id,
                    SecretShareTweak = new SecretShare { SecretShare_ = ByteString.CopyFrom(share.Share) },
                    SecretCipher = ByteString.CopyFrom(secretCipher),
                    Signature = ByteString.CopyFrom(tweakSig),
                };
                foreach (var proof in share.Proofs)
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
        var packageSignature = await wallet.Signer.SignWithIdentityKeyAsync(packageHash, ct).ConfigureAwait(false);
        transferPackage.UserSignature = ByteString.CopyFrom(packageSignature);

        // Step 7: Finalize transfer with transfer package
        await coordinatorClient.finalize_transfer_with_transfer_packageAsync(
            new FinalizeTransferWithTransferPackageRequest
            {
                TransferId = exitResponse.Transfer.Id,
                OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
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
        byte[] SigningPublicKey,
        byte[] VerifyingKey,
        byte[] CpfpRefundTx,
        byte[]? DirectRefundTx,
        byte[] DirectFromCpfpRefundTx,
        NSpark.Signer.LeafFrostNonceCommitment CpfpNonce,
        NSpark.Signer.LeafFrostNonceCommitment DirectNonce,
        NSpark.Signer.LeafFrostNonceCommitment DirectFromCpfpNonce,
        byte[] CpfpNodeTx,
        byte[]? DirectNodeTx,
        int ConnectorOutputIndex);

    /// <summary>
    /// Phase 2 of withdrawal FROST signing: given a leaf's previously-issued nonce, the
    /// recomputed sighash, and the SO's signing result, ask the signer to sign with the
    /// nonce and then aggregate locally (aggregation is a pure-public-key op).
    /// </summary>
    private static async Task<byte[]> SignAndAggregateAsync(
        NSpark.Signer.ISparkSigner signer,
        string leafId,
        NSpark.Signer.LeafFrostNonceCommitment nonce,
        byte[] sighash,
        byte[] verifyingKey,
        SigningResult signingResult,
        CancellationToken ct)
    {
        var soCommitments = new Dictionary<string, NSpark.Signer.SigningCommitment>();
        foreach (var (soId, c) in signingResult.SigningNonceCommitments)
        {
            soCommitments[soId] = new NSpark.Signer.SigningCommitment(c.Hiding.ToByteArray(), c.Binding.ToByteArray());
        }

        var selfSignature = await signer.SignLeafFrostWithNonceAsync(
            leafId, nonce.NonceHandle, sighash, verifyingKey, soCommitments, adaptorPublicKey: null, ct)
            .ConfigureAwait(false);

        return FrostSigningHelper.AggregateFrostSignature(
            sighash: sighash,
            selfCommitment: nonce.Commitment,
            selfSignature: selfSignature,
            selfPublicKey: nonce.PublicKey,
            verifyingKey: verifyingKey,
            signingResult: signingResult,
            adaptorPublicKey: null);
    }

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
