using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSpark.GraphQL;
using NSpark.Signer;

namespace NSpark.Connection;

/// <summary>
/// Challenge-response authentication with the Spark Service Provider (SSP) via GraphQL.
/// Tokens are cached by identity public key hex with TTL.
/// </summary>
internal sealed class SspAuthenticator
{
    private static readonly ConcurrentDictionary<string, CachedToken> s_tokenCache = new();
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(1);

    private record CachedToken(string Token, DateTimeOffset ExpiresAt);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<string> GetTokenAsync(
        HttpClient httpClient,
        string sspUrl,
        ISparkSigner signer,
        CancellationToken ct = default)
    {
        var cacheKey = $"ssp:{Convert.ToHexString(signer.IdentityPublicKey)}";

        if (s_tokenCache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow + TokenRefreshBuffer)
        {
            return cached.Token;
        }

        var token = await AuthenticateAsync(httpClient, sspUrl, signer, ct).ConfigureAwait(false);
        s_tokenCache[cacheKey] = token;
        return token.Token;
    }

    private static async Task<CachedToken> AuthenticateAsync(
        HttpClient httpClient,
        string sspUrl,
        ISparkSigner signer,
        CancellationToken ct)
    {
        var identityPubKeyHex = Convert.ToHexString(signer.IdentityPublicKey).ToLowerInvariant();

        // Step 1: Get challenge (no auth required)
        var challengeResponse = await ExecuteGraphQLAsync<GetChallengeResponse>(
            httpClient, sspUrl, Mutations.GetChallenge,
            new { public_key = identityPubKeyHex }, ct).ConfigureAwait(false);

        var protectedChallenge = challengeResponse.GetChallenge.ProtectedChallenge;

        // Step 2: Sign the challenge (SSP uses base64url encoding)
        var challengeBytes = DecodeBase64Url(protectedChallenge);
        var challengeHash = SHA256.HashData(challengeBytes);
        var signature = signer.SignWithIdentityKey(challengeHash);
        var signatureBase64 = Convert.ToBase64String(signature);

        // Step 3: Verify challenge and get token
        var verifyResponse = await ExecuteGraphQLAsync<VerifyChallengeResponse>(
            httpClient, sspUrl, Mutations.VerifyChallenge,
            new
            {
                protected_challenge = protectedChallenge,
                signature = signatureBase64,
                identity_public_key = identityPubKeyHex,
            }, ct).ConfigureAwait(false);

        var data = verifyResponse.VerifyChallenge;
        var expiresAt = DateTimeOffset.Parse(data.ValidUntil, System.Globalization.CultureInfo.InvariantCulture);
        return new CachedToken(data.SessionToken, expiresAt);
    }

    private static async Task<T> ExecuteGraphQLAsync<T>(
        HttpClient httpClient,
        string sspUrl,
        string query,
        object variables,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, sspUrl)
        {
            Content = JsonContent.Create(new { query, variables }, options: s_jsonOptions)
        };

        var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GraphQLEnvelope<T>>(s_jsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SSP returned null response.");

        if (result.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", result.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"SSP auth error: {messages}");
        }

        return result.Data ?? throw new InvalidOperationException("SSP returned null data.");
    }

    private static byte[] DecodeBase64Url(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private record GraphQLEnvelope<T>(
        [property: JsonPropertyName("data")] T? Data,
        [property: JsonPropertyName("errors")] List<GraphQLErrorItem>? Errors);

    private record GraphQLErrorItem(
        [property: JsonPropertyName("message")] string Message);
}
