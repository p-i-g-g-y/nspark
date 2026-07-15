using System.Collections.Concurrent;
using System.Collections.Frozen;
using Grpc.Net.Client;
using NSpark.Proto;
using NSpark.Proto.Authn;
using NSpark.Proto.Token;

namespace NSpark.Connection;

/// <summary>
/// Maintains one GrpcChannel per Signing Operator address. Channels are shared across all wallets.
/// HTTP/2 multiplexing means one channel per SO is sufficient.
/// </summary>
internal sealed class GrpcConnectionPool : IDisposable
{
    /// <summary>
    /// Size cap for the dedicated recovery-query channels. query_nodes with
    /// include_parents returns every leaf's full ancestor chain in one message,
    /// and long-lived wallets exceed the 4 MiB transport default (seen live:
    /// 6.9 MB → ResourceExhausted).
    /// </summary>
    private const int LargeMessageMaxBytes = 128 * 1024 * 1024;

    private readonly FrozenDictionary<string, GrpcChannel> _channels;
    private readonly ConcurrentDictionary<string, Lazy<GrpcChannel>> _largeMessageChannels = new();

    public GrpcConnectionPool(IReadOnlyList<string> soAddresses)
    {
        var dict = new Dictionary<string, GrpcChannel>(soAddresses.Count);
        foreach (var address in soAddresses)
        {
            dict[address] = GrpcChannel.ForAddress(address);
        }
        _channels = dict.ToFrozenDictionary();
    }

    public IReadOnlyList<string> Addresses => [.. _channels.Keys];

    public GrpcChannel GetChannel(string address)
    {
        if (_channels.TryGetValue(address, out var channel))
        {
            return channel;
        }

        throw new ArgumentException($"Unknown SO address: {address}");
    }

    public SparkService.SparkServiceClient GetSparkClient(string address)
    {
        return new SparkService.SparkServiceClient(GetChannel(address));
    }

    /// <summary>
    /// SparkService client on a dedicated channel with 128 MiB send/receive
    /// caps, created lazily per address. Grpc.Net.Client only supports message
    /// size limits at the channel level, so recovery-snapshot queries use these
    /// channels instead of raising the limit for every call in the pool.
    /// </summary>
    public SparkService.SparkServiceClient GetLargeMessageSparkClient(string address)
    {
        if (!_channels.ContainsKey(address))
        {
            throw new ArgumentException($"Unknown SO address: {address}");
        }

        var channel = _largeMessageChannels.GetOrAdd(
            address,
            static a => new Lazy<GrpcChannel>(() => GrpcChannel.ForAddress(a, new GrpcChannelOptions
            {
                MaxReceiveMessageSize = LargeMessageMaxBytes,
                MaxSendMessageSize = LargeMessageMaxBytes,
            })));
        return new SparkService.SparkServiceClient(channel.Value);
    }

    public SparkAuthnService.SparkAuthnServiceClient GetAuthnClient(string address)
    {
        return new SparkAuthnService.SparkAuthnServiceClient(GetChannel(address));
    }

    public SparkTokenService.SparkTokenServiceClient GetTokenClient(string address)
    {
        return new SparkTokenService.SparkTokenServiceClient(GetChannel(address));
    }

    /// <summary>
    /// Returns SparkServiceClients for all SOs.
    /// </summary>
    public IReadOnlyList<SparkService.SparkServiceClient> GetAllSparkClients()
    {
        return _channels.Values.Select(ch => new SparkService.SparkServiceClient(ch)).ToList();
    }

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }

        foreach (var lazyChannel in _largeMessageChannels.Values)
        {
            if (lazyChannel.IsValueCreated)
            {
                lazyChannel.Value.Dispose();
            }
        }
    }
}
