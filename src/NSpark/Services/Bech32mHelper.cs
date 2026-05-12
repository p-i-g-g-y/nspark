using System.Text;
using NSpark.Exceptions;

namespace NSpark.Services;

/// <summary>
/// Bech32m encoder / decoder used by Spark addresses, Bitcoin segwit
/// addresses, and Spark token identifiers. Implements BIP-350 with arbitrary
/// human-readable parts.
/// </summary>
public static class Bech32mHelper
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Bech32mConst = 0x2bc830a3;

    private static readonly int[] CharsetLookup = BuildCharsetLookup();

    /// <summary>
    /// Encode raw bytes as a Bech32m string under the given HRP.
    /// </summary>
    public static string Encode(string hrp, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(hrp);
        ArgumentNullException.ThrowIfNull(data);

        var words = ConvertBits(data, fromBits: 8, toBits: 5, pad: true)
            ?? throw new SparkConfigurationException("bech32m.encode", "Failed to convert bytes to 5-bit words.");
        var checksum = CreateChecksum(hrp, words);

        var sb = new StringBuilder(hrp.Length + 1 + words.Length + 6);
        sb.Append(hrp);
        sb.Append('1');
        foreach (var w in words)
        {
            sb.Append(Charset[w]);
        }
        foreach (var c in checksum)
        {
            sb.Append(Charset[c]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decode a Bech32m string into its HRP and raw byte payload.
    /// </summary>
    /// <param name="bech32m">The encoded string (case-insensitive).</param>
    /// <param name="limit">Maximum allowed encoded length. Defaults to 90 for BIP-350; pass 500 for Spark token identifiers.</param>
    /// <exception cref="SparkConfigurationException">
    /// Thrown when the string is malformed, exceeds <paramref name="limit"/>,
    /// contains invalid characters, or fails the checksum.
    /// </exception>
    public static (string Hrp, byte[] Data) Decode(string bech32m, int limit = 90)
    {
        ArgumentException.ThrowIfNullOrEmpty(bech32m);

        if (bech32m.Length > limit)
        {
            throw new SparkConfigurationException(
                "bech32m.decode",
                $"Bech32m string is {bech32m.Length} chars, max is {limit}.");
        }

        var lower = bech32m.ToLowerInvariant();
        var sepIdx = lower.LastIndexOf('1');
        if (sepIdx < 1 || sepIdx + 7 > lower.Length)
        {
            throw new SparkConfigurationException(
                "bech32m.decode",
                "Bech32m string missing or misplaced '1' separator.");
        }

        var hrp = lower[..sepIdx];
        var dataStr = lower[(sepIdx + 1)..];

        if (dataStr.Length < 6)
        {
            throw new SparkConfigurationException("bech32m.decode", "Bech32m data section is shorter than 6 chars.");
        }

        var words = new byte[dataStr.Length];
        for (int i = 0; i < dataStr.Length; i++)
        {
            var idx = (int)dataStr[i] < CharsetLookup.Length ? CharsetLookup[dataStr[i]] : -1;
            if (idx < 0)
            {
                throw new SparkConfigurationException(
                    "bech32m.decode",
                    $"Invalid bech32m character '{dataStr[i]}' at position {sepIdx + 1 + i}.");
            }
            words[i] = (byte)idx;
        }

        if (!VerifyChecksum(hrp, words))
        {
            throw new SparkConfigurationException("bech32m.decode", "Invalid bech32m checksum.");
        }

        // Strip the 6-word checksum suffix, convert remaining 5-bit words back to bytes.
        var payloadWords = words[..^6];
        var bytes = ConvertBits(payloadWords, fromBits: 5, toBits: 8, pad: false)
            ?? throw new SparkConfigurationException("bech32m.decode", "Bech32m payload contains illegal padding.");

        return (hrp, bytes);
    }

    /// <summary>
    /// Convert a byte array between two bit-widths (e.g., 8-bit to 5-bit for
    /// Bech32m encoding). Returns <c>null</c> when <paramref name="pad"/> is
    /// false and the input cannot be evenly converted (i.e., the source
    /// contained illegal padding bits).
    /// </summary>
    internal static byte[]? ConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var maxv = (1 << toBits) - 1;
        var result = new List<byte>(data.Length * fromBits / toBits + 1);
        foreach (var value in data)
        {
            acc = (acc << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((acc >> bits) & maxv));
            }
        }
        if (pad)
        {
            if (bits > 0)
            {
                result.Add((byte)((acc << (toBits - bits)) & maxv));
            }
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
        {
            return null;
        }
        return result.ToArray();
    }

    private static byte[] CreateChecksum(string hrp, byte[] data)
    {
        var hrpExpanded = HrpExpand(hrp);
        var values = new byte[hrpExpanded.Length + data.Length + 6];
        Array.Copy(hrpExpanded, 0, values, 0, hrpExpanded.Length);
        Array.Copy(data, 0, values, hrpExpanded.Length, data.Length);

        var polymod = Polymod(values) ^ Bech32mConst;
        var checksum = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            checksum[i] = (byte)((polymod >> (5 * (5 - i))) & 0x1F);
        }
        return checksum;
    }

    private static bool VerifyChecksum(string hrp, byte[] data)
    {
        var hrpExpanded = HrpExpand(hrp);
        var values = new byte[hrpExpanded.Length + data.Length];
        Array.Copy(hrpExpanded, 0, values, 0, hrpExpanded.Length);
        Array.Copy(data, 0, values, hrpExpanded.Length, data.Length);
        return Polymod(values) == Bech32mConst;
    }

    private static byte[] HrpExpand(string hrp)
    {
        var result = new byte[(hrp.Length * 2) + 1];
        for (int i = 0; i < hrp.Length; i++)
        {
            result[i] = (byte)(hrp[i] >> 5);
        }
        result[hrp.Length] = 0;
        for (int i = 0; i < hrp.Length; i++)
        {
            result[hrp.Length + 1 + i] = (byte)(hrp[i] & 0x1F);
        }
        return result;
    }

    private static uint Polymod(byte[] values)
    {
        uint chk = 1;
        ReadOnlySpan<uint> gen = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1FFFFFF) << 5) ^ v;
            for (int i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) == 1)
                {
                    chk ^= gen[i];
                }
            }
        }
        return chk;
    }

    private static int[] BuildCharsetLookup()
    {
        var lookup = new int[128];
        Array.Fill(lookup, -1);
        for (int i = 0; i < Charset.Length; i++)
        {
            lookup[Charset[i]] = i;
        }
        return lookup;
    }
}
