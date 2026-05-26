using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSpark.Diagnostics;
using NSpark.Exceptions;
using NSpark.Proto.Authn;
using NSpark.Signer;

namespace NSpark.Connection;

/// <summary>
/// Challenge-response authentication with Spark Signing Operators.
/// Tokens are cached per (soAddress, identityPubKey) with TTL + size eviction.
/// </summary>
/// <remarks>
/// The previous instance-static cache was unbounded; under workloads that create
/// many short-lived signers (e.g. multi-tenant hosts) it leaked. This implementation
/// caps the cache at <see cref="DefaultMaxCacheEntries"/> entries and evicts the
/// least-recently-used token when full.
/// </remarks>
internal sealed class SparkAuthenticator
{
    /// <summary>Default maximum number of cached tokens.</summary>
    public const int DefaultMaxCacheEntries = 1024;

    /// <summary>Default buffer subtracted from token expiration to force early refresh.</summary>
    public static readonly TimeSpan DefaultRefreshBuffer = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();
    private readonly ILogger<SparkAuthenticator> _logger;
    private readonly int _maxCacheEntries;
    private readonly TimeSpan _refreshBuffer;

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt, long LastUsedTicks);

    /// <inheritdoc cref="SparkAuthenticator(ILogger{SparkAuthenticator}, int, TimeSpan?)"/>
    public SparkAuthenticator()
        : this(NullLogger<SparkAuthenticator>.Instance, DefaultMaxCacheEntries, DefaultRefreshBuffer)
    {
    }

    /// <summary>
    /// Create an authenticator with explicit cache configuration.
    /// </summary>
    /// <param name="logger">Logger for telemetry events.</param>
    /// <param name="maxCacheEntries">Soft cap on cached tokens; LRU-evicted on overflow.</param>
    /// <param name="refreshBuffer">How long before expiration to force a refresh.</param>
    public SparkAuthenticator(
        ILogger<SparkAuthenticator> logger,
        int maxCacheEntries = DefaultMaxCacheEntries,
        TimeSpan? refreshBuffer = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxCacheEntries = maxCacheEntries > 0
            ? maxCacheEntries
            : throw new ArgumentOutOfRangeException(nameof(maxCacheEntries));
        _refreshBuffer = refreshBuffer ?? DefaultRefreshBuffer;
    }

    /// <summary>
    /// Get a valid auth token for the given SO address, authenticating if needed.
    /// </summary>
    public async Task<string> GetTokenAsync(
        GrpcConnectionPool pool,
        string soAddress,
        ISparkSigner signer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentException.ThrowIfNullOrEmpty(soAddress);
        ArgumentNullException.ThrowIfNull(signer);

        var identityPubKey = await signer.GetIdentityPublicKeyAsync(ct).ConfigureAwait(false);
        var cacheKey = MakeCacheKey(soAddress, identityPubKey);

        if (_cache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow + _refreshBuffer)
        {
            // Refresh LRU stamp atomically.
            _cache[cacheKey] = cached with { LastUsedTicks = DateTime.UtcNow.Ticks };
            SparkMeter.AuthTokenCacheHits.Add(1);
            return cached.Token;
        }

        SparkMeter.AuthTokenCacheMisses.Add(1);
        var fresh = await AuthenticateAsync(pool, soAddress, signer, identityPubKey, ct).ConfigureAwait(false);

        EvictIfFull();
        _cache[cacheKey] = fresh;
        return fresh.Token;
    }

    /// <summary>
    /// Create gRPC call headers with the auth token.
    /// </summary>
    public async Task<Metadata> GetAuthMetadataAsync(
        GrpcConnectionPool pool,
        string soAddress,
        ISparkSigner signer,
        CancellationToken ct = default)
    {
        var token = await GetTokenAsync(pool, soAddress, signer, ct).ConfigureAwait(false);
        return new Metadata { { "authorization", $"Bearer {token}" } };
    }

    /// <summary>
    /// Drop every cached token (for example on signer rotation or shutdown).
    /// </summary>
    public void ClearCache() => _cache.Clear();

    private static string MakeCacheKey(string soAddress, byte[] identityPubKey) =>
        $"{soAddress}:{Convert.ToHexString(identityPubKey)}";

    private void EvictIfFull()
    {
        if (_cache.Count < _maxCacheEntries)
        {
            return;
        }

        // Simple LRU eviction: drop ~10% of the least recently used entries.
        var dropCount = Math.Max(1, _maxCacheEntries / 10);
        foreach (var kvp in _cache
                     .OrderBy(e => e.Value.LastUsedTicks)
                     .Take(dropCount))
        {
            _cache.TryRemove(kvp.Key, out _);
        }
    }

    private async Task<CachedToken> AuthenticateAsync(
        GrpcConnectionPool pool,
        string soAddress,
        ISparkSigner signer,
        byte[] identityPubKey,
        CancellationToken ct)
    {
        using var activity = SparkActivitySource.Start(SparkActivitySource.Spans.SoAuthenticate);
        activity?.SetTag(SparkActivitySource.Tags.SoAddress, soAddress);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var authnClient = pool.GetAuthnClient(soAddress);

            // Step 1: Request challenge.
            var challengeResponse = await authnClient.get_challengeAsync(
                new GetChallengeRequest
                {
                    PublicKey = ByteString.CopyFrom(identityPubKey),
                },
                cancellationToken: ct).ConfigureAwait(false);

            // Step 2: Sign the challenge.
            var challengeBytes = challengeResponse.ProtectedChallenge.Challenge.ToByteArray();
            var challengeHash = SHA256.HashData(challengeBytes);
            var signature = await signer.SignWithIdentityKeyAsync(challengeHash, ct).ConfigureAwait(false);

            // Step 3: Verify challenge and receive a session token.
            var verifyResponse = await authnClient.verify_challengeAsync(
                new VerifyChallengeRequest
                {
                    ProtectedChallenge = challengeResponse.ProtectedChallenge,
                    Signature = ByteString.CopyFrom(signature),
                    PublicKey = ByteString.CopyFrom(identityPubKey),
                },
                cancellationToken: ct).ConfigureAwait(false);

            stopwatch.Stop();
            SparkMeter.SoRttMs.Record(stopwatch.Elapsed.TotalMilliseconds);

            _logger.LogDebug(
                LogEvents.AuthTokenRefreshed,
                "Refreshed auth token from signing operator {SoAddress}; expires at {ExpiresAt}.",
                soAddress,
                DateTimeOffset.FromUnixTimeSeconds(verifyResponse.ExpirationTimestamp));

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(verifyResponse.ExpirationTimestamp);
            return new CachedToken(verifyResponse.SessionToken, expiresAt, DateTime.UtcNow.Ticks);
        }
        catch (RpcException rpc)
        {
            SparkMeter.SoGrpcErrors.Add(1);
            activity?.SetTag(SparkActivitySource.Tags.GrpcStatus, rpc.StatusCode.ToString());
            activity?.SetStatus(ActivityStatusCode.Error, rpc.Status.Detail);
            _logger.LogWarning(
                LogEvents.GrpcCallFailed,
                rpc,
                "Authentication gRPC call to {SoAddress} failed with status {GrpcStatus}.",
                soAddress,
                rpc.StatusCode);
            throw SparkConnectionException.FromRpc("so.authenticate", soAddress, rpc);
        }
    }
}
