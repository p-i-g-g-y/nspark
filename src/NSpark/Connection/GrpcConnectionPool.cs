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
    private readonly FrozenDictionary<string, GrpcChannel> _channels;

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
    }
}
