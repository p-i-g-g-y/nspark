namespace NSpark.Exceptions;

/// <summary>
/// Authentication with a Signing Operator or SSP failed (bad signature,
/// expired challenge, or rejected identity). Retry only after refreshing
/// or rotating identity material.
/// </summary>
public sealed class SparkAuthenticationException : SparkException
{
    /// <summary>The endpoint that rejected the credentials, when known.</summary>
    public string? Endpoint { get; init; }

    /// <inheritdoc />
    public SparkAuthenticationException(string operation, string message)
        : base(operation, message)
    {
    }

    /// <inheritdoc />
    public SparkAuthenticationException(string operation, string message, Exception innerException)
        : base(operation, message, innerException)
    {
    }
}
