namespace NSpark.Models;

/// <summary>
/// Base type for events emitted by the Signing Operator event stream.
/// Pattern-match concrete subclasses to handle specific event categories.
/// </summary>
public abstract record SparkEvent;

/// <summary>An incoming Spark-to-Spark transfer is ready to be claimed.</summary>
/// <param name="Transfer">The transfer descriptor.</param>
public sealed record TransferReceivedEvent(SparkTransfer Transfer) : SparkEvent;

/// <summary>An on-chain deposit has confirmed and is ready to be claimed.</summary>
/// <param name="TreeId">Identifier of the resulting Spark tree.</param>
public sealed record DepositConfirmedEvent(string TreeId) : SparkEvent;

/// <summary>The event stream connection has been established.</summary>
public sealed record ConnectedEvent : SparkEvent;
