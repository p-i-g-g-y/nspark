using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NBitcoin;
using NSpark.GraphQL;
using NSpark.Models;
using NSpark.Proto;
using Network = NSpark.Proto.Network;

namespace NSpark.Services;

/// <summary>
/// Extension methods on <see cref="SparkWallet"/> for on-chain Bitcoin deposits
/// — generating addresses, claiming confirmed deposits, and managing static
/// deposit flows.
/// </summary>
public static class DepositService
{
    private const uint InitialRefundSequence = 2000;
    private const uint DirectTimelockOffset = 50;
    private const ulong DefaultFeeSats = SparkConstants.DefaultRefundFeeSats; // 191 vbytes × 5 sat/vbyte

    /// <summary>
    /// Generate a deposit address for receiving on-chain BTC into the Spark wallet.
    /// </summary>
    public static async Task<DepositAddress> GetDepositAddressAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var network = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet
            : Network.Regtest;

        var leafId = Guid.NewGuid().ToString().ToLowerInvariant();
        var signingPubKey = await wallet.Signer.GetLeafPublicKeyAsync(leafId, ct).ConfigureAwait(false);

        var response = await client.generate_deposit_addressAsync(
            new GenerateDepositAddressRequest
            {
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                SigningPublicKey = ByteString.CopyFrom(signingPubKey),
                Network = network,
                LeafId = leafId,
                HashVariant = HashVariant.V2,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        return new DepositAddress(
            Address: response.DepositAddress.Address_,
            LeafId: leafId,
            UserPublicKey: signingPubKey,
            VerifyingKey: response.DepositAddress.VerifyingKey.ToByteArray());
    }

    /// <summary>
    /// Claim a confirmed on-chain deposit, creating the Spark tree.
    /// </summary>
    /// <param name="wallet">The Spark wallet.</param>
    /// <param name="depositTxId">The on-chain transaction ID (hex string).</param>
    /// <param name="vout">The output index (default 0).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ClaimDepositAsync(
        this SparkWallet wallet,
        string depositTxId,
        uint vout = 0,
        CancellationToken ct = default)
    {
        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);
        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet : Network.Regtest;

        // Step 1: Fetch raw tx from mempool.space / local electrs
        var rawTx = await FetchRawTransactionAsync(wallet, depositTxId, ct).ConfigureAwait(false);

