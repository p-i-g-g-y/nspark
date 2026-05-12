using System.Text.Json;

namespace NSpark.Services;

/// <inheritdoc/>
public static class LightningAddressService
{
    /// <summary>
    /// Pay a Lightning address (user@domain) by resolving it via LNURL-pay protocol:
    /// 1. GET https://domain/.well-known/lnurlp/user → { callback, minSendable, maxSendable }
    /// 2. GET callback?amount={millisats} → { pr: "lnbc..." }
    /// 3. Pay the BOLT11 invoice via PayLightningInvoiceAsync
    /// </summary>
    public static async Task<string> PayLightningAddressAsync(
        this SparkWallet wallet,
        string lightningAddress,
        long amountSats,
        long? maxFeeSats = null,
        CancellationToken ct = default)
    {
        var parts = lightningAddress.Split('@', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Invalid Lightning address format: {lightningAddress}");
        }

        var user = parts[0];
        var domain = parts[1];
        var http = wallet.Client.HttpClient;

        // Step 1: Fetch LNURL-pay metadata
        var metadataUrl = $"https://{domain}/.well-known/lnurlp/{user}";
        var metadataJson = await http.GetStringAsync(metadataUrl, ct).ConfigureAwait(false);
        using var metadata = JsonDocument.Parse(metadataJson);
        var root = metadata.RootElement;

        var callback = root.GetProperty("callback").GetString()
            ?? throw new InvalidOperationException("LNURL-pay response missing 'callback'.");
        var minSendable = root.GetProperty("minSendable").GetInt64();
        var maxSendable = root.GetProperty("maxSendable").GetInt64();

        var amountMsats = amountSats * 1000;
        if (amountMsats < minSendable || amountMsats > maxSendable)
        {
            throw new InvalidOperationException(
                $"Amount {amountSats} sats ({amountMsats} msats) outside allowed range [{minSendable}, {maxSendable}] msats.");
        }

        // Step 2: Request invoice from callback
        var separator = callback.Contains('?') ? "&" : "?";
        var invoiceUrl = $"{callback}{separator}amount={amountMsats}";
        var invoiceJson = await http.GetStringAsync(invoiceUrl, ct).ConfigureAwait(false);
        using var invoiceDoc = JsonDocument.Parse(invoiceJson);

        var pr = invoiceDoc.RootElement.GetProperty("pr").GetString()
            ?? throw new InvalidOperationException("LNURL-pay callback response missing 'pr'.");

        // Step 3: Pay the invoice
        return await wallet.PayLightningInvoiceAsync(pr, maxFeeSats, ct).ConfigureAwait(false);
    }
}
