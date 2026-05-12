namespace NSpark.Exceptions;

/// <summary>
/// Lightning-domain failure: invoice handling, BOLT11 decoding,
/// payment routing, HTLC construction, SSP swap. Subclasses encode the specific
/// failure mode for callers that want to react differently per case.
/// </summary>
public class SparkLightningException : SparkException
{
    /// <inheritdoc />
    public SparkLightningException(string operation, string message)
        : base(operation, message)
    {
    }

    /// <inheritdoc />
    public SparkLightningException(string operation, string message, Exception innerException)
        : base(operation, message, innerException)
    {
    }
}

/// <summary>The provided BOLT11 payment request could not be decoded.</summary>
public sealed class InvalidBolt11Exception : SparkLightningException
{
    /// <summary>The payment request string that failed to decode.</summary>
    public string? PaymentRequest { get; init; }

    /// <inheritdoc />
    public InvalidBolt11Exception(string operation, string message)
        : base(operation, message)
    {
    }

    /// <inheritdoc />
    public InvalidBolt11Exception(string operation, string message, Exception innerException)
        : base(operation, message, innerException)
    {
    }
}

/// <summary>
/// The wallet has insufficient balance to fund the requested Lightning payment
/// (after fees and any required swap). Not retryable without funding the wallet.
/// </summary>
public sealed class InsufficientFundsException : SparkLightningException
{
    /// <summary>Amount the wallet attempted to spend, in satoshis.</summary>
    public long RequestedSats { get; init; }

    /// <summary>Amount the wallet had available, in satoshis.</summary>
    public long AvailableSats { get; init; }

    /// <inheritdoc />
    public InsufficientFundsException(string operation, string message)
        : base(operation, message)
    {
    }
}

/// <summary>The remote SSP rejected or failed the payment after the HTLC was built.</summary>
public sealed class PaymentFailedException : SparkLightningException
{
    /// <summary>The reason string surfaced by the SSP, when available.</summary>
    public string? Reason { get; init; }

    /// <inheritdoc />
    public PaymentFailedException(string operation, string message)
        : base(operation, message)
    {
    }

    /// <inheritdoc />
    public PaymentFailedException(string operation, string message, Exception innerException)
        : base(operation, message, innerException)
    {
    }
}

/// <summary>The invoice the wallet attempted to operate on has expired.</summary>
public sealed class InvoiceExpiredException : SparkLightningException
{
    /// <summary>The expiration timestamp, when known.</summary>
    public DateTimeOffset? ExpiredAt { get; init; }

    /// <inheritdoc />
    public InvoiceExpiredException(string operation, string message)
        : base(operation, message)
    {
    }
}
