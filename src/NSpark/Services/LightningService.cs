using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NSpark.GraphQL;
using NSpark.Models;
using NSpark.Proto;
using uniffi.spark_frost;

namespace NSpark.Services;

/// <inheritdoc/>
public static class LightningService
{
    private const uint HtlcSequence = 40;

    /// <summary>
    /// Create a Lightning invoice to receive a payment.
    /// Generates a preimage, requests the invoice from SSP, then splits
    /// and stores preimage shares with Signing Operators via FROST VSS.
    /// </summary>
    public static async Task<LightningInvoice> CreateLightningInvoiceAsync(
        this SparkWallet wallet,
        long amountSats,
        string? memo = null,
        int? expirySecs = null,
        byte[]? receiverIdentityPublicKey = null,
        byte[]? descriptionHash = null,
        CancellationToken ct = default)
    {
        if (descriptionHash is { Length: not 32 })
        {
            throw new ArgumentException("descriptionHash must be 32 bytes (SHA-256).", nameof(descriptionHash));
        }

        // Step 1: Generate preimage and compute payment hash
        var preimage = SparkFrostMethods.RandomSecretKeyBytes();
        var paymentHash = SHA256.HashData(preimage);
        var paymentHashHex = Convert.ToHexString(paymentHash).ToLowerInvariant();

        // Step 2: Request invoice from SSP via GraphQL
        var network = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? "MAINNET" : "REGTEST";

        var variables = new Dictionary<string, object?>
        {
            ["network"] = network,
            ["amount_sats"] = amountSats,
            ["payment_hash"] = paymentHashHex,
            ["expiry_secs"] = expirySecs,
            ["memo"] = memo,
            // BOLT11 description_hash (BIP-21 'h' field). Used by NIP-57 zaps where the
            // description is a signed zap-request JSON the recipient must commit to without
            // putting the full payload in the invoice.
            ["description_hash"] = descriptionHash != null
                ? Convert.ToHexString(descriptionHash).ToLowerInvariant()
                : null,
            ["receiver_identity_pubkey"] = receiverIdentityPublicKey != null
                ? Convert.ToHexString(receiverIdentityPublicKey).ToLowerInvariant()
                : null,
        };

        var response = await wallet.SspClient.ExecuteAsync<RequestLightningReceiveResponse>(
            Mutations.RequestLightningReceive, variables, ct).ConfigureAwait(false);

        var requestData = response.RequestLightningReceive.Request;
        var invoiceData = requestData.Invoice;

        // Step 3: Split preimage and store encrypted shares with SOs
        var soConfigs = wallet.Client.Options.SigningOperators;
        var numOperators = (uint)soConfigs.Length;
        var threshold = (uint)((numOperators + 2) / 2); // same as JS SDK

        var shares = SparkFrostMethods.SplitSecretWithProofsUniffi(
            preimage, threshold, numOperators);

        var coordinatorAddress = soConfigs[0].Address;
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);

        var storeRequest = new StorePreimageShareV2Request
        {
            PaymentHash = ByteString.CopyFrom(paymentHash),
            Threshold = threshold,
            InvoiceString = invoiceData.EncodedInvoice,
            UserIdentityPublicKey = ByteString.CopyFrom(receiverIdentityPublicKey ?? wallet.Signer.IdentityPublicKey),
        };

        // Match shares to operators by index (0-based), encrypt to each SO's identity key
        for (int i = 0; i < soConfigs.Length; i++)
        {
            var soConfig = soConfigs[i];
            var share = shares[i];

            var secretShare = new SecretShare
            {
                SecretShare_ = ByteString.CopyFrom(share.@share),
            };
            foreach (var proof in share.@proofs)
            {
                secretShare.Proofs.Add(ByteString.CopyFrom(proof));
            }

            var shareBytes = secretShare.ToByteArray();
            var identityPubKey = Convert.FromHexString(soConfig.IdentityPublicKeyHex);
            var encrypted = SparkFrostMethods.EncryptEcies(shareBytes, identityPubKey);
            storeRequest.EncryptedPreimageShares.Add(soConfig.Identifier, ByteString.CopyFrom(encrypted));
        }

        await coordinatorClient.store_preimage_share_v2Async(
            storeRequest, headers, cancellationToken: ct);

