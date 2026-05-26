using System.Runtime.CompilerServices;
using Google.Protobuf;
using NSpark.Models;
using NSpark.Proto;

namespace NSpark.Services;

/// <inheritdoc/>
public static class EventService
{
    /// <summary>
    /// Subscribe to wallet events from the coordinator SO.
    /// Returns an IAsyncEnumerable that yields events as they arrive.
    /// </summary>
    public static async IAsyncEnumerable<SparkEvent> SubscribeEventsAsync(
        this SparkWallet wallet,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var soAddress = wallet.Client.Options.SigningOperatorAddresses[0];
        var client = wallet.Pool.GetSparkClient(soAddress);
        var headers = await wallet.GetAuthMetadataAsync(soAddress, ct).ConfigureAwait(false);

        using var stream = client.subscribe_to_events(
            new SubscribeToEventsRequest
            {
                IdentityPublicKey = ByteString.CopyFrom(wallet.IdentityPublicKey),
            },
            headers,
            cancellationToken: ct);

        while (await stream.ResponseStream.MoveNext(ct).ConfigureAwait(false))
        {
            var mapped = MapEvent(stream.ResponseStream.Current);
            if (mapped is not null)
            {
                yield return mapped;
            }
        }
    }

    private static SparkEvent? MapEvent(SubscribeToEventsResponse response) =>
        response.EventCase switch
        {
            SubscribeToEventsResponse.EventOneofCase.Connected => new Models.ConnectedEvent(),
            SubscribeToEventsResponse.EventOneofCase.ReceiverTransfer => MapTransferEvent(response.ReceiverTransfer),
            SubscribeToEventsResponse.EventOneofCase.SenderTransfer => MapTransferEvent(response.SenderTransfer),
            SubscribeToEventsResponse.EventOneofCase.Deposit => new DepositConfirmedEvent(
                response.Deposit.Deposit.TreeId),
            _ => null
        };

    private static TransferReceivedEvent MapTransferEvent(TransferEvent transferEvent)
    {
        var t = transferEvent.Transfer;
        return new TransferReceivedEvent(new SparkTransfer(
            Id: t.Id,
            SenderIdentityPublicKey: Convert.ToHexString(t.SenderIdentityPublicKey.ToByteArray()).ToLowerInvariant(),
            ReceiverIdentityPublicKey: Convert.ToHexString(t.ReceiverIdentityPublicKey.ToByteArray()).ToLowerInvariant(),
            TotalValueSats: (long)t.TotalValue,
            Status: t.Status.ToString(),
            CreatedAt: t.CreatedTime?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow));
    }
}
