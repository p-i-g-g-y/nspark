using System.Diagnostics.CodeAnalysis;

namespace NSpark.Diagnostics;

/// <summary>
/// Wrapper that hides a sensitive value from string formatting / structured logs
/// while keeping it usable inside the SDK. Calling <c>ToString()</c> always returns
/// <c>"***"</c>; access the underlying value via <see cref="Value"/>.
/// </summary>
/// <remarks>
/// Use for preimages, mnemonics, xprivs, FROST shares, bearer tokens, ECIES
/// ciphertexts, HMACs, and anything else that must never appear in logs.
/// This is a defense-in-depth tool: code paths that explicitly call
/// <see cref="Value"/> still need to be reviewed.
/// </remarks>
/// <typeparam name="T">Underlying value type.</typeparam>
public readonly struct Sensitive<T> : IEquatable<Sensitive<T>>
{
    private readonly T _value;

    /// <summary>Wrap a value as <see cref="Sensitive{T}"/>.</summary>
    public Sensitive(T value)
    {
        _value = value;
    }

    /// <summary>The underlying value. Use only in code paths that have a reviewed need.</summary>
    [SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = "Property name 'Value' is reserved by struct semantics; this is the intentional escape hatch.")]
    public T Value => _value;

    /// <inheritdoc />
    public override string ToString() => "***";

    /// <inheritdoc />
    public bool Equals(Sensitive<T> other) => EqualityComparer<T>.Default.Equals(_value, other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Sensitive<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value?.GetHashCode() ?? 0;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Sensitive<T> left, Sensitive<T> right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Sensitive<T> left, Sensitive<T> right) => !left.Equals(right);

    /// <summary>Implicit wrap.</summary>
    public static implicit operator Sensitive<T>(T value) => new(value);
}

/// <summary>Static helpers for redacting sensitive byte arrays in logs.</summary>
public static class SensitiveFormat
{
    /// <summary>
    /// Render a byte array as <c>"&lt;redacted N bytes&gt;"</c>. Use in log message
    /// templates when a value cannot be omitted entirely but its bytes must not leak.
    /// </summary>
    public static string Redact(ReadOnlySpan<byte> bytes) => $"<redacted {bytes.Length} bytes>";

    /// <summary>Short fingerprint (first 4 bytes) for diagnostic correlation only.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return "<redacted>";
        }

        return $"<{bytes[0]:x2}{bytes[1]:x2}{bytes[2]:x2}{bytes[3]:x2}…>";
    }
}
