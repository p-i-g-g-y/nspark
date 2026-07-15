namespace NSpark.Models;

/// <summary>
/// Outcome of a renewal sweep. Renewals are per-leaf and best-effort: one
/// failing leaf never aborts the rest.
/// </summary>
/// <param name="Checked">Total leaves inspected for a low refund timelock.</param>
/// <param name="Renewed">Leaves successfully renewed.</param>
/// <param name="Failures"><c>"leafId: error"</c> for each leaf that could not be renewed.</param>
public sealed record SparkLeafRenewal(int Checked, int Renewed, IReadOnlyList<string> Failures);