        // Step 2: Query unused deposit addresses to find matching leafId
        var queryResp = await client.query_unused_deposit_addressesAsync(
            new QueryUnusedDepositAddressesRequest
            {
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = protoNetwork,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        var depositInfo = queryResp.DepositAddresses
            .FirstOrDefault(d => !string.IsNullOrEmpty(d.LeafId))
            ?? throw new InvalidOperationException(
                "No unused deposit address found. Generate one first with GetDepositAddressAsync().");

        var leafId = depositInfo.LeafId;
        var verifyingKey = depositInfo.VerifyingPublicKey.ToByteArray();
        var depositAddress = depositInfo.DepositAddress;

        // Step 3: Get the per-leaf public key from the signer (no private key needed in process)
        var signingPubKey = await wallet.Signer.GetLeafPublicKeyAsync(leafId, ct).ConfigureAwait(false);

        // Step 4: Create root node tx pair (CPFP + direct)
        var rootNodeTx = SparkTxBuilder.BuildNodeTxPair(
            parentTx: rawTx,
            vout: vout,
            address: depositAddress,
            sequence: 0,
            directSequence: DirectTimelockOffset,
            feeSats: DefaultFeeSats);

        // Step 5: Create refund tx trio
        var refundTrio = SparkTxBuilder.BuildRefundTxTrio(
            cpfpNodeTx: rootNodeTx.Cpfp.Tx,
            directNodeTx: null,
            vout: 0,
            receivingPublicKey: signingPubKey,
            network: networkStr,
            sequence: InitialRefundSequence,
            directSequence: InitialRefundSequence + DirectTimelockOffset,
            feeSats: DefaultFeeSats);

        // Step 6: Get signing commitments (3: root, cpfpRefund, directFromCpfpRefund)
        var commitmentsResponse = await client.get_signing_commitmentsAsync(
            new GetSigningCommitmentsRequest { Count = 3, NodeIdCount = 1 },
            headers,
            cancellationToken: ct).ConfigureAwait(false);
        var allCommitments = commitmentsResponse.SigningCommitments.ToList();

        // Step 7: Build signing jobs (FROST signing happens inside the signer)
        var rootJob = await FrostSigningHelper.BuildSigningJobAsync(
            wallet.Signer, leafId, verifyingKey,
            rootNodeTx.Cpfp.Tx, rootNodeTx.Cpfp.Sighash,
            allCommitments[0].SigningNonceCommitments, ct).ConfigureAwait(false);

        var refundJob = await FrostSigningHelper.BuildSigningJobAsync(
            wallet.Signer, leafId, verifyingKey,
            refundTrio.CpfpRefund.Tx, refundTrio.CpfpRefund.Sighash,
            allCommitments[1].SigningNonceCommitments, ct).ConfigureAwait(false);

        var directFromCpfpRefundJob = await FrostSigningHelper.BuildSigningJobAsync(
            wallet.Signer, leafId, verifyingKey,
            refundTrio.DirectFromCpfpRefund.Tx, refundTrio.DirectFromCpfpRefund.Sighash,
            allCommitments[2].SigningNonceCommitments, ct).ConfigureAwait(false);

        // Step 8: Build UTXO proto (txid in internal byte order = reversed)
        var txidBytes = Convert.FromHexString(depositTxId);
        Array.Reverse(txidBytes);

        var utxo = new UTXO
        {
            RawTx = ByteString.CopyFrom(rawTx),
            Vout = vout,
            Network = protoNetwork,
            Txid = ByteString.CopyFrom(txidBytes),
        };

        // Step 9: Finalize deposit tree creation
        await client.finalize_deposit_tree_creationAsync(
            new FinalizeDepositTreeCreationRequest
            {
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                OnChainUtxo = utxo,
                RootTxSigningJob = rootJob,
                RefundTxSigningJob = refundJob,
                DirectFromCpfpRefundTxSigningJob = directFromCpfpRefundJob,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generate a static (reusable) deposit address.
    /// </summary>
    public static async Task<StaticDepositAddress> GetStaticDepositAddressAsync(
        this SparkWallet wallet,
        CancellationToken ct = default)
    {
        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var network = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet : Network.Regtest;

        var staticPubKey = await wallet.Signer.GetStaticDepositPublicKeyAsync(0, ct).ConfigureAwait(false);

        var response = await client.generate_static_deposit_addressAsync(
            new GenerateStaticDepositAddressRequest
            {
                SigningPublicKey = ByteString.CopyFrom(staticPubKey),
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = network,
                HashVariant = HashVariant.V2,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        return new StaticDepositAddress(
            Address: response.DepositAddress.Address_,
            VerifyingKey: response.DepositAddress.VerifyingKey.ToByteArray());
    }

    /// <summary>
    /// Claim a static deposit via the SSP. Returns the Spark transfer ID.
    /// </summary>
    public static async Task<string> ClaimStaticDepositAsync(
        this SparkWallet wallet,
        string transactionId,
        uint outputIndex = 0,
        CancellationToken ct = default)
    {
        var networkStr = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? "MAINNET" : "REGTEST";

        // Step 1: Get quote from SSP
        var quoteResponse = await wallet.SspClient.ExecuteAsync<StaticDepositQuoteResponse>(
            Mutations.StaticDepositQuote,
            new Dictionary<string, object>
            {
                ["transaction_id"] = transactionId,
                ["output_index"] = (int)outputIndex,
                ["network"] = networkStr,
            },
            ct).ConfigureAwait(false);

        var creditAmountSats = quoteResponse.StaticDepositQuote.CreditAmountSats;
        var quoteSignature = quoteResponse.StaticDepositQuote.Signature;

        // Step 2: Build signing payload. The Spark static-deposit protocol requires revealing
        // the raw static-deposit private key to the SSP — signers that refuse to export it
        // (HSM/KMS) will throw NotSupportedException and static-deposit claims are unavailable.
        var staticSecretKey = await wallet.Signer.ExportStaticDepositPrivateKeyAsync(0, ct).ConfigureAwait(false);
        var depositSecretKeyHex = Convert.ToHexString(staticSecretKey).ToLowerInvariant();

        // Payload: "claim_static_deposit" + network(lowercase) + txid + outputIndex(LE u32) + requestType(u8: 0=Fixed) + creditAmountSats(LE u64) + sspSignature
        var networkPayload = networkStr.ToLowerInvariant(); // signing payload uses lowercase
        using var ms = new MemoryStream();
        ms.Write(System.Text.Encoding.UTF8.GetBytes("claim_static_deposit"));
        ms.Write(System.Text.Encoding.UTF8.GetBytes(networkPayload));
        ms.Write(System.Text.Encoding.UTF8.GetBytes(transactionId));
        ms.Write(BitConverter.GetBytes(outputIndex)); // LE u32
        ms.WriteByte(0); // requestType = Fixed
        ms.Write(BitConverter.GetBytes((ulong)creditAmountSats)); // LE u64
        var sigBytes = Convert.FromHexString(quoteSignature);
        ms.Write(sigBytes);

        var payloadHash = SHA256.HashData(ms.ToArray());
        var signature = await wallet.Signer.SignWithIdentityKeyAsync(payloadHash, ct).ConfigureAwait(false);

        // Step 3: Claim via SSP
        var claimResponse = await wallet.SspClient.ExecuteAsync<ClaimStaticDepositResponse>(
            Mutations.ClaimStaticDeposit,
            new Dictionary<string, object>
            {
                ["transaction_id"] = transactionId,
                ["output_index"] = (int)outputIndex,
                ["network"] = networkStr,
                ["request_type"] = "FIXED_AMOUNT",
                ["credit_amount_sats"] = creditAmountSats,
                ["deposit_secret_key"] = depositSecretKeyHex,
                ["signature"] = Convert.ToHexString(signature).ToLowerInvariant(),
                ["quote_signature"] = quoteSignature,
            },
            ct).ConfigureAwait(false);

        return claimResponse.ClaimStaticDeposit.TransferId
            ?? throw new InvalidOperationException("ClaimStaticDeposit did not return a transfer ID");
    }

    /// <summary>
    /// Query unused (non-static) deposit addresses for this wallet.
    /// </summary>
    public static async Task<IReadOnlyList<UnusedDepositAddress>> QueryUnusedDepositAddressesAsync(
        this SparkWallet wallet,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet : Network.Regtest;

        var response = await client.query_unused_deposit_addressesAsync(
            new QueryUnusedDepositAddressesRequest
            {
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
                Network = protoNetwork,
                Limit = limit,
                Offset = offset,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        return response.DepositAddresses
            .Select(d => new UnusedDepositAddress(
                Address: d.DepositAddress,
                LeafId: d.HasLeafId ? d.LeafId : null,
                UserSigningPublicKey: d.UserSigningPublicKey.ToByteArray(),
                VerifyingPublicKey: d.VerifyingPublicKey.ToByteArray()))
            .ToList();
    }

    /// <summary>
    /// Get UTXOs sent to a deposit address. Calls the Spark coordinator.
    /// </summary>
    public static async Task<IReadOnlyList<Models.DepositUtxo>> GetUtxosForDepositAddressAsync(
        this SparkWallet wallet,
        string address,
        bool excludeClaimed = true,
        CancellationToken ct = default)
    {
        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet : Network.Regtest;

        var response = await client.get_utxos_for_addressAsync(
            new GetUtxosForAddressRequest
            {
                Address = address,
                Network = protoNetwork,
                ExcludeClaimed = excludeClaimed,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        return response.Utxos
            .Select(u =>
            {
                // Bitcoin txids are displayed in reverse byte order. Use Array.Reverse explicitly to
                // avoid overload ambiguity with MemoryExtensions.Reverse(Span<T>) on newer TFMs.
                var txidBytes = u.Txid.ToByteArray();
                Array.Reverse(txidBytes);
                var txidHex = Convert.ToHexString(txidBytes).ToLowerInvariant();
                return new Models.DepositUtxo(Txid: txidHex, Vout: u.Vout);
            })
            .ToList();
    }

    /// <summary>
    /// Get a fee estimate (credit amount) for claiming a static deposit.
    /// </summary>
    public static async Task<DepositFeeEstimate> GetDepositFeeEstimateAsync(
        this SparkWallet wallet,
        string transactionId,
        uint outputIndex = 0,
        CancellationToken ct = default)
    {
        var networkStr = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? "MAINNET" : "REGTEST";

        var response = await wallet.SspClient.ExecuteAsync<StaticDepositQuoteResponse>(
            Mutations.StaticDepositQuote,
            new Dictionary<string, object>
            {
                ["transaction_id"] = transactionId,
                ["output_index"] = (int)outputIndex,
                ["network"] = networkStr,
            },
            ct).ConfigureAwait(false);

        return new DepositFeeEstimate(
            CreditAmountSats: response.StaticDepositQuote.CreditAmountSats,
            Signature: response.StaticDepositQuote.Signature);
    }

    /// <summary>
    /// Refund a static deposit back to an on-chain address.
    /// Returns the signed transaction hex ready for broadcast.
    /// </summary>
    public static async Task<string> RefundStaticDepositAsync(
        this SparkWallet wallet,
        string depositTransactionId,
        string destinationAddress,
        ulong satsPerVbyte,
        uint outputIndex = 0,
        CancellationToken ct = default)
    {
        if (satsPerVbyte > 150)
        {
            throw new ArgumentException("Fee rate must be <= 150 sat/vbyte", nameof(satsPerVbyte));
        }

        // Estimated tx size for a single-input single-output P2TR spend
        const ulong estimatedVbytes = 194;
        var fee = satsPerVbyte * estimatedVbytes;

        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);
        var networkStr = FrostSigningHelper.GetNetworkString(wallet.Client.Options.Network);
        var protoNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? Network.Mainnet : Network.Regtest;

        // Step 1: Fetch the raw deposit transaction
        var rawDepositTx = await FetchRawTransactionAsync(wallet, depositTransactionId, ct).ConfigureAwait(false);

        // Step 2: Parse the output value at outputIndex
        var (script, totalAmount) = WithdrawalService.ParseTxOutput(rawDepositTx, outputIndex);
        var creditAmountSats = totalAmount - fee;
        if (creditAmountSats <= 0 || totalAmount < fee)
        {
            throw new InvalidOperationException(
                $"Deposit output ({totalAmount} sats) too small to cover fee ({fee} sats)");
        }

        // Step 3: Build the spend transaction
        var nbtcNetwork = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? NBitcoin.Network.Main : NBitcoin.Network.RegTest;
        var destScript = BitcoinAddress.Create(destinationAddress, nbtcNetwork).ScriptPubKey.ToBytes();
        var spendTx = ConstructSpendTx(depositTransactionId, outputIndex, destScript, creditAmountSats);

        // Step 4: Compute sighash
        var sighash = SparkTxBuilder.ComputeMultiInputSighash(
            tx: spendTx, inputIndex: 0,
            prevOutScripts: [script],
            prevOutValues: [totalAmount]);

        // Step 5: Build user signature payload
        // "claim_static_deposit" + network + txId + outputIndex(LE u32) + requestType(2=Refund) + creditAmountSats(LE u64) + sighash.hex
        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes("claim_static_deposit"));
        ms.Write(Encoding.UTF8.GetBytes(networkStr.ToLowerInvariant()));
        ms.Write(Encoding.UTF8.GetBytes(depositTransactionId));
        ms.Write(BitConverter.GetBytes(outputIndex)); // LE u32
        ms.WriteByte(2); // requestType = Refund
        ms.Write(BitConverter.GetBytes(creditAmountSats)); // LE u64
        ms.Write(Encoding.UTF8.GetBytes(Convert.ToHexString(sighash).ToLowerInvariant()));

        var payloadHash = SHA256.HashData(ms.ToArray());
        var userSignature = await wallet.Signer.SignWithIdentityKeyAsync(payloadHash, ct).ConfigureAwait(false);

        // Step 6: Phase-1 FROST nonce via signer (static-deposit key never leaves the signer)
        var staticNonce = await wallet.Signer.GenerateStaticDepositFrostNonceAsync(0, ct).ConfigureAwait(false);
        var staticPubKey = staticNonce.PublicKey;

        // Step 7: Build SigningJob
        var signingJob = new Proto.SigningJob
        {
            SigningPublicKey = ByteString.CopyFrom(staticPubKey),
            RawTx = ByteString.CopyFrom(spendTx),
            SigningNonceCommitment = new Proto.Common.SigningCommitment
            {
                Hiding = ByteString.CopyFrom(staticNonce.Commitment.Hiding),
                Binding = ByteString.CopyFrom(staticNonce.Commitment.Binding),
            },
        };

        // Step 8: Build UTXO (txid in internal byte order = reversed)
        var txidBytes = Convert.FromHexString(depositTransactionId);
        Array.Reverse(txidBytes);
        var utxo = new UTXO
        {
            Txid = ByteString.CopyFrom(txidBytes),
            Vout = outputIndex,
            Network = protoNetwork,
        };

        // Step 9: Call initiate_static_deposit_utxo_refund
        var refundResponse = await client.initiate_static_deposit_utxo_refundAsync(
            new InitiateStaticDepositUtxoRefundRequest
            {
                OnChainUtxo = utxo,
                RefundTxSigningJob = signingJob,
                UserSignature = ByteString.CopyFrom(userSignature),
                HashVariant = HashVariant.V2,
            },
            headers,
            cancellationToken: ct).ConfigureAwait(false);

        // Step 10: Phase-2 FROST sign via signer with the real verifyingKey from the SO,
        // then aggregate locally (aggregation is a pure-public-key op).
        var verifyingKey = refundResponse.DepositAddress.VerifyingPublicKey.ToByteArray();
        var soCommitments = new Dictionary<string, NSpark.Signer.SigningCommitment>();
        foreach (var (soId, commitment) in refundResponse.RefundTxSigningResult.SigningNonceCommitments)
        {
            soCommitments[soId] = new NSpark.Signer.SigningCommitment(
                commitment.Hiding.ToByteArray(), commitment.Binding.ToByteArray());
        }
        var selfSignature = await wallet.Signer.SignStaticDepositFrostWithNonceAsync(
            0, staticNonce.NonceHandle, sighash, verifyingKey, soCommitments, ct).ConfigureAwait(false);
        var aggregatedSig = FrostSigningHelper.AggregateFrostSignature(
            sighash: sighash,
            selfCommitment: staticNonce.Commitment,
            selfSignature: selfSignature,
            selfPublicKey: staticPubKey,
            verifyingKey: verifyingKey,
            signingResult: refundResponse.RefundTxSigningResult,
            adaptorPublicKey: null);

        // Step 11: Add witness to tx and return hex
        var signedTx = AddWitnessToTx(spendTx, aggregatedSig);
        return Convert.ToHexString(signedTx).ToLowerInvariant();
    }

    /// <summary>
    /// Broadcast a raw transaction to the Bitcoin network.
    /// Returns the transaction ID.
    /// </summary>
    public static async Task<string> BroadcastTransactionAsync(
        this SparkWallet wallet,
        string txHex,
        CancellationToken ct = default)
    {
        var baseUrl = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? "https://mempool.space/api"
            : "http://localhost:3000";

        var httpClient = wallet.Client.HttpClient;
        var content = new StringContent(txHex, Encoding.UTF8, "text/plain");
        var response = await httpClient.PostAsync($"{baseUrl}/tx", content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
    }

    // ── Private helpers ──

    /// <summary>
    /// Construct a version-2 spend transaction with one P2TR input and one output.
    /// </summary>
    private static byte[] ConstructSpendTx(
        string txIdHex, uint vout, byte[] destScriptPubKey, ulong amount)
    {
        using var ms = new MemoryStream();

        // Version 2
        ms.Write(BitConverter.GetBytes(2u));

        // Segwit marker + flag
        ms.WriteByte(0x00);
        ms.WriteByte(0x01);

        // Input count = 1
        ms.WriteByte(0x01);

        // Input: txid (reversed) + vout + empty scriptSig + sequence
        var txidBytes = Convert.FromHexString(txIdHex);
        Array.Reverse(txidBytes);
        ms.Write(txidBytes);
        ms.Write(BitConverter.GetBytes(vout));
        ms.WriteByte(0x00); // scriptSig length = 0
        ms.Write(BitConverter.GetBytes(0xFFFFFFFFu)); // sequence

        // Output count = 1
        ms.WriteByte(0x01);

        // Output: amount + scriptPubKey
        ms.Write(BitConverter.GetBytes(amount));
        var scriptLenBytes = WithdrawalService.EncodeVarInt((ulong)destScriptPubKey.Length);
        ms.Write(scriptLenBytes);
        ms.Write(destScriptPubKey);

        // Empty witness for the single input
        ms.WriteByte(0x00);

        // Locktime = 0
        ms.Write(BitConverter.GetBytes(0u));

        return ms.ToArray();
    }

    /// <summary>
    /// Replace empty witness with the aggregated signature for a single-input transaction.
    /// </summary>
    private static byte[] AddWitnessToTx(byte[] rawTx, byte[] signature)
    {
        // Find the witness section: after outputs, before locktime
        int offset = 4; // skip version
        bool hasWitness = rawTx.Length > 5 && rawTx[offset] == 0x00 && rawTx[offset + 1] == 0x01;
        if (hasWitness)
        {
            offset += 2;
        }

        // Skip inputs
        var (inputCount, inputCountLen) = WithdrawalService.ReadVarInt(rawTx, offset);
        offset += inputCountLen;
        for (ulong j = 0; j < inputCount; j++)
        {
            offset += 36;
            var (scriptLen, scriptLenLen) = WithdrawalService.ReadVarInt(rawTx, offset);
            offset += scriptLenLen + (int)scriptLen + 4;
        }

        // Skip outputs
        var (outputCount, outputCountLen) = WithdrawalService.ReadVarInt(rawTx, offset);
        offset += outputCountLen;
        for (ulong j = 0; j < outputCount; j++)
        {
            offset += 8;
            var (scriptLen, scriptLenLen) = WithdrawalService.ReadVarInt(rawTx, offset);
            offset += scriptLenLen + (int)scriptLen;
        }
        int afterOutputs = offset;

        // Build new tx: version + marker/flag + inputs + outputs + witness + locktime
        using var result = new MemoryStream();
        result.Write(rawTx, 0, 4); // version
        result.Write([0x00, 0x01]); // marker + flag
        // inputs + outputs (from after marker/flag to afterOutputs)
        int dataStart = hasWitness ? 6 : 4;
        result.Write(rawTx, dataStart, afterOutputs - dataStart);

        // Witness: 1 item (the signature) for the single input
        result.WriteByte(0x01); // witness count for input 0
        var sigLen = WithdrawalService.EncodeVarInt((ulong)signature.Length);
        result.Write(sigLen);
        result.Write(signature);

        // Locktime (last 4 bytes)
        result.Write(rawTx, rawTx.Length - 4, 4);

        return result.ToArray();
    }

    /// <summary>
    /// Fetch raw transaction bytes from mempool.space (mainnet) or local electrs (regtest).
    /// </summary>
    internal static async Task<byte[]> FetchRawTransactionAsync(
        SparkWallet wallet, string txId, CancellationToken ct)
    {
        var baseUrl = wallet.Client.Options.Network == SparkNetwork.Mainnet
            ? "https://mempool.space/api"
            : "http://localhost:3000";

        var url = $"{baseUrl}/tx/{txId}/hex";
        var httpClient = wallet.Client.HttpClient;
        var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var hexString = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        return Convert.FromHexString(hexString);
    }
}
