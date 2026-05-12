namespace NSpark.Connection;

/// <summary>
/// Tracks clock offset between local time and Spark SO server time.
/// Offset is computed from the 'date' header in gRPC responses.
/// </summary>
internal sealed class ServerTimeSync
{
    private long _offsetTicks;

    /// <summary>
    /// Current estimated offset: server time - local time.
    /// </summary>
    public TimeSpan Offset => new(_offsetTicks);

    /// <summary>
    /// Current estimated server time.
    /// </summary>
    public DateTimeOffset ServerNow => DateTimeOffset.UtcNow + Offset;

    /// <summary>
    /// Update the offset from a server-provided date header value.
    /// </summary>
    public void UpdateFromHeader(string? dateHeaderValue)
    {
        if (dateHeaderValue is null)
        {
            return;
        }

        if (!DateTimeOffset.TryParse(dateHeaderValue, out var serverTime))
        {
            return;
        }

        var localNow = DateTimeOffset.UtcNow;
        var newOffset = serverTime - localNow;

        // Atomic update — good enough for approximate sync
        Interlocked.Exchange(ref _offsetTicks, newOffset.Ticks);
    }

    /// <summary>
    /// Get a Unix timestamp (seconds) adjusted for server clock.
    /// </summary>
    public long GetServerUnixSeconds() => ServerNow.ToUnixTimeSeconds();
}
