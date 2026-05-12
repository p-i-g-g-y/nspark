using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace NSpark.Services;

/// <summary>
/// BIP-340-style tagged SHA-256 hasher matching the Spark JS SDK's <c>newHasher()</c> /
/// <c>HashStructure</c> implementation.
///
/// Tag is computed as: SHA256(len‖tag₁ ‖ len‖tag₂ ‖ …)
/// Final hash is:       SHA256(tagHash ‖ tagHash ‖ data)
///
/// Every value is length-prefixed with an 8-byte big-endian uint64.
/// </summary>
internal sealed class SparkTaggedHash
{
    private readonly IncrementalHash _hash;

    private SparkTaggedHash(byte[] tagHash)
    {
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _hash.AppendData(tagHash);  // first copy
        _hash.AppendData(tagHash);  // second copy (BIP-340 pattern)
    }

    /// <summary>
    /// Create a tagged hasher with the given domain tag parts (e.g. "spark", "claim", "signing payload").
    /// </summary>
    public static SparkTaggedHash Create(params string[] tagParts)
    {
        using var tagHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lenBuf = stackalloc byte[8];
        foreach (var part in tagParts)
        {
            var utf8 = Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteUInt64BigEndian(lenBuf, (ulong)utf8.Length);
            tagHasher.AppendData(lenBuf);
            tagHasher.AppendData(utf8);
        }
        var tagHash = tagHasher.GetHashAndReset();
        return new SparkTaggedHash(tagHash);
    }

    /// <summary>Append length-prefixed raw bytes.</summary>
    public SparkTaggedHash AddBytes(byte[] data)
    {
        Span<byte> lenBuf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(lenBuf, (ulong)data.Length);
        _hash.AppendData(lenBuf);
        _hash.AppendData(data);
        return this;
    }

    /// <summary>Append a length-prefixed uint64 value.</summary>
    public SparkTaggedHash AddUInt64(ulong value)
    {
        // addUint64 → addValue which wraps with a length prefix of the value size (8 bytes)
        Span<byte> lenBuf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(lenBuf, 8UL); // length of uint64 = 8
        _hash.AppendData(lenBuf);
        BinaryPrimitives.WriteUInt64BigEndian(lenBuf, value);
        _hash.AppendData(lenBuf);
        return this;
    }

    /// <summary>
    /// Append a map of string→bytes (sorted by key bytes lexicographically).
    /// Writes: uint64(entryCount), then for each entry: lenPrefixed(keyUTF8) + lenPrefixed(valueBytes).
    /// </summary>
    public SparkTaggedHash AddMapStringToBytes(Dictionary<string, ByteString> map)
    {
        // Sort by raw key bytes (lexicographic unsigned byte comparison)
        var sorted = map
            .Select(kv => (keyBytes: Encoding.UTF8.GetBytes(kv.Key), value: kv.Value))
            .OrderBy(x => x.keyBytes, ByteArrayComparer.Instance)
            .ToList();

        AddUInt64((ulong)sorted.Count);

        Span<byte> lenBuf = stackalloc byte[8];
        foreach (var (keyBytes, value) in sorted)
        {
            // Key
            BinaryPrimitives.WriteUInt64BigEndian(lenBuf, (ulong)keyBytes.Length);
            _hash.AppendData(lenBuf);
            _hash.AppendData(keyBytes);

            // Value
            var valueBytes = value.ToByteArray();
            BinaryPrimitives.WriteUInt64BigEndian(lenBuf, (ulong)valueBytes.Length);
            _hash.AppendData(lenBuf);
            _hash.AppendData(valueBytes);
        }

        return this;
    }

    /// <summary>Finalize and return the 32-byte hash.</summary>
    public byte[] Hash()
    {
        var result = _hash.GetHashAndReset();
        _hash.Dispose();
        return result;
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null && y is null)
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var len = Math.Min(x.Length, y.Length);
            for (int i = 0; i < len; i++)
            {
                var cmp = x[i].CompareTo(y[i]);
                if (cmp != 0)
                {
                    return cmp;
                }
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
