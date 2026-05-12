namespace NSpark.Models;

/// <summary>
/// Strategy for choosing which token outputs to spend when assembling a
/// token transfer transaction.
/// </summary>
public enum TokenSelectionStrategy
{
    /// <summary>
    /// Prefer the smallest outputs first. Tends to consolidate dust but
    /// requires more outputs per transaction.
    /// </summary>
    SmallFirst = 0,

    /// <summary>
    /// Prefer the largest outputs first. Fewer outputs per transaction
    /// but leaves more dust behind.
    /// </summary>
    LargeFirst = 1,
}

/// <summary>Result of a token issuance / creation operation.</summary>
/// <param name="TransactionHash">Hex-encoded hash of the create transaction.</param>
/// <param name="TokenIdentifier">Bech32m identifier of the newly created token, when known.</param>
public sealed record TokenCreationResult(string TransactionHash, string? TokenIdentifier);

/// <summary>Result of a token transfer.</summary>
/// <param name="TransactionHash">Hex-encoded hash of the broadcast transfer transaction.</param>
public sealed record TokenTransferResult(string TransactionHash);
