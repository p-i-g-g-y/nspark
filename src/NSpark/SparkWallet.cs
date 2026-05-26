using NSpark.Connection;
using NSpark.Services;
using NSpark.Signer;

namespace NSpark;

/// <summary>
/// Per-wallet instance holding the signer and exposing Spark operations. Lightweight —
/// references shared connections from <see cref="SparkConnection"/>. The wallet caches
/// the identity and deposit public keys at construction so synchronous accessors
/// (<see cref="IdentityPublicKey"/>, <see cref="GetSparkAddress"/>) stay non-async even
/// when the signer is remote.
/// </summary>
public sealed class SparkWallet
{
    private readonly SparkConnection _client;
    private readonly ISparkSigner _signer;
    private readonly SspGraphQLClient _sspClient;
    private readonly byte[] _identityPublicKey;
    private readonly byte[] _depositPublicKey;

    internal SparkWallet(
        SparkConnection client,
        ISparkSigner signer,
        SspGraphQLClient sspClient,
        byte[] identityPublicKey,
        byte[] depositPublicKey)
    {
        _client = client;
        _signer = signer;
        _sspClient = sspClient;
        _identityPublicKey = identityPublicKey;
        _depositPublicKey = depositPublicKey;
    }

    internal SparkConnection Client => _client;
    internal ISparkSigner Signer => _signer;
    internal SspGraphQLClient SspClient => _sspClient;
    internal GrpcConnectionPool Pool => _client.Pool;
    internal SparkAuthenticator Authenticator => _client.Authenticator;

    /// <summary>Identity public key (33-byte compressed secp256k1) cached at wallet creation.</summary>
    public byte[] IdentityPublicKey => _identityPublicKey;

    /// <summary>Deposit public key (33-byte compressed secp256k1) cached at wallet creation.</summary>
    public byte[] DepositPublicKey => _depositPublicKey;

    /// <summary>Identity public key hex string for this wallet.</summary>
    public string IdentityPublicKeyHex => Convert.ToHexString(_identityPublicKey).ToLowerInvariant();

    /// <summary>
    /// Get the Spark address for this wallet (Bech32m-encoded identity public key).
    /// </summary>
    public string GetSparkAddress()
    {
        var hrp = _client.Options.Network == SparkNetwork.Mainnet ? "spark" : "sparkrt";
        var pubkey = _identityPublicKey;

        // Build protobuf wire payload: field 1, wire type 2 (length-delimited) = tag 0x0A
        var payload = new byte[2 + pubkey.Length];
        payload[0] = 0x0A;
        payload[1] = (byte)pubkey.Length;
        Array.Copy(pubkey, 0, payload, 2, pubkey.Length);

        return Bech32mHelper.Encode(hrp, payload);
    }

    /// <summary>Get auth metadata for gRPC calls to a specific SO.</summary>
    internal async Task<Grpc.Core.Metadata> GetAuthMetadataAsync(string soAddress, CancellationToken ct = default)
    {
        return await Authenticator.GetAuthMetadataAsync(Pool, soAddress, _signer, ct).ConfigureAwait(false);
    }
}
