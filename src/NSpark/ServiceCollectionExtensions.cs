using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NSpark;

/// <summary>
/// Dependency-injection helpers for registering the SDK in
/// <see cref="IServiceCollection"/>-based hosts (ASP.NET Core, generic host).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="SparkConnection"/> as a singleton with the given configuration.
    /// </summary>
    /// <remarks>
    /// Registers the singleton client, a named <see cref="HttpClient"/> via
    /// <see cref="IHttpClientFactory"/>, and adds <c>Microsoft.Extensions.Logging</c>
    /// if not already present. Wire up an <see cref="ILoggerProvider"/> in the
    /// host to receive structured logs via <see cref="ILogger{T}"/>.
    /// </remarks>
    public static IServiceCollection AddSpark(
        this IServiceCollection services,
        Action<SparkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.Configure<SparkOptions>(_ => { });
        }

        // Ensure logging is available — hosts that already called AddLogging() are unaffected.
        services.AddLogging();

        services.AddHttpClient(nameof(SparkConnection));

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SparkOptions>>();
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var httpClient = httpClientFactory.CreateClient(nameof(SparkConnection));
            return new SparkConnection(options, httpClient, loggerFactory);
        });

        return services;
    }
}
