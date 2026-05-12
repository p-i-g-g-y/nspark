using System.Numerics;
using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;
using uniffi.spark_frost;

namespace NSpark.Services;

/// <inheritdoc/>
public static class ClaimService
{
    private const uint TimeLockInterval = 100;
    private const uint DirectTimelockOffset = 50;

    /// <summary>
    /// Claim all pending incoming transfers (Spark transfers and Lightning receives).
    /// Without calling this, incoming funds remain in "pending" state and never appear in the wallet balance.
    /// Transfers that fail to claim are skipped; successfully claimed transfers are returned.
    /// </summary>
    public static async Task<IReadOnlyList<SparkTransfer>> ClaimPendingTransfersAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);

        // Step 1: Query pending transfers where we are the receiver
        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet : Network.Regtest;
        var filter = new TransferFilter
        {
            ReceiverIdentityPublicKey = ByteString.CopyFrom(wallet.Signer.IdentityPublicKey),
            Network = protoNetwork,
        };
        var pendingResponse = await coordinatorClient.query_pending_transfersAsync(
            filter, headers, cancellationToken: ct);

        var claimed = new List<SparkTransfer>();
        if (pendingResponse.Transfers.Count == 0)
        {
            return claimed;
        }

        // Step 2: Get SO operator info
        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct);
        var soOperators = soListResponse.SigningOperators;
        var soCount = (uint)soOperators.Count;

        // Process each pending transfer
        foreach (var transfer in pendingResponse.Transfers)
        {
            var transferLeaves = transfer.Leaves.ToList();
            if (transferLeaves.Count == 0)
            {
                continue;
            }

            try
            {
                var result = await ClaimSingleTransferAsync(
                    wallet, coordinatorClient, headers, networkStr,
                    soOperators, soCount, transfer, transferLeaves, ct).ConfigureAwait(false);
                claimed.Add(result);
            }
            catch
            {
                // Skip transfers that fail (e.g. corrupted state from previous attempts)
            }
        }

        return claimed;
    }

    internal static async Task<SparkTransfer> ClaimSingleTransferAsync(
        SparkWallet wallet,
        SparkService.SparkServiceClient coordinatorClient,
        Grpc.Core.Metadata headers,
        string networkStr,
        Google.Protobuf.Collections.MapField<string, SigningOperatorInfo> soOperators,
        uint soCount,
        Transfer transfer,
        List<TransferLeaf> transferLeaves,
        CancellationToken ct)
    {
        // Step 3: Get signing commitments (Count=3: cpfp, direct, directFromCpfp)
        var commitmentsRequest = new GetSigningCommitmentsRequest
        {
            Count = 3,
            NodeIdCount = (uint)transferLeaves.Count,
        };
        var commitmentsResponse = await coordinatorClient.get_signing_commitmentsAsync(
            commitmentsRequest, headers, cancellationToken: ct);
        var allCommitments = commitmentsResponse.SigningCommitments.ToList();

        // Step 4: Process each leaf — decrypt tweak, VSS split, construct + sign refund txs
        var cpfpRefundJobs = new List<UserSignedTxSigningJob>();
        var directRefundJobs = new List<UserSignedTxSigningJob>();
        var directFromCpfpRefundJobs = new List<UserSignedTxSigningJob>();

        var perSoTweaks = new Dictionary<string, ClaimLeafKeyTweaks>();
        foreach (var (soId, _) in soOperators)
        {
            perSoTweaks[soId] = new ClaimLeafKeyTweaks();
        }

        var threshold = Math.Max(2, (soCount + 2) / 2);

        for (int i = 0; i < transferLeaves.Count; i++)
        {
            var transferLeaf = transferLeaves[i];
            var node = transferLeaf.Leaf;

            // ECIES decrypt secret_cipher → sender's intermediate signing key
            var secretCipher = transferLeaf.SecretCipher.ToByteArray();
            var oldSigningKey = SparkFrostMethods.DecryptEcies(secretCipher, wallet.Signer.IdentityPrivateKey);

            // Derive new signing key for this leaf
            var newSigningKey = wallet.Signer.DeriveLeafSigningKey(node.Id);
            var newSigningPubKey = SparkFrostMethods.GetPublicKeyBytes(newSigningKey, compressed: true);
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();

            // Compute key tweak = oldKey - newKey (mod secp256k1 order)
            var keyTweak = SubtractPrivateKeys(oldSigningKey, newSigningKey);

            // VSS split the tweak among SOs
            var vssShares = SparkFrostMethods.SplitSecretWithProofsUniffi(keyTweak, threshold: threshold, numShares: soCount);

            // Extract the refund sequence from the sender's intermediate refund tx
            var intermediateRefundBytes = transferLeaf.IntermediateRefundTx.ToByteArray();
            var nodeRefundBytes = node.RefundTx.ToByteArray();
            uint currentSequence;
            if (intermediateRefundBytes.Length > 0)
            {
                currentSequence = ParseInputSequence(intermediateRefundBytes);
            }
            else if (nodeRefundBytes.Length > 0)
            {
                currentSequence = ParseInputSequence(nodeRefundBytes);
            }
            else
            {
                currentSequence = ParseInputSequence(node.NodeTx.ToByteArray());
            }

            // Round DOWN to nearest TimeLockInterval for claim sequence
            var currentTimelock = currentSequence & 0xFFFF;
            var bit30 = currentSequence & (1u << 30);
            var remainder = currentTimelock % TimeLockInterval;
            if (remainder != 0)
            {
                currentTimelock -= remainder;
            }

            var claimSeq = bit30 | currentTimelock;
            var claimDirectSeq = bit30 | (currentTimelock + DirectTimelockOffset);

            // Construct refund tx trio (cpfp, direct, directFromCpfp)
            var cpfpNodeTx = node.NodeTx.ToByteArray();
            var directNodeTx = node.DirectTx.Length > 0 ? node.DirectTx.ToByteArray() : null;

            var refundTrio = SparkFrostMethods.ConstructRefundTxTrio(
                cpfpNodeTx: cpfpNodeTx,
                directNodeTx: directNodeTx,
                vout: 0,
                receivingPubkey: newSigningPubKey,
                network: networkStr,
                sequence: claimSeq,
                directSequence: claimDirectSeq,
                feeSats: 0);

            // Commitments are interleaved: [leaf0_r0, leaf1_r0, ..., leaf0_r1, leaf1_r1, ...]
            var cpfpCommitments = allCommitments[i].SigningNonceCommitments;
            var directCommitments = allCommitments[i + transferLeaves.Count].SigningNonceCommitments;
            var directFromCpfpCommitments = allCommitments[i + 2 * transferLeaves.Count].SigningNonceCommitments;

            // FROST sign cpfp refund
            cpfpRefundJobs.Add(FrostSigningHelper.BuildSigningJob(
                node.Id, newSigningKey, verifyingKey,
                refundTrio.@cpfpRefund.@tx, refundTrio.@cpfpRefund.@sighash, cpfpCommitments));

            // FROST sign direct refund (if direct tx exists)
            if (refundTrio.@directRefund != null)
            {
                directRefundJobs.Add(FrostSigningHelper.BuildSigningJob(
                    node.Id, newSigningKey, verifyingKey,
                    refundTrio.@directRefund.@tx, refundTrio.@directRefund.@sighash, directCommitments));
            }

            // FROST sign direct-from-cpfp refund
            directFromCpfpRefundJobs.Add(FrostSigningHelper.BuildSigningJob(
                node.Id, newSigningKey, verifyingKey,
                refundTrio.@directFromCpfpRefund.@tx, refundTrio.@directFromCpfpRefund.@sighash,
                directFromCpfpCommitments));

            // Build pubkey shares tweak map: SO_id → pubkey(that SO's VSS share)
            var pubkeySharesTweak = new Dictionary<string, ByteString>();
            foreach (var (soId2, soInfo2) in soOperators)
            {
                var matchedShare = vssShares.First(s => s.@index == soInfo2.Index + 1);
                pubkeySharesTweak[soId2] = ByteString.CopyFrom(
                    SparkFrostMethods.GetPublicKeyBytes(matchedShare.@share, compressed: true));
            }

            // Build per-SO key tweak entries
            foreach (var (soId, soInfo) in soOperators)
            {
                var share = vssShares.First(s => s.@index == soInfo.Index + 1);
                var leafTweak = new ClaimLeafKeyTweak
                {
                    LeafId = node.Id,
                    SecretShareTweak = new SecretShare { SecretShare_ = ByteString.CopyFrom(share.@share) },
                };
                foreach (var proof in share.@proofs)
                {
                    leafTweak.SecretShareTweak.Proofs.Add(ByteString.CopyFrom(proof));
                }

                foreach (var (k, v) in pubkeySharesTweak)
                {
                    leafTweak.PubkeySharesTweak.Add(k, v);
                }

                perSoTweaks[soId].LeavesToReceive.Add(leafTweak);
            }
        }

        // Step 5: ECIES encrypt per-SO key tweak packages (to config identity keys, NOT gRPC response keys)
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

        // Step 6: Sign the key tweak package (BIP-340 tagged hash)
        var transferIdBytes = Convert.FromHexString(transfer.Id.Replace("-", ""));
        var packageHash = SparkTaggedHash.Create("spark", "claim", "signing payload")
            .AddBytes(transferIdBytes)
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        var packageSignature = wallet.Signer.SignWithIdentityKey(packageHash);

        // Step 7: Build ClaimPackage
        var claimPackage = new ClaimPackage
        {
            UserSignature = ByteString.CopyFrom(packageSignature),
            HashVariant = HashVariant.V2,
        };
        foreach (var job in cpfpRefundJobs)
        {
            claimPackage.LeavesToClaim.Add(job);
        }

        foreach (var job in directRefundJobs)
        {
            claimPackage.DirectLeavesToClaim.Add(job);
        }

        foreach (var job in directFromCpfpRefundJobs)
        {
            claimPackage.DirectFromCpfpLeavesToClaim.Add(job);
        }

        foreach (var (soId, cipher) in keyTweakPackage)
        {
            claimPackage.KeyTweakPackage.Add(soId, cipher);
        }

        // Step 8: Call claim_transfer
        var claimResponse = await coordinatorClient.claim_transferAsync(
            new ClaimTransferRequest
            {
                TransferId = transfer.Id,
                OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.Signer.IdentityPublicKey),
                ClaimPackage = claimPackage,
            },
            headers,
            cancellationToken: ct);

        var claimedTransfer = claimResponse.Transfer;
        return new SparkTransfer(
            Id: claimedTransfer.Id,
            SenderIdentityPublicKey: Convert.ToHexString(claimedTransfer.SenderIdentityPublicKey.ToByteArray()),
            ReceiverIdentityPublicKey: Convert.ToHexString(claimedTransfer.ReceiverIdentityPublicKey.ToByteArray()),
            TotalValueSats: (long)claimedTransfer.TotalValue,
            Status: claimedTransfer.Status.ToString(),
            CreatedAt: claimedTransfer.CreatedTime.ToDateTimeOffset());
    }

    /// <summary>
    /// Extract the nSequence from the leaf's refund transaction to determine the current timelock.
    /// Falls back to the node_tx input sequence if refund_tx is empty.
    /// </summary>
    internal static uint ExtractRefundSequence(TreeNode node)
    {
        var txBytes = node.RefundTx.Length > 0
            ? node.RefundTx.ToByteArray()
            : node.NodeTx.ToByteArray();
        return ParseInputSequence(txBytes);
    }

    /// <summary>
    /// Parse the raw nSequence from the first input in raw Bitcoin transaction bytes.
    /// Returns the full 32-bit sequence value (including bit 30 for relative timelock type).
    /// Callers extract lower 16 bits for timelock value and preserve upper bits as needed.
    /// </summary>
    internal static uint ParseInputSequence(byte[] txBytes)
    {
        int offset = 4; // skip version

        // Check for segwit marker (0x00 followed by 0x01)
        if (txBytes[offset] == 0x00 && txBytes[offset + 1] == 0x01)
        {
            offset += 2; // skip marker + flag
        }

        // Read input count (varint) — we only need the first input
        offset += ReadVarIntSize(txBytes, offset);

        // Skip prev_hash (32 bytes) + prev_index (4 bytes)
        offset += 32 + 4;

        // Read script length (varint) and skip script
        var (scriptLen, varIntBytes) = ReadVarInt(txBytes, offset);
        offset += varIntBytes + (int)scriptLen;

        // Read raw nSequence (4 bytes LE) — preserve all bits including bit 30
        return BitConverter.ToUInt32(txBytes, offset);
    }

    private static int ReadVarIntSize(byte[] data, int offset)
    {
        return data[offset] switch
        {
            < 0xFD => 1,
            0xFD => 3,
            0xFE => 5,
            _ => 9,
        };
    }

    private static (long value, int bytesRead) ReadVarInt(byte[] data, int offset)
    {
        var first = data[offset];
        return first switch
        {
            < 0xFD => (first, 1),
            0xFD => (BitConverter.ToUInt16(data, offset + 1), 3),
            0xFE => (BitConverter.ToUInt32(data, offset + 1), 5),
            _ => ((long)BitConverter.ToUInt64(data, offset + 1), 9),
        };
    }

    private static readonly BigInteger Secp256k1Order = BigInteger.Parse(
        "0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141",
        System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Compute (a - b) mod secp256k1 order, returning a 32-byte big-endian scalar.
    /// </summary>
    internal static byte[] SubtractPrivateKeys(byte[] a, byte[] b)
    {
        var aInt = new BigInteger(a, isUnsigned: true, isBigEndian: true);
        var bInt = new BigInteger(b, isUnsigned: true, isBigEndian: true);
        var result = ((aInt - bInt) % Secp256k1Order + Secp256k1Order) % Secp256k1Order;

        var bytes = result.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == 32)
        {
            return bytes;
        }

        var padded = new byte[32];
        bytes.CopyTo(padded, 32 - bytes.Length);
        return padded;
    }
}
