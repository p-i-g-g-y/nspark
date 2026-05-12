namespace NSpark.Exceptions;

/// <summary>
/// A signing operation failed inside the local <c>ISparkSigner</c> implementation
/// (BIP-39/32 derivation, ECDSA signing, FROST round). Not retryable in general —
/// the failure usually indicates corrupted key material or a broken native binding.
/// </summary>
public sealed class SparkSignerException : SparkException
{
    /// <inheritdoc />
    public SparkSignerException(string operation, string message)
        : base(operation, message)
    {
    }

    /// <inheritdoc />
    public SparkSignerException(string operation, string message, Exception innerException)
        : base(operation, message, innerException)
    {
    }
}
