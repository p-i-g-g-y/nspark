namespace NSpark.Models;

/// <summary>
/// Estimated fee for an on-chain operation, returned by the SO / SSP fee oracle.
/// </summary>
/// <param name="FeeSats">Absolute fee in satoshis.</param>
/// <param name="FeeRateSatsPerVbyte">Fee rate in satoshis per virtual byte.</param>
public sealed record FeeQuote(long FeeSats, long FeeRateSatsPerVbyte);
