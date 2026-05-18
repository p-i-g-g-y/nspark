namespace NSpark.Services;

/// <summary>
/// Internal constants shared across NSpark services. Kept in one place so all flows that
/// construct refund txs deduct the same Bitcoin fee from the output value — the SSP enforces
/// this on every refund-tx output it validates (see e.g. <c>WithdrawalService</c>'s
/// cooperative-exit path, which validates all three refund branches).
/// </summary>
internal static class SparkConstants
{
    /// <summary>
    /// Default Bitcoin-network fee (in satoshis) deducted from each refund-tx output value.
    /// Computed as <c>ESTIMATED_REFUND_TX_VSIZE (191) × DEFAULT_SATS_PER_VBYTE (5) = 955</c>.
    /// </summary>
    public const ulong DefaultRefundFeeSats = 191UL * 5UL;
}