        return new LightningInvoice(
            PaymentRequest: invoiceData.EncodedInvoice,
            PaymentHash: paymentHashHex,
            AmountSats: amountSats,
            ExpiresAt: DateTimeOffset.Parse(invoiceData.ExpiresAt, System.Globalization.CultureInfo.InvariantCulture),
            RequestId: requestData.Id);
    }

    /// <summary>
    /// Query the status of a Lightning receive request by its SSP request ID.
    /// Returns the status string (e.g. "PENDING", "COMPLETED") or null if not found.
    /// </summary>
    public static async Task<string?> GetLightningReceiveRequestStatusAsync(
        this SparkWallet wallet,
        string requestId,
        CancellationToken ct = default)
    {
        var response = await wallet.SspClient.ExecuteAsync<GetUserRequestResponse>(
            Queries.GetUserRequest,
            new Dictionary<string, object> { ["request_id"] = requestId },
            ct).ConfigureAwait(false);

        var data = response.UserRequest;
        if (data is null)
        {
            return null;
        }
        // Only return a status for the receive variant — otherwise the caller (typically the
        // invoice poller) would treat a send-request status string as a receive status and
        // misinterpret it.
        return string.Equals(data.TypeName, "LightningReceiveRequest", StringComparison.Ordinal)
            ? data.ReceiveStatus
            : null;
    }

    /// <summary>
    /// Query the SSP for the status of an outgoing Lightning payment by its SSP request id (the
    /// string returned from <see cref="PayLightningInvoiceAsync"/>). Returns null if the SSP has
    /// no record of the request id, or if the request id resolves to a non-send request type
    /// (e.g., a Lightning receive request).
    /// <para>
    /// Known status values are listed on Spark's <c>LightningSendRequestStatus</c> enum and
    /// include (non-exhaustive): <c>CREATED</c>, <c>REQUEST_VALIDATED</c>,
    /// <c>LIGHTNING_PAYMENT_INITIATED</c>, <c>LIGHTNING_PAYMENT_SUCCEEDED</c>,
    /// <c>LIGHTNING_PAYMENT_FAILED</c>, <c>PREIMAGE_PROVIDED</c>, <c>PREIMAGE_PROVIDING_FAILED</c>,
    /// <c>TRANSFER_COMPLETED</c>, <c>TRANSFER_FAILED</c>, <c>USER_TRANSFER_VALIDATION_FAILED</c>,
    /// <c>USER_SWAP_RETURNED</c>, <c>USER_SWAP_RETURN_FAILED</c>. Treat any value not yet on this
    /// list as still in flight — Spark explicitly reserves the right to add new ones.
    /// </para>
    /// <para>
    /// While the payment is in flight <see cref="LightningSendStatus.FeeSats"/> and
    /// <see cref="LightningSendStatus.Preimage"/> are null; the fee is reported once the SSP
    /// finalises pricing and the preimage appears once status reaches one of the succeeded states.
    /// </para>
    /// </summary>
    public static async Task<LightningSendStatus?> GetLightningSendStatusAsync(
        this SparkWallet wallet,
        string requestId,
        CancellationToken ct = default)
    {
        var response = await wallet.SspClient.ExecuteAsync<GetUserRequestResponse>(
            Queries.GetUserRequest,
            new Dictionary<string, object> { ["request_id"] = requestId },
            ct).ConfigureAwait(false);

        var data = response.UserRequest;
        if (data is null)
        {
            return null;
        }

        // user_request is polymorphic. We only care about the LightningSendRequest variant; anyone
        // querying the wrong id type gets a null back.
        if (!string.Equals(data.TypeName, "LightningSendRequest", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrEmpty(data.SendStatus))
        {
            return null;
        }

        // Honour the SSP's CurrencyAmount unit discriminator — same dispatch as
        // GetLightningSendFeeEstimateAsync / WithdrawalService.GetFeeQuoteAsync.
        long? feeSats = data.SendFee is { } fee
            ? CurrencyAmountExtensions.ToSats(fee.OriginalValue, fee.OriginalUnit)
            : null;

        // The SSP's payment-hash field doesn't appear on LightningSendRequest, so we don't have it
        // here. Callers that need it must keep the (request_id ↔ payment_hash) mapping themselves.
        return new LightningSendStatus(
            PaymentHash: string.Empty,
            Status: data.SendStatus,
            FeeSats: feeSats,
            Preimage: data.SendPaymentPreimage);
    }

    /// <summary>
    /// Get a fee estimate for sending a Lightning payment.
    /// Returns estimated fee in satoshis.
    /// </summary>
    public static async Task<long> GetLightningSendFeeEstimateAsync(
        this SparkWallet wallet,
        string encodedInvoice,
        long? amountSats = null,
        CancellationToken ct = default)
    {
        var variables = new Dictionary<string, object?>
        {
            ["encoded_invoice"] = encodedInvoice,
            ["amount_sats"] = amountSats,
        };

        var response = await wallet.SspClient.ExecuteAsync<LightningSendFeeEstimateResponse>(
            Queries.LightningSendFeeEstimate, variables, ct).ConfigureAwait(false);

        // Honour the SSP's CurrencyAmount unit discriminator — the same field can come back as
        // SATOSHI or MILLISATOSHI depending on the route. See GraphQL.CurrencyAmountExtensions.
        var fee = response.LightningSendFeeEstimate.FeeEstimate;
        return CurrencyAmountExtensions.ToSats(fee.OriginalValue, fee.OriginalUnit);
    }

    /// <summary>
    /// Query transfers for a given receiver identity public key.
    /// Can be used to check if a Lightning invoice was paid (funds transferred to receiver).
    /// Internal: returns raw protobuf transfers, not part of the public NSpark contract.
    /// </summary>
    internal static async Task<IReadOnlyList<Proto.Transfer>> QueryTransfersForReceiverAsync(
        this SparkWallet wallet,
        byte[] receiverIdentityPublicKey,
        CancellationToken ct = default)
    {
        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);

        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet : Network.Regtest;
        var filter = new Proto.TransferFilter
        {
            ReceiverIdentityPublicKey = ByteString.CopyFrom(receiverIdentityPublicKey),
            Network = protoNetwork,
        };
        var response = await coordinatorClient.query_all_transfersAsync(
            filter, headers, cancellationToken: ct);

        return response.Transfers.ToList();
    }

    // JS SDK constants for sequence computation
    private const uint TimeLockInterval = 100;
    private const uint HtlcTimelockOffset = 70;
    private const uint DirectHtlcTimelockOffset = 85;
    private const uint DirectTimelockOffset = 50;
    private const uint LightningHtlcSequence = 2160;
    // DEFAULT_FEE_SATS = ESTIMATED_TX_SIZE(191) * DEFAULT_SATS_PER_VBYTE(5)
    private const ulong DefaultFeeSats = SparkConstants.DefaultRefundFeeSats;

    /// <summary>
    /// Pay a Lightning invoice via the v3 preimage swap flow.
    /// Matches the JS reference SDK: prepareTransferForLightning + swapNodesForPreimage + SSP send.
    /// </summary>
    public static async Task<string> PayLightningInvoiceAsync(
        this SparkWallet wallet,
        string paymentRequest,
        long? maxFeeSats = null,
        CancellationToken ct = default)
    {
        var paymentHash = Bolt11Decoder.GetPaymentHash(paymentRequest);
        var amountSats = Bolt11Decoder.GetAmountSats(paymentRequest);

        // Get fee estimate from SSP
        var feeEstimate = await wallet.GetLightningSendFeeEstimateAsync(paymentRequest, ct: ct).ConfigureAwait(false);
        if (maxFeeSats.HasValue && maxFeeSats.Value < feeEstimate)
        {
            throw new InvalidOperationException(
                $"maxFeeSats ({maxFeeSats.Value}) does not cover fee estimate ({feeEstimate} sats).");
        }

        var feeSats = (ulong)Math.Max(feeEstimate, 1);

        var coordinatorAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var coordinatorClient = wallet.Pool.GetSparkClient(coordinatorAddress);
        var headers = await wallet.GetAuthMetadataAsync(coordinatorAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);

        // Step 1: Select leaves covering invoice amount + fee (exact match or swap)
        var selectedLeaves = await wallet.SelectLeavesWithSwapAsync(amountSats + (long)feeSats, ct).ConfigureAwait(false);
        var leafIds = selectedLeaves.Select(l => l.Id).ToList();

        // Step 2: Get SO operator info
        var soListResponse = await coordinatorClient.get_signing_operator_listAsync(
            new Google.Protobuf.WellKnownTypes.Empty(), headers, cancellationToken: ct);
        var soOperators = soListResponse.SigningOperators;
        var soCount = (uint)soOperators.Count;

        var sspPubKey = Convert.FromHexString(wallet.Client.Options.SspIdentityPublicKeyHex);
        var senderPubKey = wallet.Signer.IdentityPublicKey;
        var transferId = Guid.NewGuid().ToString();
        var expiryTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
            DateTimeOffset.UtcNow.AddDays(16));

        // ── prepareTransferForLightning: key tweaks + HTLC refund txs ──

        // Step 3: Build key tweaks for each leaf
        var perSoTweaks = new Dictionary<string, SendLeafKeyTweaks>();
        foreach (var (soId, _) in soOperators)
        {
            perSoTweaks[soId] = new SendLeafKeyTweaks();
        }

        for (int i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            // Key tweak = oldSigningKey - newRandomKey (matches JS SDK subtractSplitAndEncrypt)
            var oldSigningKey = wallet.Signer.DeriveLeafSigningKey(leaf.Id);
            var newRandomKey = SparkFrostMethods.RandomSecretKeyBytes();
            var keyTweak = ClaimService.SubtractPrivateKeys(oldSigningKey, newRandomKey);
            var vssShares = SparkFrostMethods.SplitSecretWithProofsUniffi(
                keyTweak, threshold: Math.Max(2, (soCount + 2) / 2), numShares: soCount);
            // Encrypt the NEW key (intermediate signing key), NOT the tweak
            var secretCipher = SparkFrostMethods.EncryptEcies(newRandomKey, sspPubKey);

            var sigPayload = Encoding.UTF8.GetBytes(leaf.Id + transferId);
            sigPayload = [.. sigPayload, .. secretCipher];
            // Compact signature (64 bytes) for leaf key tweak, matching JS SDK compact=true
            var tweakSig = wallet.Signer.SignCompactWithIdentityKey(SHA256.HashData(sigPayload));

            var pubkeySharesTweak = new Dictionary<string, ByteString>();
            foreach (var (soId2, soInfo2) in soOperators)
            {
                var matchedShare = vssShares.First(s => s.@index == soInfo2.Index + 1);
                pubkeySharesTweak[soId2] = ByteString.CopyFrom(
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

        // Step 4: Encrypt per-SO key tweak packages (to config identity keys, NOT gRPC response keys)
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

        // Step 5: Get signing commitments for HTLC refund txs (Count=3: cpfp, direct, directFromCpfp)
        var htlcCommitmentsReq = new GetSigningCommitmentsRequest { Count = 3 };
        htlcCommitmentsReq.NodeIds.AddRange(leafIds);
        var htlcCommitmentsResp = await coordinatorClient.get_signing_commitmentsAsync(
            htlcCommitmentsReq, headers, cancellationToken: ct);
        var htlcCommitments = htlcCommitmentsResp.SigningCommitments.ToList();

        // Step 6: Build and sign HTLC refund txs (signRefundsForLightning)
        var htlcCpfpJobs = new List<UserSignedTxSigningJob>();
        var htlcDirectJobs = new List<UserSignedTxSigningJob>();
        var htlcDirectFromCpfpJobs = new List<UserSignedTxSigningJob>();

        for (int i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            var node = leaf.Node;
            var signingKey = wallet.Signer.DeriveLeafSigningKey(leaf.Id);
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();
            var nodeTxBytes = node.NodeTx.ToByteArray();

            // Read current sequence from refund tx
            var refundTxBytes = node.RefundTx.Length > 0
                ? node.RefundTx.ToByteArray()
                : nodeTxBytes;
            var currentSequence = ClaimService.ParseInputSequence(refundTxBytes);
            var currentTimelock = currentSequence & 0xFFFF;
            var bit30 = currentSequence & (1u << 30);
            var nextTimelock = currentTimelock - TimeLockInterval;
            var htlcSeq = bit30 | (nextTimelock + HtlcTimelockOffset);
            var htlcDirectSeq = bit30 | (nextTimelock + DirectHtlcTimelockOffset);

            // CPFP HTLC refund tx (applyFee: false)
            var cpfpHtlc = SparkFrostMethods.ConstructHtlcTransaction(
                nodeTx: nodeTxBytes, vout: 0, sequence: htlcSeq,
                paymentHash: paymentHash, hashlockPubkey: sspPubKey,
                seqlockPubkey: senderPubKey, htlcSequence: LightningHtlcSequence,
                applyFee: false, feeSats: 0, network: networkStr);

            htlcCpfpJobs.Add(FrostSigningHelper.BuildSigningJob(
                node.Id, signingKey, verifyingKey,
                cpfpHtlc.@tx, cpfpHtlc.@sighash,
                htlcCommitments[i].SigningNonceCommitments));

            // Direct HTLC refund tx (if directTx exists)
            if (node.DirectTx.Length > 0)
            {
                var directHtlc = SparkFrostMethods.ConstructHtlcTransaction(
                    nodeTx: node.DirectTx.ToByteArray(), vout: 0, sequence: htlcDirectSeq,
                    paymentHash: paymentHash, hashlockPubkey: sspPubKey,
                    seqlockPubkey: senderPubKey, htlcSequence: LightningHtlcSequence,
                    applyFee: true, feeSats: DefaultFeeSats, network: networkStr);

                htlcDirectJobs.Add(FrostSigningHelper.BuildSigningJob(
                    node.Id, signingKey, verifyingKey,
                    directHtlc.@tx, directHtlc.@sighash,
                    htlcCommitments[i + selectedLeaves.Count].SigningNonceCommitments));
            }

            // DirectFromCpfp HTLC refund tx (applyFee: true)
            var directFromCpfpHtlc = SparkFrostMethods.ConstructHtlcTransaction(
                nodeTx: nodeTxBytes, vout: 0, sequence: htlcDirectSeq,
                paymentHash: paymentHash, hashlockPubkey: sspPubKey,
                seqlockPubkey: senderPubKey, htlcSequence: LightningHtlcSequence,
                applyFee: true, feeSats: DefaultFeeSats, network: networkStr);

            htlcDirectFromCpfpJobs.Add(FrostSigningHelper.BuildSigningJob(
                node.Id, signingKey, verifyingKey,
                directFromCpfpHtlc.@tx, directFromCpfpHtlc.@sighash,
                htlcCommitments[i + 2 * selectedLeaves.Count].SigningNonceCommitments));
        }

        // Step 7: Build TransferPackage with HTLC jobs + key tweaks
        var transferPackage = new TransferPackage
        {
            UserSignature = ByteString.Empty, // signed below
            HashVariant = HashVariant.V2,
        };
        foreach (var job in htlcCpfpJobs)
        {
            transferPackage.LeavesToSend.Add(job);
        }

        foreach (var job in htlcDirectJobs)
        {
            transferPackage.DirectLeavesToSend.Add(job);
        }

        foreach (var job in htlcDirectFromCpfpJobs)
        {
            transferPackage.DirectFromCpfpLeavesToSend.Add(job);
        }

        foreach (var (soId, cipher) in keyTweakPackage)
        {
            transferPackage.KeyTweakPackage.Add(soId, cipher);
        }

        // Sign the transfer package
        var transferIdBytes = Convert.FromHexString(transferId.Replace("-", ""));
        var packageHash = SparkTaggedHash.Create("spark", "transfer", "signing payload")
            .AddBytes(transferIdBytes)
            .AddMapStringToBytes(keyTweakPackage)
            .Hash();
        transferPackage.UserSignature = ByteString.CopyFrom(
            wallet.Signer.SignWithIdentityKey(packageHash));

        // Step 8: Build StartTransferRequest with TransferPackage + leaves_to_send
        var startTransferRequest = new StartTransferRequest
        {
            TransferId = transferId,
            OwnerIdentityPublicKey = ByteString.CopyFrom(senderPubKey),
            ReceiverIdentityPublicKey = ByteString.CopyFrom(sspPubKey),
            ExpiryTime = expiryTime,
            TransferPackage = transferPackage,
        };

        // The server reads HTLC refund txs from leaves_to_send (LeafRefundTxSigningJob)
        for (int i = 0; i < selectedLeaves.Count; i++)
        {
            var leafRefundJob = new LeafRefundTxSigningJob
            {
                LeafId = htlcCpfpJobs[i].LeafId,
                RefundTxSigningJob = ToSigningJob(htlcCpfpJobs[i]),
                DirectFromCpfpRefundTxSigningJob = ToSigningJob(htlcDirectFromCpfpJobs[i]),
            };
            if (i < htlcDirectJobs.Count)
            {
                leafRefundJob.DirectRefundTxSigningJob = ToSigningJob(htlcDirectJobs[i]);
            }

            startTransferRequest.LeavesToSend.Add(leafRefundJob);
        }

        // ── swapNodesForPreimage: normal refund txs + initiate_preimage_swap_v3 ──

        // Step 9: Get signing commitments for normal refund txs (Count=3)
        var swapCommitmentsReq = new GetSigningCommitmentsRequest { Count = 3 };
        swapCommitmentsReq.NodeIds.AddRange(leafIds);
        var swapCommitmentsResp = await coordinatorClient.get_signing_commitmentsAsync(
            swapCommitmentsReq, headers, cancellationToken: ct);
        var swapCommitments = swapCommitmentsResp.SigningCommitments.ToList();

        // Step 10: Build and sign normal decremented-timelock refund txs (signRefunds)
        var swapCpfpJobs = new List<UserSignedTxSigningJob>();

        for (int i = 0; i < selectedLeaves.Count; i++)
        {
            var leaf = selectedLeaves[i];
            var node = leaf.Node;
            var signingKey = wallet.Signer.DeriveLeafSigningKey(leaf.Id);
            var verifyingKey = node.VerifyingPublicKey.ToByteArray();
            var nodeTxBytes = node.NodeTx.ToByteArray();
            var directNodeTx = node.DirectTx.Length > 0 ? node.DirectTx.ToByteArray() : null;

            // Read current sequence and compute decremented sequences
            var refundTxBytes = node.RefundTx.Length > 0
                ? node.RefundTx.ToByteArray()
                : nodeTxBytes;
            var currentSequence = ClaimService.ParseInputSequence(refundTxBytes);

            // Use ConstructRefundTxTrio for proper decremented-timelock refund txs
            var currentTimelock = currentSequence & 0xFFFF;
            var bit30 = currentSequence & (1u << 30);
            var nextTimelock = currentTimelock - TimeLockInterval;
            var normalSeq = bit30 | nextTimelock;
            var normalDirectSeq = bit30 | (nextTimelock + DirectTimelockOffset);

            var refundTrio = SparkFrostMethods.ConstructRefundTxTrio(
                cpfpNodeTx: nodeTxBytes,
                directNodeTx: directNodeTx,
                vout: 0,
                receivingPubkey: sspPubKey,
                network: networkStr,
                sequence: normalSeq,
                directSequence: normalDirectSeq,
                // Only the cpfp refund is submitted in this swap path, but value must still
                // match SSP expectation.
                feeSats: SparkConstants.DefaultRefundFeeSats);

            // Only cpfp goes into transfer.leavesToSend (direct/directFromCpfp omitted per ref SDK)
            swapCpfpJobs.Add(FrostSigningHelper.BuildSigningJob(
                node.Id, signingKey, verifyingKey,
                refundTrio.@cpfpRefund.@tx, refundTrio.@cpfpRefund.@sighash,
                swapCommitments[i].SigningNonceCommitments));
        }

        // Step 11: Build StartUserSignedTransferRequest (cpfp only)
        var swapTransfer = new StartUserSignedTransferRequest
        {
            TransferId = transferId,
            OwnerIdentityPublicKey = ByteString.CopyFrom(senderPubKey),
            ReceiverIdentityPublicKey = ByteString.CopyFrom(sspPubKey),
            ExpiryTime = expiryTime,
        };
        foreach (var job in swapCpfpJobs)
        {
            swapTransfer.LeavesToSend.Add(job);
        }

        // Step 12: Call initiate_preimage_swap_v3 with both transfer + transferRequest
        var swapResponse = await coordinatorClient.initiate_preimage_swap_v3Async(
            new InitiatePreimageSwapRequest
            {
                PaymentHash = ByteString.CopyFrom(paymentHash),
                InvoiceAmount = new InvoiceAmount
                {
                    ValueSats = (ulong)amountSats,
                    InvoiceAmountProof = new InvoiceAmountProof
                    {
                        Bolt11Invoice = paymentRequest,
                    },
                },
                Reason = InitiatePreimageSwapRequest.Types.Reason.Send,
                Transfer = swapTransfer,
                ReceiverIdentityPublicKey = ByteString.CopyFrom(sspPubKey),
                FeeSats = feeSats,
                TransferRequest = startTransferRequest,
            },
            headers,
            cancellationToken: ct);

        // Step 13: Call SSP to initiate the Lightning send with the transfer ID
        var variables = new Dictionary<string, object?>
        {
            ["encoded_invoice"] = paymentRequest,
            ["user_outbound_transfer_external_id"] = swapResponse.Transfer.Id,
        };

        var sspResponse = await wallet.SspClient.ExecuteAsync<RequestLightningSendResponse>(
            Mutations.RequestLightningSend, variables, ct).ConfigureAwait(false);

        return sspResponse.RequestLightningSend.Request.Id;
    }

    /// <summary>
    /// Minimal BOLT11 invoice decoder — extracts only payment hash and amount.
    /// </summary>
    internal static class Bolt11Decoder
    {
        private const string Bech32Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

        public static byte[] GetPaymentHash(string invoice)
        {
            var (_, dataGroups) = SplitInvoice(invoice);

            // Skip timestamp (7 groups)
            int offset = 7;

            while (offset + 3 <= dataGroups.Length)
            {
                int type = dataGroups[offset];
                int dataLen = (dataGroups[offset + 1] << 5) | dataGroups[offset + 2];
                offset += 3;

                if (type == 1) // payment hash tag
                {
                    return ConvertBits(dataGroups, offset, dataLen);
                }

                offset += dataLen;
            }

            throw new InvalidOperationException("Payment hash not found in BOLT11 invoice.");
        }

        public static long GetAmountSats(string invoice)
        {
            var lower = invoice.ToLowerInvariant();
            var separatorIdx = lower.LastIndexOf('1');
            var hrp = lower[..separatorIdx];

            // Strip ln + network prefix
            string amountStr;
            if (hrp.StartsWith("lnbcrt", StringComparison.Ordinal))
            {
                amountStr = hrp[6..];
            }
            else if (hrp.StartsWith("lnbc", StringComparison.Ordinal))
            {
                amountStr = hrp[4..];
            }
            else if (hrp.StartsWith("lntb", StringComparison.Ordinal))
            {
                amountStr = hrp[4..];
            }
            else
            {
                throw new InvalidOperationException($"Unknown BOLT11 network prefix: {hrp}");
            }

            if (string.IsNullOrEmpty(amountStr))
            {
                throw new InvalidOperationException("BOLT11 invoice has no amount.");
            }

            // Parse amount: digits + optional multiplier (m/u/n/p)
            char last = amountStr[^1];
            if (char.IsDigit(last))
            {
                return (long)(decimal.Parse(amountStr, System.Globalization.CultureInfo.InvariantCulture) * 100_000_000m);
            }

            var value = decimal.Parse(amountStr[..^1], System.Globalization.CultureInfo.InvariantCulture);
            var btcAmount = value * last switch
            {
                'm' => 0.001m,
                'u' => 0.000_001m,
                'n' => 0.000_000_001m,
                'p' => 0.000_000_000_001m,
                _ => throw new InvalidOperationException($"Unknown BOLT11 multiplier: {last}"),
            };

            return (long)(btcAmount * 100_000_000m);
        }

        private static (string hrp, int[] dataGroups) SplitInvoice(string invoice)
        {
            var lower = invoice.ToLowerInvariant();
            var separatorIdx = lower.LastIndexOf('1');
            var hrp = lower[..separatorIdx];
            var dataStr = lower[(separatorIdx + 1)..^6]; // strip 6-char bech32 checksum

            var groups = new int[dataStr.Length];
            for (int i = 0; i < dataStr.Length; i++)
            {
                groups[i] = Bech32Charset.IndexOf(dataStr[i]);
            }

            return (hrp, groups);
        }

        /// <summary>
        /// Convert 5-bit groups to 8-bit bytes (bech32 → raw bytes).
        /// </summary>
        private static byte[] ConvertBits(int[] data, int offset, int length)
        {
            int acc = 0, bits = 0;
            var result = new List<byte>();

            for (int i = 0; i < length; i++)
            {
                acc = (acc << 5) | data[offset + i];
                bits += 5;
                while (bits >= 8)
                {
                    bits -= 8;
                    result.Add((byte)((acc >> bits) & 0xFF));
                }
            }

            return result.ToArray();
        }
    }

    /// <summary>
    /// Extract SigningJob (subset) from a UserSignedTxSigningJob for LeafRefundTxSigningJob.
    /// </summary>
    private static Proto.SigningJob ToSigningJob(UserSignedTxSigningJob userJob)
    {
        return new Proto.SigningJob
        {
            SigningPublicKey = userJob.SigningPublicKey,
            RawTx = userJob.RawTx,
            SigningNonceCommitment = userJob.SigningNonceCommitment,
        };
    }
}
