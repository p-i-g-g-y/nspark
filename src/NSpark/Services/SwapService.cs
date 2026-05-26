using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NSpark.GraphQL;
using NSpark.Models;
using NSpark.Proto;
using uniffi.spark_frost;

namespace NSpark.Services;

/// <summary>
/// Leaf swap service — splits existing leaves into target denominations via SSP.
/// Ported from Swift SDK SwapService.swift.
/// </summary>
public static class SwapService
{
    private const uint TimeLockInterval = 100;
    private const uint DirectTimelockOffset = 50;

    /// <summary>
    /// Select leaves that exactly cover the target amount. If no exact match exists,
    /// triggers a leaf swap via SSP to split leaves into the required denominations.
    /// </summary>
    public static async Task<IReadOnlyList<SparkLeaf>> SelectLeavesWithSwapAsync(
        this SparkWallet wallet,
        long amountSats,
        CancellationToken ct = default)
    {
        var leaves = await wallet.GetLeavesAsync(ct).ConfigureAwait(false);

        // First try exact selection (leaves that sum exactly to the target)
        var exact = TryExactSelection(leaves, amountSats);
        if (exact != null)
        {
            return exact;
        }

        // No exact match — swap leaves via SSP to get right denominations
        var newLeaves = await wallet.RequestLeavesSwapAsync([amountSats], ct).ConfigureAwait(false);

        // Retry selection with new leaves — swap should have created exact denominations
        exact = TryExactSelection(newLeaves, amountSats);
        if (exact != null)
        {
            return exact;
        }

        // Swap didn't produce exact match — should not happen, but don't overspend
        throw new InvalidOperationException(
            $"Leaf swap did not produce exact denomination for {amountSats} sats. Available: {string.Join(", ", newLeaves.Where(l => l.Status == "AVAILABLE").Select(l => l.ValueSats))} sats.");
    }

