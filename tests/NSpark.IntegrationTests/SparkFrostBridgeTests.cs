using NSpark.Services;
using uniffi.spark_frost;

namespace NSpark.Tests;

/// <summary>
/// Validates that the C# UniFFI bridge to the Rust spark-frost library
/// produces the same results as the Rust unit tests with hardcoded test vectors.
/// </summary>
[TestFixture]
public class SparkFrostBridgeTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // HTLC transaction construction — matches Rust htlc.rs test vectors
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void ConstructHtlcTransaction_MatchesGoTestVector()
    {
        // From Rust: test_construct_htlc_transaction_matches_go
        // (which matches Go's TestCreateLightningHTLCTransaction_BuildsExpectedTxFromExpectedParams)

        var rawTxHex = "0300000000010180d6e3ba8082893627a42f2770fdb2e900731638258a2d04cd6b8b2f7a982e150000000000d0070040020002000000000000225120d04e30f634945d8b59283c10831cfab354d6d9cb88d1f7adfdba67cb8a7734f500000000000000000451024e730140ebcc474fdc71b83fe5f547976e418e91025ef8b323b572f68e709b82c36c7303496ee315c3b3b710af59c14f8d2aa97b9a0bc40b778385b32c59f7e0f34fabb200000000";
        var nodeTx = Convert.FromHexString(rawTxHex);

        var rawRefundTxHex = "03000000000101d4b9193b8a28d4a986a15f17f5fe4e310c1d73e34865a24d04d39e37dddaccff00000000006c0700000200020000000000002251200686f6870264df6673c066f0591d38b5d60636f4f7a58143b88cbdff327cb68000000000000000000451024e73014003bb8cccc5b494ac9eb2b510618e5c54bd0082c5c5ba0838c9411f3d432dd4a0ec59ec4b4274006a2761040d8aa54702bc01dfed165035c2beaa017e5acc79c100000000";
        var refundTxBytes = Convert.FromHexString(rawRefundTxHex);

        // Parse refund tx sequence (same as Rust test)
        var refundSequence = ClaimService.ParseInputSequence(refundTxBytes);
        var sequence = refundSequence - 30; // Go: sequence := rawRefundTx.TxIn[0].Sequence - 30

        var paymentHash = Convert.FromHexString("10d31aeabd2bf7cdcba3a229107a4edb7b1c5b35c90c2fca491bd127c68069bd");
        var hashlockPk = Convert.FromHexString("028c094a432d46a0ac95349d792c2e3730bd60c29188db716f56a99e39b95338b4");
        var seqlockPk = Convert.FromHexString("032f0db1a8b99ad42e75e2f1cf4d977511a6d94587b4482c77fbd1fe9acc456a27");

        var result = SparkFrostMethods.ConstructHtlcTransaction(
            nodeTx: nodeTx,
            vout: 0,
            sequence: sequence,
            paymentHash: paymentHash,
            hashlockPubkey: hashlockPk,
            seqlockPubkey: seqlockPk,
            htlcSequence: 2160,
            applyFee: false,
            feeSats: 955,
            network: "regtest");

        // Expected output from Go/Rust test
        var expectedTxHex = "03000000000101d4b9193b8a28d4a986a15f17f5fe4e310c1d73e34865a24d04d39e37dddaccff00000000004e0700000200020000000000002251207898ca6a523e1724e99e3f6eb9bbd36eba16e6b15304921854e3c6b1174574b200000000000000000451024e7301406edf601068e37dc1222de88f2cbceaf9bcaa391683a7f393a40a68dc37d8765a7fae02793c4c3981101d6f35a9b9cd3a901c5f109f58a76ffaf8838c80670b5900000000";
        var expectedTxBytes = Convert.FromHexString(expectedTxHex);

        // Compare output value: should be 512 sats (same as input, no fee)
        Assert.That(result.@tx, Is.Not.Null, "tx bytes should not be null");
        Assert.That(result.@sighash, Has.Length.EqualTo(32), "sighash should be 32 bytes");

        // Parse output value from result tx (output 0)
        var resultOutputValue = ParseOutputValue(result.@tx, 0);
        var expectedOutputValue = ParseOutputValue(expectedTxBytes, 0);
        Assert.That(resultOutputValue, Is.EqualTo(expectedOutputValue),
            $"Output value mismatch: result={resultOutputValue}, expected={expectedOutputValue}");

        // Compare the sequence in the input
        var resultSequence = ClaimService.ParseInputSequence(result.@tx);
        var expectedSequence = ClaimService.ParseInputSequence(expectedTxBytes);
        Assert.That(resultSequence, Is.EqualTo(expectedSequence),
            $"Sequence mismatch: result={resultSequence}, expected={expectedSequence}");
    }

    [Test]
    public void ConstructHtlcTransaction_Basic_NoFee()
    {
        // From Rust: test_construct_htlc_transaction_basic
        // Simple node tx with 100,000 sats, no fee applied
        var nodeTx = BuildSimpleNodeTx(100_000);

        var hash = new byte[32];
        Array.Fill(hash, (byte)0x11);

        var hashlockPk = Convert.FromHexString("0247997a5c32ccf934257a675c306bf6ec37019358240156628af62baad7066a83");
        var seqlockPk = Convert.FromHexString("03b66b574670a7b6bea89c0548903f70a6f059fd9abe737dc4c5aafe14a127408f");

        var result = SparkFrostMethods.ConstructHtlcTransaction(
            nodeTx: nodeTx,
            vout: 0,
            sequence: 12345,
            paymentHash: hash,
            hashlockPubkey: hashlockPk,
            seqlockPubkey: seqlockPk,
            htlcSequence: 2160,
            applyFee: false,
            feeSats: 955,
            network: "regtest");

        var outputValue = ParseOutputValue(result.@tx, 0);
        Assert.That(outputValue, Is.EqualTo(100_000UL), "No fee: output should equal input");
        Assert.That(result.@sighash, Has.Length.EqualTo(32));

        var seq = ClaimService.ParseInputSequence(result.@tx);
        Assert.That(seq, Is.EqualTo(12345u), "Sequence should match input");
    }

    [Test]
    public void ConstructHtlcTransaction_DirectSubtractsFee()
    {
        // From Rust: test_construct_htlc_direct_subtracts_fee
        var nodeTx = BuildSimpleNodeTx(50_000);

        var hash = new byte[32];
        Array.Fill(hash, (byte)0x22);

        var hashlockPk = Convert.FromHexString("0247997a5c32ccf934257a675c306bf6ec37019358240156628af62baad7066a83");
        var seqlockPk = Convert.FromHexString("03b66b574670a7b6bea89c0548903f70a6f059fd9abe737dc4c5aafe14a127408f");

        var result = SparkFrostMethods.ConstructHtlcTransaction(
            nodeTx: nodeTx,
            vout: 0,
            sequence: 54321,
            paymentHash: hash,
            hashlockPubkey: hashlockPk,
            seqlockPubkey: seqlockPk,
            htlcSequence: 2160,
            applyFee: true,
            feeSats: 955,
            network: "regtest");

        var outputValue = ParseOutputValue(result.@tx, 0);
        Assert.That(outputValue, Is.EqualTo(50_000UL - 955UL),
            $"Direct HTLC should subtract fee: expected {50_000 - 955}, got {outputValue}");
    }

    [Test]
    public void ConstructHtlcTransaction_WithCustomFee330()
    {
        // Test with fee=330 (our HtlcFeeSats constant) to verify it works
        var nodeTx = BuildSimpleNodeTx(512);

        var hash = new byte[32];
        Array.Fill(hash, (byte)0x33);

        var hashlockPk = Convert.FromHexString("0247997a5c32ccf934257a675c306bf6ec37019358240156628af62baad7066a83");
        var seqlockPk = Convert.FromHexString("03b66b574670a7b6bea89c0548903f70a6f059fd9abe737dc4c5aafe14a127408f");

        var result = SparkFrostMethods.ConstructHtlcTransaction(
            nodeTx: nodeTx,
            vout: 0,
            sequence: 1000,
            paymentHash: hash,
            hashlockPubkey: hashlockPk,
            seqlockPubkey: seqlockPk,
            htlcSequence: 2160,
            applyFee: true,
            feeSats: 330,
            network: "regtest");

        var outputValue = ParseOutputValue(result.@tx, 0);
        Assert.That(outputValue, Is.EqualTo(512UL - 330UL),
            $"Fee 330: expected {512 - 330}, got {outputValue}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Refund Tx Trio — matches Rust transaction.rs test vectors
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void ConstructRefundTxTrio_BasicWithDirect()
    {
        var nodeTx = BuildSimpleNodeTx(100_000);
        var pubkey = Convert.FromHexString("031cd7599775b6959193029794b04dcd99d257cbec008d63e49fdf0f89a5f7c231");

        // sequence=900, directSequence=950 (from next_sequence(1000))
        // Rust test uses DEFAULT_FEE_SATS = 955
        var result = SparkFrostMethods.ConstructRefundTxTrio(
            cpfpNodeTx: nodeTx,
            directNodeTx: nodeTx,
            vout: 0,
            receivingPubkey: pubkey,
            network: "regtest",
            sequence: 900,
            directSequence: 950,
            feeSats: 955);

        // CPFP refund: no fee, 2 outputs (value + anchor)
        Assert.That(result.@cpfpRefund, Is.Not.Null);
        var cpfpValue = ParseOutputValue(result.@cpfpRefund.@tx, 0);
        Assert.That(cpfpValue, Is.EqualTo(100_000UL), "CPFP refund should have full value");
        Assert.That(result.@cpfpRefund.@sighash, Has.Length.EqualTo(32));

        // Direct refund: fee applied, 1 output
        Assert.That(result.@directRefund, Is.Not.Null);
        var directValue = ParseOutputValue(result.@directRefund!.@tx, 0);
        var expectedDirectValue = 100_000UL - 955UL; // DEFAULT_FEE_SATS
        Assert.That(directValue, Is.EqualTo(expectedDirectValue),
            $"Direct refund should subtract DEFAULT_FEE_SATS: expected {expectedDirectValue}, got {directValue}");

        // DirectFromCpfp refund: fee applied, 1 output
        Assert.That(result.@directFromCpfpRefund, Is.Not.Null);
        var directFromCpfpValue = ParseOutputValue(result.@directFromCpfpRefund.@tx, 0);
        Assert.That(directFromCpfpValue, Is.EqualTo(expectedDirectValue),
            $"DirectFromCpfp should also subtract DEFAULT_FEE_SATS");
    }

    [Test]
    public void ConstructRefundTxTrio_NoDirect()
    {
        var nodeTx = BuildSimpleNodeTx(100_000);
        var pubkey = Convert.FromHexString("031cd7599775b6959193029794b04dcd99d257cbec008d63e49fdf0f89a5f7c231");

        var result = SparkFrostMethods.ConstructRefundTxTrio(
            cpfpNodeTx: nodeTx,
            directNodeTx: null,
            vout: 0,
            receivingPubkey: pubkey,
            network: "regtest",
            sequence: 900,
            directSequence: 950,
            feeSats: 0);

        Assert.That(result.@cpfpRefund, Is.Not.Null);
        Assert.That(result.@directRefund, Is.Null, "No direct refund when directNodeTx is null");
        Assert.That(result.@directFromCpfpRefund, Is.Not.Null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ECIES encrypt/decrypt round-trip
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void EciesEncryptDecrypt_RoundTrip()
    {
        // Generate a keypair using the bridge
        var privateKey = SparkFrostMethods.RandomSecretKeyBytes();
        var publicKey = SparkFrostMethods.GetPublicKeyBytes(privateKey, compressed: true);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("Hello, Spark FROST bridge!");

        var ciphertext = SparkFrostMethods.EncryptEcies(plaintext, publicKey);
        Assert.That(ciphertext, Is.Not.EqualTo(plaintext), "Ciphertext should differ from plaintext");

        var decrypted = SparkFrostMethods.DecryptEcies(ciphertext, privateKey);
        Assert.That(decrypted, Is.EqualTo(plaintext), "Decrypted should match original plaintext");
    }

    [Test]
    public void EciesEncryptDecrypt_EmptyMessage()
    {
        var privateKey = SparkFrostMethods.RandomSecretKeyBytes();
        var publicKey = SparkFrostMethods.GetPublicKeyBytes(privateKey, compressed: true);

        var plaintext = Array.Empty<byte>();

        var ciphertext = SparkFrostMethods.EncryptEcies(plaintext, publicKey);
        var decrypted = SparkFrostMethods.DecryptEcies(ciphertext, privateKey);
        Assert.That(decrypted, Is.EqualTo(plaintext));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // VSS split and recover
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void SplitSecretWithProofs_ProducesCorrectShareCount()
    {
        var secret = SparkFrostMethods.RandomSecretKeyBytes();
        uint threshold = 2;
        uint numShares = 3;

        var shares = SparkFrostMethods.SplitSecretWithProofsUniffi(secret, threshold, numShares);

        Assert.That(shares.Count, Is.EqualTo((int)numShares));

        // Each share should have index 1..numShares
        var indices = shares.Select(s => s.@index).OrderBy(i => i).ToList();
        Assert.That(indices, Is.EqualTo(new uint[] { 1, 2, 3 }),
            "Share indices should be 1-based sequential");

        // Each share should have proofs
        foreach (var share in shares)
        {
            Assert.That(share.@share, Has.Length.EqualTo(32), "Share should be 32 bytes");
            Assert.That(share.@proofs, Is.Not.Empty, "Each share should have proofs");
        }
    }

    [Test]
    public void SplitSecretWithProofs_ThresholdOf3In5()
    {
        var secret = SparkFrostMethods.RandomSecretKeyBytes();
        var shares = SparkFrostMethods.SplitSecretWithProofsUniffi(secret, threshold: 3, numShares: 5);

        Assert.That(shares.Count, Is.EqualTo(5));
        var indices = shares.Select(s => s.@index).OrderBy(i => i).ToList();
        Assert.That(indices, Is.EqualTo(new uint[] { 1, 2, 3, 4, 5 }));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sequence parsing (ParseInputSequence used by ClaimService)
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void ParseInputSequence_MatchesRustTestVector()
    {
        // The refund tx from the Go/Rust HTLC test
        var rawRefundTxHex = "03000000000101d4b9193b8a28d4a986a15f17f5fe4e310c1d73e34865a24d04d39e37dddaccff00000000006c0700000200020000000000002251200686f6870264df6673c066f0591d38b5d60636f4f7a58143b88cbdff327cb68000000000000000000451024e73014003bb8cccc5b494ac9eb2b510618e5c54bd0082c5c5ba0838c9411f3d432dd4a0ec59ec4b4274006a2761040d8aa54702bc01dfed165035c2beaa017e5acc79c100000000";
        var refundTxBytes = Convert.FromHexString(rawRefundTxHex);

        var sequence = ClaimService.ParseInputSequence(refundTxBytes);

        // Rust parses this as 0x076c = 1900, then sequence-30 = 1870 for the HTLC test
        // But ParseInputSequence returns the raw value from the tx: 0x076c = 1900
        Assert.That(sequence, Is.EqualTo(1900u),
            $"Refund tx sequence should be 1900, got {sequence} (0x{sequence:X4})");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public key derivation
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void GetPublicKeyBytes_Compressed33Bytes()
    {
        var privateKey = SparkFrostMethods.RandomSecretKeyBytes();
        var publicKey = SparkFrostMethods.GetPublicKeyBytes(privateKey, compressed: true);

        Assert.That(publicKey, Has.Length.EqualTo(33));
        Assert.That(publicKey[0], Is.EqualTo(2).Or.EqualTo(3),
            "Compressed pubkey should start with 02 or 03");
    }

    [Test]
    public void GetPublicKeyBytes_Uncompressed65Bytes()
    {
        var privateKey = SparkFrostMethods.RandomSecretKeyBytes();
        var publicKey = SparkFrostMethods.GetPublicKeyBytes(privateKey, compressed: false);

        Assert.That(publicKey, Has.Length.EqualTo(65));
        Assert.That(publicKey[0], Is.EqualTo(4),
            "Uncompressed pubkey should start with 04");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a minimal valid Bitcoin transaction with a single output of the given value.
    /// Matches the Rust test helper make_dummy_prev_tx().
    /// </summary>
    private static byte[] BuildSimpleNodeTx(ulong amountSats)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // Version (4 bytes LE) — v3
        w.Write(3u);

        // No segwit marker — non-witness serialization

        // Input count (varint: 1)
        w.Write((byte)1);
        // Previous outpoint: 32-byte zero hash + 4-byte zero index
        w.Write(new byte[32]);
        w.Write(0u);
        // Script length (varint: 0)
        w.Write((byte)0);
        // Sequence (4 bytes)
        w.Write(0u);

        // Output count (varint: 1)
        w.Write((byte)1);
        // Value (8 bytes LE)
        w.Write(amountSats);
        // Script: OP_TRUE (0x51) — 1 byte
        w.Write((byte)1); // script length
        w.Write((byte)0x51); // OP_TRUE

        // Locktime (4 bytes)
        w.Write(0u);

        return ms.ToArray();
    }

    /// <summary>
    /// Parse the output value (8-byte LE uint64) at the given output index from raw tx bytes.
    /// </summary>
    private static ulong ParseOutputValue(byte[] txBytes, int outputIndex)
    {
        int offset = 4; // skip version

        // Check for segwit marker
        if (txBytes[offset] == 0x00 && txBytes[offset + 1] == 0x01)
            offset += 2;

        // Skip inputs
        var (inputCount, inputCountBytes) = ReadVarInt(txBytes, offset);
        offset += inputCountBytes;

        for (int i = 0; i < (int)inputCount; i++)
        {
            offset += 32 + 4; // prev_hash + prev_index
            var (scriptLen, scriptLenBytes) = ReadVarInt(txBytes, offset);
            offset += scriptLenBytes + (int)scriptLen;
            offset += 4; // sequence
        }

        // Read outputs
        var (outputCount, outputCountBytes) = ReadVarInt(txBytes, offset);
        offset += outputCountBytes;

        for (int i = 0; i < (int)outputCount; i++)
        {
            var value = BitConverter.ToUInt64(txBytes, offset);
            offset += 8;
            var (scriptLen, scriptLenBytes) = ReadVarInt(txBytes, offset);
            offset += scriptLenBytes + (int)scriptLen;

            if (i == outputIndex)
                return value;
        }

        throw new InvalidOperationException($"Output index {outputIndex} not found in tx with {outputCount} outputs");
    }

    private static (long value, int bytesRead) ReadVarInt(byte[] data, int offset)
    {
        var first = data[offset];
        return first switch
        {
            < 0xFD => (first, 1),
            0xFD => (BitConverter.ToUInt16(data, offset + 1), 3),
            0xFE => (BitConverter.ToUInt32(data, offset + 1), 5),
            _ => ((long)BitConverter.ToUInt64(data, offset + 1), 9),
        };
    }
}
