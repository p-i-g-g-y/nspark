namespace NSpark.Models;

/// <summary>
/// Quote returned by the SSP for an upcoming static-deposit claim.
/// </summary>
/// <param name="CreditAmountSats">Amount in satoshis that will be credited to the wallet after the SSP fee.</param>
/// <param name="Signature">SSP-issued signature authorizing the claim against the quoted amount.</param>
public sealed record DepositFeeEstimate(long CreditAmountSats, string Signature);