    /// <summary>
    /// Try to find leaves that exactly sum to the target amount.
    /// Returns null if no exact combination found.
    /// </summary>
    internal static IReadOnlyList<SparkLeaf>? TryExactSelection(IReadOnlyList<SparkLeaf> leaves, long amountSats)
    {
        var available = leaves.Where(l => l.Status == "AVAILABLE").ToList();

        // Check if single leaf matches exactly
        var single = available.FirstOrDefault(l => l.ValueSats == amountSats);
        if (single != null)
        {
            return [single];
        }

        // Greedy descending: take each leaf if it fits in the remaining amount.
        // For power-of-2 denominations this always finds an exact match if one exists.
        var sorted = available.OrderByDescending(l => l.ValueSats).ToList();
        var selected = new List<SparkLeaf>();
        var remaining = amountSats;
        foreach (var leaf in sorted)
        {
            if (leaf.ValueSats <= remaining)
            {
                selected.Add(leaf);
                remaining -= leaf.ValueSats;
                if (remaining == 0)
                {
                    return selected;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Request leaf swap via SSP: splits existing leaves into target denominations.
    /// Returns newly claimed leaves after the swap.
    /// </summary>
    public static async Task<IReadOnlyList<SparkLeaf>> RequestLeavesSwapAsync(
        this SparkWallet wallet,
        long[] targetAmounts,
        CancellationToken ct = default)
    {
        var totalTarget = targetAmounts.Sum();
        var leaves = await wallet.GetLeavesAsync(ct).ConfigureAwait(false);

        // Select leaves covering the total target (smallest first)
        var sorted = leaves
            .Where(l => l.Status == "AVAILABLE")
            .OrderBy(l => l.ValueSats)
            .ToList();
        var selected = new List<SparkLeaf>();
        long total = 0;
        foreach (var leaf in sorted)
        {
            if (total < totalTarget)
            {
                selected.Add(leaf);
                total += leaf.ValueSats;
            }
        }

        if (total < totalTarget)
        {
            throw new InvalidOperationException(
                $"Insufficient balance for swap: need {totalTarget} sats, have {total} sats.");
        }

        return await wallet.ProcessSwapBatchAsync(selected, targetAmounts, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Process a single batch of leaves for swapping.
    /// 1. Send swap transfer to coordinator (with adaptor key)
    /// 2. Aggregate FROST signatures with adaptor pubkey
    /// 3. Call SSP request_swap mutation
    /// 4. Claim inbound transfer from SSP
    /// </summary>
    internal static async Task<IReadOnlyList<SparkLeaf>> ProcessSwapBatchAsync(
        this SparkWallet wallet,
        IReadOnlyList<SparkLeaf> leaves,
        long[] targetAmounts,
        CancellationToken ct = default)
    {
        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);
        var receiverPubKey = Convert.FromHexString(wallet.Client.Options.SspIdentityPublicKeyHex);

        // Get SO operator list
        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct);
        var soOperators = soListResponse.SigningOperators;
        var soCount = (uint)soOperators.Count;
        var threshold = (uint)Math.Max(2, (soCount + 2) / 2);

        // Generate adaptor keypair via the signer — the adaptor private key never leaves the signer.
        var adaptorKey = await wallet.Signer.GenerateAdaptorKeyAsync(ct).ConfigureAwait(false);
        var adaptorPubKey = adaptorKey.PublicKey;

        var transferId = Guid.NewGuid().ToString();
        var expiryTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
            DateTimeOffset.UtcNow.AddDays(16));

        // Get signing commitments (count=3 for cpfp, direct, directFromCpfp)
        var leafIds = leaves.Select(l => l.Id).ToList();
        var commitmentsRequest = new GetSigningCommitmentsRequest { Count = 3 };
        commitmentsRequest.NodeIds.AddRange(leafIds);
        var commitmentsResponse = await coordinatorClient.get_signing_commitmentsAsync(
            commitmentsRequest, headers, cancellationToken: ct);
        var allCommitments = commitmentsResponse.SigningCommitments.ToList();

        // Build transfer package (only cpfp refund jobs — direct/directFromCpfp cleared for swaps)
        var cpfpRefundJobs = new List<UserSignedTxSigningJob>();
        var perSoTweaks = new Dictionary<string, SendLeafKeyTweaks>();
        foreach (var (soId, _) in soOperators)
        {
            perSoTweaks[soId] = new SendLeafKeyTweaks();
        }

        // Store signing info for FROST aggregation after SO response
        var leafSigningInfos = new List<(string LeafId, NSpark.Signer.SigningCommitment SelfCommitment, byte[] Sighash)>();

        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            var node = leaf.Node;
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();

            var cpfpCommitments = allCommitments[i].SigningNonceCommitments;

            // Tweak shares via the signer
            var tweak = await wallet.Signer.ComputeLeafTweakSharesAsync(
                leaf.Id, receiverPubKey, threshold, soCount, ct).ConfigureAwait(false);
            var secretCipher = tweak.SecretCipher;

            // Compute sequences
            var refundTxBytes = node.RefundTx.Length > 0
                ? node.RefundTx.ToByteArray()
                : node.NodeTx.ToByteArray();
            var currentSequence = ClaimService.ParseInputSequence(refundTxBytes);
            var currentTimelock = currentSequence & 0xFFFF;
            var bit30 = currentSequence & (1u << 30);
            var nextTimelock = currentTimelock - TimeLockInterval;
            var cpfpSequence = bit30 | nextTimelock;
            var directSequence = bit30 | (nextTimelock + DirectTimelockOffset);

            // Build CPFP refund tx
            var nodeTxBytes = node.NodeTx.ToByteArray();
            var directNodeTx = node.DirectTx.Length > 0 ? node.DirectTx.ToByteArray() : null;
            var refundTrio = SparkFrostMethods.ConstructRefundTxTrio(
                cpfpNodeTx: nodeTxBytes,
                directNodeTx: directNodeTx,
                vout: 0,
                receivingPubkey: receiverPubKey,
                network: networkStr,
                sequence: cpfpSequence,
                directSequence: directSequence,
                // Only the cpfp branch is submitted for swaps today, but value mismatch
                // would still trip the SO check.
                feeSats: SparkConstants.DefaultRefundFeeSats);

            // Build signing job with adaptor key (only cpfp for swap)
            var (job, selfCommitment, sighash) = await FrostSigningHelper.BuildSigningJobWithAdaptorAsync(
                wallet.Signer, leaf.Id, verifyingKey,
                refundTrio.@cpfpRefund.@tx, refundTrio.@cpfpRefund.@sighash,
                cpfpCommitments, adaptorPubKey, ct)
                .ConfigureAwait(false);
            cpfpRefundJobs.Add(job);
            leafSigningInfos.Add((leaf.Id, selfCommitment, sighash));

            // Compact signature: SHA256(leaf_id || transfer_id || secret_cipher)
            var sigPayload = Encoding.UTF8.GetBytes(leaf.Id + transferId);
            sigPayload = [.. sigPayload, .. secretCipher];
            var tweakSig = await wallet.Signer.SignCompactWithIdentityKeyAsync(
                SHA256.HashData(sigPayload), ct).ConfigureAwait(false);

            // Build pubkey shares tweak map
            var pubkeySharesTweak = new Dictionary<string, ByteString>();
            foreach (var (soId2, soInfo2) in soOperators)
            {
                var matchedShare = tweak.Shares.First(s => s.Index == soInfo2.Index + 1);
                pubkeySharesTweak[soId2] = ByteString.CopyFrom(
                    SparkFrostMethods.GetPublicKeyBytes(matchedShare.Share, compressed: true));
            }

            // Build per-SO key tweak entries
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

        // Encrypt key tweak packages
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

        // Sign the transfer package
        var transferIdBytes = Convert.FromHexString(transferId.Replace("-", ""));
        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(transferIdBytes)
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        var packageSignature = await wallet.Signer.SignWithIdentityKeyAsync(packageHash, ct).ConfigureAwait(false);

        // Build TransferPackage (direct/directFromCpfp cleared for swap)
        var transferPackage = new TransferPackage
        {
            UserSignature = ByteString.CopyFrom(packageSignature),
            HashVariant = HashVariant.V2,
        };
        foreach (var job in cpfpRefundJobs)
        {
            transferPackage.LeavesToSend.Add(job);
        }
        // Direct and directFromCpfp are intentionally empty for swap
        foreach (var (soId, cipher) in keyTweakPackage)
        {
            transferPackage.KeyTweakPackage.Add(soId, cipher);
        }

        // Send swap transfer to coordinator
        var swapRequest = new InitiateSwapPrimaryTransferRequest
        {
            Transfer = new StartTransferRequest
            {
                TransferId = transferId,
                OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                ReceiverIdentityPublicKey = ByteString.CopyFrom(receiverPubKey),
                ExpiryTime = expiryTime,
                TransferPackage = transferPackage,
            },
            AdaptorPublicKeys = new AdaptorPublicKeyPackage
            {
                AdaptorPublicKey = ByteString.CopyFrom(adaptorPubKey),
            },
        };

        var swapResponse = await coordinatorClient.initiate_swap_primary_transferAsync(
            swapRequest, headers, cancellationToken: ct);

        if (swapResponse.Transfer == null)
        {
            throw new InvalidOperationException("No transfer in swap response.");
        }

        // Aggregate FROST signatures with adaptor pubkey for each leaf
        var adaptorSignatures = new Dictionary<string, byte[]>();
        foreach (var signingResult in swapResponse.SigningResults)
        {
            var info = leafSigningInfos.FirstOrDefault(x => x.LeafId == signingResult.LeafId);
            if (info.LeafId == null)
            {
                continue;
            }

            var job = cpfpRefundJobs.FirstOrDefault(j => j.LeafId == signingResult.LeafId);
            if (job == null)
            {
                continue;
            }

            var aggregated = FrostSigningHelper.AggregateFrostSignature(
                sighash: info.Sighash,
                selfCommitment: info.SelfCommitment,
                selfSignature: job.UserSignature.ToByteArray(),
                selfPublicKey: job.SigningPublicKey.ToByteArray(),
                verifyingKey: signingResult.VerifyingKey.ToByteArray(),
                signingResult: signingResult.RefundTxSigningResult,
                adaptorPublicKey: adaptorPubKey);

            adaptorSignatures[signingResult.LeafId] = aggregated;
        }

        // Build user leaves for SSP request_swap mutation
        var userLeaves = new List<Dictionary<string, string>>();
        foreach (var signingResult in swapResponse.SigningResults)
        {
            if (!adaptorSignatures.TryGetValue(signingResult.LeafId, out var adaptorSig))
            {
                continue;
            }

            var adaptorSigHex = Convert.ToHexString(adaptorSig).ToLowerInvariant();

            var transferLeaf = swapResponse.Transfer.Leaves
                .FirstOrDefault(l => l.Leaf.Id == signingResult.LeafId);

            userLeaves.Add(new Dictionary<string, string>
            {
                ["leaf_id"] = signingResult.LeafId,
                ["raw_unsigned_refund_transaction"] = Convert.ToHexString(
                    transferLeaf?.IntermediateRefundTx.ToByteArray() ?? []).ToLowerInvariant(),
                ["direct_raw_unsigned_refund_transaction"] = Convert.ToHexString(
                    transferLeaf?.IntermediateDirectRefundTx.ToByteArray() ?? []).ToLowerInvariant(),
                ["direct_from_cpfp_raw_unsigned_refund_transaction"] = Convert.ToHexString(
                    transferLeaf?.IntermediateDirectFromCpfpRefundTx.ToByteArray() ?? []).ToLowerInvariant(),
                ["adaptor_added_signature"] = adaptorSigHex,
                ["direct_adaptor_added_signature"] = adaptorSigHex,
                ["direct_from_cpfp_adaptor_added_signature"] = adaptorSigHex,
            });
        }

        // Call SSP request_swap mutation
        var totalAmountSats = leaves.Sum(l => l.ValueSats);
        var variables = new Dictionary<string, object?>
        {
            ["adaptor_pubkey"] = Convert.ToHexString(adaptorPubKey).ToLowerInvariant(),
            ["total_amount_sats"] = totalAmountSats,
            ["target_amount_sats"] = targetAmounts,
            ["fee_sats"] = 0L,
            ["user_leaves"] = userLeaves,
            ["user_outbound_transfer_external_id"] = swapResponse.Transfer.Id,
        };

        var sspResponse = await wallet.SspClient.ExecuteAsync<RequestSwapResponse>(
            Mutations.RequestSwap, variables, ct).ConfigureAwait(false);

        var swapStatus = sspResponse.RequestSwap.Request.Status;
        if (swapStatus == "FAILED")
        {
            throw new InvalidOperationException("Leaf swap request failed.");
        }

        var inboundSparkId = sspResponse.RequestSwap.Request.InboundTransfer?.SparkId
            ?? throw new InvalidOperationException("No inbound transfer in swap response.");

        // Query the inbound transfer and claim it
        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Proto.Network.Mainnet : Proto.Network.Regtest;
        var filter = new TransferFilter
        {
            ReceiverIdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            Network = protoNetwork,
        };
        filter.TransferIds.Add(inboundSparkId);

        var queryResponse = await coordinatorClient.query_pending_transfersAsync(
            filter, headers, cancellationToken: ct);

        var inboundTransfer = queryResponse.Transfers.FirstOrDefault()
            ?? throw new InvalidOperationException($"Inbound swap transfer not found: {inboundSparkId}");

        // Claim the inbound transfer
        var soListResponse2 = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct);

        await ClaimService.ClaimSingleTransferAsync(
            wallet, coordinatorClient, headers, networkStr,
            soListResponse2.SigningOperators, (uint)soListResponse2.SigningOperators.Count,
            inboundTransfer, inboundTransfer.Leaves.ToList(), ct).ConfigureAwait(false);

        // Return the new leaves
        return await wallet.GetLeavesAsync(ct).ConfigureAwait(false);
    }
}
