using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="SparkTaggedHash"/> — the BIP-340-style tagged
/// SHA-256 hasher used across signing payloads.
/// </summary>
/// <remarks>
/// We reconstruct the expected hash from the documented formula
/// <c>SHA256(tagHash ‖ tagHash ‖ length-prefixed data...)</c> and assert
/// equality. Any change to either the tag construction or the
/// length-prefix scheme will be caught here.
/// </remarks>
[TestFixture]
public sealed class SparkTaggedHashTests
{
    [Test]
    public void Hash_with_single_tag_part_and_no_data_matches_reference_formula()
    {
        var tagHash = ComputeTagHash("spark");
        var expected = SHA256.HashData(Concat(tagHash, tagHash));

        var actual = SparkTaggedHash.Create("spark").Hash();
        actual.Should().Equal(expected);
    }

    [Test]
    public void Hash_with_multiple_tag_parts_uses_length_prefixed_concatenation()
    {
        var tagHash = ComputeTagHash("spark", "claim");
        var expected = SHA256.HashData(Concat(tagHash, tagHash));

        var actual = SparkTaggedHash.Create("spark", "claim").Hash();
        actual.Should().Equal(expected);
    }

    [Test]
    public void AddBytes_appends_length_prefix_then_payload()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var tagHash = ComputeTagHash("spark");
        var expected = SHA256.HashData(Concat(tagHash, tagHash, LengthPrefix(data.Length), data));

        var actual = SparkTaggedHash.Create("spark").AddBytes(data).Hash();
        actual.Should().Equal(expected);
    }

    [Test]
    public void AddUInt64_appends_length_8_then_value_big_endian()
    {
        const ulong value = 0xDEADBEEFCAFEBABEUL;
        var tagHash = ComputeTagHash("spark");
        var valueBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(valueBytes, value);
        var expected = SHA256.HashData(Concat(tagHash, tagHash, LengthPrefix(8), valueBytes));

        var actual = SparkTaggedHash.Create("spark").AddUInt64(value).Hash();
        actual.Should().Equal(expected);
    }

    [Test]
    public void Hash_is_deterministic_for_same_inputs()
    {
        var a = SparkTaggedHash.Create("spark").AddBytes([0xab, 0xcd]).AddUInt64(42).Hash();
        var b = SparkTaggedHash.Create("spark").AddBytes([0xab, 0xcd]).AddUInt64(42).Hash();
        a.Should().Equal(b);
    }

    [Test]
    public void Hash_is_sensitive_to_tag_part_order()
    {
        var a = SparkTaggedHash.Create("spark", "claim").AddBytes([0x01]).Hash();
        var b = SparkTaggedHash.Create("claim", "spark").AddBytes([0x01]).Hash();
        a.Should().NotEqual(b);
    }

    [Test]
    public void Hash_is_sensitive_to_data_field_order()
    {
        var a = SparkTaggedHash.Create("spark").AddBytes([0x01]).AddUInt64(99).Hash();
        var b = SparkTaggedHash.Create("spark").AddUInt64(99).AddBytes([0x01]).Hash();
        a.Should().NotEqual(b);
    }

    [Test]
    public void Hash_returns_32_bytes()
    {
        var hash = SparkTaggedHash.Create("spark").AddUInt64(1).Hash();
        hash.Length.Should().Be(32);
    }

    [Test]
    public void AddMapStringToBytes_sorts_entries_lexicographically_before_hashing()
    {
        // Two maps with the same entries inserted in different orders must
        // hash identically because AddMapStringToBytes sorts by key bytes.
        var unordered = new Dictionary<string, ByteString>
        {
            ["zebra"] = ByteString.CopyFrom([0xff]),
            ["alpha"] = ByteString.CopyFrom([0x01]),
            ["mango"] = ByteString.CopyFrom([0x7f]),
        };
        var reordered = new Dictionary<string, ByteString>
        {
            ["mango"] = ByteString.CopyFrom([0x7f]),
            ["alpha"] = ByteString.CopyFrom([0x01]),
            ["zebra"] = ByteString.CopyFrom([0xff]),
        };

        var hashA = SparkTaggedHash.Create("spark").AddMapStringToBytes(unordered).Hash();
        var hashB = SparkTaggedHash.Create("spark").AddMapStringToBytes(reordered).Hash();
        hashA.Should().Equal(hashB);
    }

    // --- helpers that mirror SparkTaggedHash's documented contract ---

    private static byte[] ComputeTagHash(params string[] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            var utf8 = Encoding.UTF8.GetBytes(part);
            h.AppendData(LengthPrefix(utf8.Length));
            h.AppendData(utf8);
        }
        return h.GetHashAndReset();
    }

    private static byte[] LengthPrefix(int length)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, (ulong)length);
        return buf;
    }

    private static byte[] Concat(params byte[][] segments)
    {
        var size = segments.Sum(s => s.Length);
        var result = new byte[size];
        var offset = 0;
        foreach (var s in segments)
        {
            Buffer.BlockCopy(s, 0, result, offset, s.Length);
            offset += s.Length;
        }
        return result;
    }
}
