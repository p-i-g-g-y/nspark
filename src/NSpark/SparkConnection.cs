using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSpark.Connection;
using NSpark.Diagnostics;
using NSpark.Signer;

namespace NSpark;

/// <summary>
/// Singleton host that owns the shared transport infrastructure for one or more
/// <see cref="SparkWallet"/> instances: gRPC channels to Signing Operators, the
/// HTTP client used for SSP GraphQL, and the auth-token cache. One instance per
/// application is the recommended deployment.
/// </summary>
/// <remarks>
/// The type is named <c>SparkConnection</c> for legacy compatibility; it will be
/// renamed to <c>SparkConnection</c> when the namespace becomes <c>NSpark</c>
/// in the v1 release (release plan, Phase 6).
/// </remarks>
public sealed class SparkConnection : IDisposable, IAsyncDisposable
{
    private readonly GrpcConnectionPool _pool;
    private readonly HttpClient _httpClient;
    private readonly SparkOptions _options;
    private readonly SparkAuthenticator _authenticator;
    private readonly ServerTimeSync _timeSync;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SparkConnection> _logger;
    private int _disposed;

    /// <summary>
    /// Construct a new client with the given options and an HTTP client provided by
    /// <c>IHttpClientFactory</c>. Use <see cref="ServiceCollectionExtensions.AddSpark"/>
    /// to register the standard set of dependencies.
    /// </summary>
    public SparkConnection(IOptions<SparkOptions> options, HttpClient httpClient)
        : this(options, httpClient, NullLoggerFactory.Instance)
    {
    }

    /// <summary>
    /// Construct a new client with explicit logger factory wiring.
    /// </summary>
    public SparkConnection(
        IOptions<SparkOptions> options,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options.Value;
        _httpClient = httpClient;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SparkConnection>();

        _pool = new GrpcConnectionPool(_options.SigningOperatorAddresses);
        _authenticator = new SparkAuthenticator(loggerFactory.CreateLogger<SparkAuthenticator>());
        _timeSync = new ServerTimeSync();

        _logger.LogInformation(
            LogEvents.ClientReady,
            "NSpark client ready on {Network} with {SoCount} signing operators.",
            _options.Network,
            _options.SigningOperators.Length);
    }

    internal GrpcConnectionPool Pool => _pool;

    internal HttpClient HttpClient => _httpClient;

    internal SparkAuthenticator Authenticator => _authenticator;

    internal ServerTimeSync TimeSync => _timeSync;

    internal ILoggerFactory LoggerFactory => _loggerFactory;

    /// <summary>The configured options for this client.</summary>
    public SparkOptions Options => _options;

    /// <summary>
    /// Create a wallet instance from a BIP-39 mnemonic.
    /// The wallet is lightweight — just keys + references to shared infra.
    /// </summary>
    public SparkWallet CreateWallet(string mnemonic, int? account = null, string? passphrase = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(mnemonic);
        var effectiveAccount = account ?? (_options.Network == SparkNetwork.Regtest ? 0 : 1);
        var signer = SparkSigner.FromMnemonic(mnemonic, effectiveAccount, passphrase);
        return CreateWallet(signer);
    }

    /// <summary>
    /// Create a wallet instance from an existing signer.
    /// </summary>
    public SparkWallet CreateWallet(ISparkSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        var sspClient = new SspGraphQLClient(
            _httpClient,
            _options.SspUrl,
            ct => SspAuthenticator.GetTokenAsync(_httpClient, _options.SspUrl, signer, ct));

        return new SparkWallet(this, signer, sspClient);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _logger.LogInformation(LogEvents.ClientDisposing, "NSpark client disposing.");
        _authenticator.ClearCache();
        _pool.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // No asynchronous resources yet — both gRPC channels and HttpClient
        // are managed by the IHttpClientFactory pipeline. This API exists so
        // hosts that always-await disposal can do so cleanly.
        Dispose();
        return ValueTask.CompletedTask;
    }
}
