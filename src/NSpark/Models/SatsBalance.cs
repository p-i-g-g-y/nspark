namespace NSpark.Models;

/// <summary>
/// Three-way breakdown of the satoshi value held in a Spark wallet.
/// Matches the Swift / Kotlin / TS Spark SDKs' <c>SatsBalance</c> shape.
/// </summary>
/// <param name="Available">
/// Immediately spendable satoshis — sum of leaves with status <c>"AVAILABLE"</c>.
/// </param>
/// <param name="Owned">
/// All satoshis owned by the wallet: <see cref="Available"/> plus value locked in
/// in-flight outgoing transfers, swaps, or renewals (<c>TRANSFER_LOCKED</c>,
/// <c>SPLIT_LOCKED</c>, <c>AGGREGATE_LOCK</c>, <c>RENEW_LOCKED</c>).
/// </param>
/// <param name="Incoming">
/// Sats arriving but not yet spendable: pending inbound transfers waiting to be
/// claimed plus deposit leaves in the <c>CREATING</c> state.
/// </param>
public sealed record SatsBalance(long Available, long Owned, long Incoming);
