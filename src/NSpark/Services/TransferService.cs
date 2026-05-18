using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;
using uniffi.spark_frost;

namespace NSpark.Services;

/// <inheritdoc/>
public static class TransferService
{
    private const uint TimeLockInterval = 100;
    private const uint DirectTimelockOffset = 50;
    /// <summary>
    /// Send a Spark transfer to another wallet's identity public key.
    /// Uses the TransferPackage flow (start_transfer_v2) with FROST threshold signing.
    /// </summary>
    public static async Task<SparkTransfer> SendAsync(
        this SparkWallet wallet,
        byte[] receiverIdentityPublicKey,
        long amountSats,
        string? transferId = null,
        CancellationToken ct = default)
    {
        // Step 1: Select leaves to cover amount (exact match or swap)
        var selectedLeaves = await wallet.SelectLeavesWithSwapAsync(amountSats, ct).ConfigureAwait(false);

        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);

        // Step 2: Get SO operator info (identifiers + public keys)
        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(),
            headers,
            cancellationToken: ct);
        var soOperators = soListResponse.SigningOperators;
        var soCount = (uint)soOperators.Count;

        // Step 3: Get SO signing commitments for selected leaves
        // Count=3: cpfp, direct, directFromCpfp refund
        var leafIds = selectedLeaves.Select(l => l.Id).ToList();
        var commitmentsRequest = new GetSigningCommitmentsRequest { Count = 3 };
        commitmentsRequest.NodeIds.AddRange(leafIds);
        var commitmentsResponse = await coordinatorClient.get_signing_commitmentsAsync(
            commitmentsRequest, headers, cancellationToken: ct);

        // Commitments are interleaved: [leaf0_r0, leaf1_r0, ..., leaf0_r1, leaf1_r1, ...]
        var allCommitments = commitmentsResponse.SigningCommitments.ToList();

        // Step 4: For each leaf — derive key, VSS split tweak, construct + sign refund txs
        var cpfpRefundJobs = new List<UserSignedTxSigningJob>();
        var directRefundJobs = new List<UserSignedTxSigningJob>();
        var directFromCpfpRefundJobs = new List<UserSignedTxSigningJob>();
        var perSoTweaks = new Dictionary<string, SendLeafKeyTweaks>();
        foreach (var (soId, _) in soOperators)
        {
            perSoTweaks[soId] = new SendLeafKeyTweaks();
        }

        transferId ??= Guid.NewGuid().ToString();

        for (int i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            var node = leaf.Node;
            var signingKey = wallet.Signer.DeriveLeafSigningKey(leaf.Id);
            var signingPubKey = SparkFrostMethods.GetPublicKeyBytes(signingKey, compressed: true);
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();

            // Compute decremented sequence from leaf's current refund tx
            var nodeTxBytes = node.NodeTx.ToByteArray();
            var directNodeTx = node.DirectTx.Length > 0 ? node.DirectTx.ToByteArray() : null;
            var refundTxBytes = node.RefundTx.Length > 0
                ? node.RefundTx.ToByteArray()
                : nodeTxBytes;
            var currentSequence = ClaimService.ParseInputSequence(refundTxBytes);
            var currentTimelock = currentSequence & 0xFFFF;
            var bit30 = currentSequence & (1u << 30);
            var nextTimelock = currentTimelock - TimeLockInterval;
            var normalSeq = bit30 | nextTimelock;
            var normalDirectSeq = bit30 | (nextTimelock + DirectTimelockOffset);

            // Commitments are interleaved: [leaf0_cpfp, leaf1_cpfp, ..., leaf0_direct, leaf1_direct, ..., leaf0_dfcpfp, leaf1_dfcpfp, ...]
            var cpfpCommitments = allCommitments[i].SigningNonceCommitments;
            var directCommitments = allCommitments[i + selectedLeaves.Count].SigningNonceCommitments;
            var directFromCpfpCommitments = allCommitments[i + 2 * selectedLeaves.Count].SigningNonceCommitments;

            // Key tweak = oldSigningKey - newRandomKey (matches JS SDK subtractSplitAndEncrypt)
            var oldSigningKey = signingKey; // already derived above
            var newRandomKey = SparkFrostMethods.RandomSecretKeyBytes();
            var keyTweak = ClaimService.SubtractPrivateKeys(oldSigningKey, newRandomKey);
            var vssShares = SparkFrostMethods.SplitSecretWithProofsUniffi(keyTweak, threshold: Math.Max(2, (soCount + 2) / 2), numShares: soCount);

            // Encrypt the NEW key (intermediate signing key), NOT the tweak
            var secretCipher = SparkFrostMethods.EncryptEcies(newRandomKey, receiverIdentityPublicKey);

            // Construct refund tx trio (cpfp, direct, directFromCpfp) with decremented timelock
            // receivingPubkey = receiver's identity pubkey (server validates this)
            var refundTrio = SparkFrostMethods.ConstructRefundTxTrio(
                cpfpNodeTx: nodeTxBytes,
                directNodeTx: directNodeTx,
                vout: 0,
                receivingPubkey: receiverIdentityPublicKey,
                network: networkStr,
                sequence: normalSeq,
                directSequence: normalDirectSeq,
                feeSats: SparkConstants.DefaultRefundFeeSats);

            // FROST sign cpfp refund
            cpfpRefundJobs.Add(FrostSigningHelper.BuildSigningJob(
                leaf.Id, signingKey, verifyingKey,
                refundTrio.@cpfpRefund.@tx, refundTrio.@cpfpRefund.@sighash, cpfpCommitments));

            // FROST sign direct refund (if direct tx exists)
            if (refundTrio.@directRefund != null)
            {
                directRefundJobs.Add(FrostSigningHelper.BuildSigningJob(
                    leaf.Id, signingKey, verifyingKey,
                    refundTrio.@directRefund.@tx, refundTrio.@directRefund.@sighash, directCommitments));
            }

            // FROST sign direct-from-cpfp refund
            directFromCpfpRefundJobs.Add(FrostSigningHelper.BuildSigningJob(
                leaf.Id, signingKey, verifyingKey,
                refundTrio.@directFromCpfpRefund.@tx, refundTrio.@directFromCpfpRefund.@sighash,
                directFromCpfpCommitments));

            // Compact signature: SHA256(leaf_id || transfer_id || secret_cipher)
            var sigPayload = Encoding.UTF8.GetBytes(leaf.Id + transferId);
            sigPayload = [.. sigPayload, .. secretCipher];
            var tweakSig = wallet.Signer.SignCompactWithIdentityKey(SHA256.HashData(sigPayload));

            // Build pubkey shares tweak map: SO_id → pubkey(that SO's VSS share)
            // Match shares to operators by index: operator.Index=N → share.index=N+1
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

                // Same pubkey shares tweak map for every SO
                foreach (var (k, v) in pubkeySharesTweak)
                {
                    leafTweak.PubkeySharesTweak.Add(k, v);
                }

                perSoTweaks[soId].LeavesToSend.Add(leafTweak);
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

        // Step 6: Sign the key tweak package (BIP-340 tagged hash with domain "spark/transfer/signing payload")
        var transferIdBytes = Convert.FromHexString(transferId.Replace("-", ""));
        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(transferIdBytes)
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        var packageSignature = wallet.Signer.SignWithIdentityKey(packageHash);

        // Step 7: Assemble TransferPackage and submit
        var transferPackage = new TransferPackage
        {
            UserSignature = ByteString.CopyFrom(packageSignature),
            HashVariant = HashVariant.V2,
        };
        foreach (var job in cpfpRefundJobs)
        {
            transferPackage.LeavesToSend.Add(job);
        }

        foreach (var job in directRefundJobs)
        {
            transferPackage.DirectLeavesToSend.Add(job);
        }

        foreach (var job in directFromCpfpRefundJobs)
        {
            transferPackage.DirectFromCpfpLeavesToSend.Add(job);
        }

        foreach (var (soId, cipher) in keyTweakPackage)
        {
            transferPackage.KeyTweakPackage.Add(soId, cipher);
        }

        var response = await coordinatorClient.start_transfer_v2Async(
            new StartTransferRequest
            {
                TransferId = transferId,
                OwnerIdentityPublicKey = ByteString.CopyFrom(wallet.Signer.IdentityPublicKey),
                ReceiverIdentityPublicKey = ByteString.CopyFrom(receiverIdentityPublicKey),
                ExpiryTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    DateTimeOffset.UtcNow.AddMinutes(10)),
                TransferPackage = transferPackage,
            },
            headers,
            cancellationToken: ct);

        var transfer = response.Transfer;
        return new SparkTransfer(
            Id: transfer.Id,
            SenderIdentityPublicKey: Convert.ToHexString(transfer.SenderIdentityPublicKey.ToByteArray()),
            ReceiverIdentityPublicKey: Convert.ToHexString(transfer.ReceiverIdentityPublicKey.ToByteArray()),
            TotalValueSats: (long)transfer.TotalValue,
            Status: transfer.Status.ToString(),
            CreatedAt: transfer.CreatedTime.ToDateTimeOffset());
    }

    /// <summary>
    /// Get a single transfer by ID. Returns null if not found.
    /// </summary>
    public static async Task<SparkTransfer?> GetTransferAsync(
        this SparkWallet wallet,
        string transferId,
        CancellationToken ct = default)
    {
        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);

        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Proto.Network.Mainnet : Proto.Network.Regtest;

        var filter = new Proto.TransferFilter
        {
            SenderOrReceiverIdentityPublicKey = ByteString.CopyFrom(wallet.Signer.IdentityPublicKey),
            Network = protoNetwork,
        };
        filter.TransferIds.Add(transferId);

        var response = await coordinatorClient.query_all_transfersAsync(
            filter, headers, cancellationToken: ct);

        var transfer = response.Transfers.FirstOrDefault();
        return transfer == null ? null : MapTransfer(transfer);
    }

    /// <summary>
    /// Query transfers with pagination and optional time filters.
    /// </summary>
    public static async Task<TransferPage> GetTransfersAsync(
        this SparkWallet wallet,
        int limit = 100,
        long offset = 0,
        DateTimeOffset? createdAfter = null,
        DateTimeOffset? createdBefore = null,
        CancellationToken ct = default)
    {
        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);

        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Proto.Network.Mainnet : Proto.Network.Regtest;

        var filter = new Proto.TransferFilter
        {
            SenderOrReceiverIdentityPublicKey = ByteString.CopyFrom(wallet.Signer.IdentityPublicKey),
            Network = protoNetwork,
            Limit = limit,
            Offset = offset,
        };

        if (createdAfter.HasValue)
        {
            filter.CreatedAfter = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(createdAfter.Value);
        }
        else if (createdBefore.HasValue)
        {
            filter.CreatedBefore = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(createdBefore.Value);
        }

        var response = await coordinatorClient.query_all_transfersAsync(
            filter, headers, cancellationToken: ct);

        var transfers = response.Transfers.Select(MapTransfer).ToList();
        return new TransferPage(transfers, response.Offset);
    }

    private static SparkTransfer MapTransfer(Proto.Transfer t)
    {
        return new SparkTransfer(
            Id: t.Id,
            SenderIdentityPublicKey: Convert.ToHexString(t.SenderIdentityPublicKey.ToByteArray()).ToLowerInvariant(),
            ReceiverIdentityPublicKey: Convert.ToHexString(t.ReceiverIdentityPublicKey.ToByteArray()).ToLowerInvariant(),
            TotalValueSats: (long)t.TotalValue,
            Status: t.Status.ToString(),
            CreatedAt: t.CreatedTime.ToDateTimeOffset(),
            Type: t.Type.ToString());
    }

    internal static IReadOnlyList<SparkLeaf> SelectLeaves(IReadOnlyList<SparkLeaf> leaves, long amountSats)
    {
        var sorted = leaves
            .Where(l => l.Status == "AVAILABLE")
            .OrderByDescending(l => l.ValueSats)
            .ToList();
        var selected = new List<SparkLeaf>();
        long total = 0;
        foreach (var leaf in sorted)
        {
            selected.Add(leaf);
            total += leaf.ValueSats;
            if (total >= amountSats)
            {
                return selected;
            }
        }
        throw new InvalidOperationException(
            $"Insufficient balance: need {amountSats} sats, have {total} sats.");
    }
}
