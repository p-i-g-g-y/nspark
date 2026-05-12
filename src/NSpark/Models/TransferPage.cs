namespace NSpark.Models;

/// <summary>
/// A page of <see cref="SparkTransfer"/> records plus the offset to use for the next page.
/// </summary>
/// <param name="Transfers">Transfers on this page.</param>
/// <param name="Offset">Continuation offset for the next page.</param>
public sealed record TransferPage(
    IReadOnlyList<SparkTransfer> Transfers,
    long Offset);
