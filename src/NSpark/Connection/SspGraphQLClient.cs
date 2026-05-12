using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NSpark.Connection;

/// <summary>
/// GraphQL client for the Spark Service Provider (SSP).
/// Uses a shared HttpClient with per-wallet auth tokens.
/// </summary>
internal sealed class SspGraphQLClient
{
    private readonly HttpClient _httpClient;
    private readonly string _sspUrl;
    private readonly Func<CancellationToken, Task<string>> _getToken;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SspGraphQLClient(HttpClient httpClient, string sspUrl, Func<CancellationToken, Task<string>> getToken)
    {
        _httpClient = httpClient;
        _sspUrl = sspUrl;
        _getToken = getToken;
    }

    public async Task<T> ExecuteAsync<T>(string query, object? variables = null, CancellationToken ct = default)
    {
        var token = await _getToken(ct).ConfigureAwait(false);

        var request = new HttpRequestMessage(HttpMethod.Post, _sspUrl)
        {
            Content = JsonContent.Create(new GraphQLRequest(query, variables), options: s_jsonOptions)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GraphQLResponse<T>>(s_jsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SSP returned null response.");

        if (result.Errors is { Count: > 0 })
        {
            var messages = string.Join("; ", result.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"SSP GraphQL errors: {messages}");
        }

        return result.Data ?? throw new InvalidOperationException("SSP returned null data.");
    }

    private record GraphQLRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("variables")] object? Variables);

    private record GraphQLResponse<T>(
        [property: JsonPropertyName("data")] T? Data,
        [property: JsonPropertyName("errors")] List<GraphQLError>? Errors);

    private record GraphQLError(
        [property: JsonPropertyName("message")] string Message);
}
