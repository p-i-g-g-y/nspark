using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSpark.Diagnostics;

namespace NSpark.Hosting;

/// <summary>
/// Optional background service that keeps a long-running <see cref="SparkConnection"/>
/// healthy: it currently logs lifecycle events and provides the extension point for
/// future background tasks such as token pre-refresh and event polling.
/// </summary>
/// <remarks>
/// Register with <see cref="ServiceCollectionExtensions.AddSpark"/> followed by
/// <c>services.AddHostedService&lt;SparkHostedService&gt;()</c> in your composition root.
/// The service is intentionally minimal in v1; future versions may pre-refresh
/// auth tokens N seconds before expiry to avoid first-call latency.
/// </remarks>
public sealed class SparkHostedService : IHostedService
{
    private readonly SparkConnection _client;
    private readonly ILogger<SparkHostedService> _logger;

    /// <summary>Construct the hosted service.</summary>
    public SparkHostedService(SparkConnection client, ILogger<SparkHostedService> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            LogEvents.HostedServiceStarted,
            "NSpark hosted service started for {Network} ({SoCount} operators).",
            _client.Options.Network,
            _client.Options.SigningOperators.Length);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
