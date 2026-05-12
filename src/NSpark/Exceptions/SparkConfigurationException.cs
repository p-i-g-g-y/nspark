namespace NSpark.Exceptions;

/// <summary>
/// The SDK was used or configured incorrectly by the host application
/// (missing options, malformed inputs, illegal call sequences). Never retryable.
/// </summary>
public sealed class SparkConfigurationException : SparkException
{
    /// <inheritdoc />
    public SparkConfigurationException(string operation, string message)
        : base(operation, message)
    {
    }

    /// <inheritdoc />
    public SparkConfigurationException(string operation, string message, Exception innerException)
        : base(operation, message, innerException)
    {
    }
}
