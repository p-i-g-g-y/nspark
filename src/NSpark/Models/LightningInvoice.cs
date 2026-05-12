namespace NSpark.Models;

/// <summary>
/// A Lightning Network invoice issued by a Spark wallet — the encoded BOLT11
/// payment request plus the metadata most consumers need.
/// </summary>
/// <param name="PaymentRequest">The BOLT11-encoded payment request string (<c>lnbc…</c>, <c>lntb…</c>, or <c>lnbcrt…</c>).</param>
/// <param name="PaymentHash">SHA-256 of the HTLC preimage, hex-encoded (64 chars).</param>
/// <param name="AmountSats">Amount requested, in satoshis.</param>
/// <param name="ExpiresAt">Time at which the invoice expires and can no longer be paid.</param>
/// <param name="RequestId">Optional SSP-side request identifier for status lookups.</param>
public sealed record LightningInvoice(
    string PaymentRequest,
    string PaymentHash,
    long AmountSats,
    DateTimeOffset ExpiresAt,
    string? RequestId = null);
