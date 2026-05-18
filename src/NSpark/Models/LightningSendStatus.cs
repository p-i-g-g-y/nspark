namespace NSpark.Models;

/// <summary>
/// Status snapshot of an outgoing Lightning payment as reported by the SSP.
/// </summary>
/// <param name="PaymentHash">SHA-256 of the HTLC preimage (lowercase hex, 64 chars).</param>
/// <param name="Status">SSP-reported status string. Known values include <c>CREATED</c>, <c>PENDING</c>,
/// <c>SUCCEEDED</c>, <c>FAILED</c>. Treat unknown values as still in flight.</param>
/// <param name="FeeSats">Actual fee paid in satoshis, populated when the payment settles. Null while pending.</param>
/// <param name="Preimage">HTLC preimage (lowercase hex, 64 chars), populated when the payment settles. Null while pending.</param>
public sealed record LightningSendStatus(
    string PaymentHash,
    string Status,
    long? FeeSats,
    string? Preimage);
