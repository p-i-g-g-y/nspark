using BenchmarkDotNet.Attributes;
using NSpark.Signer;

namespace NSpark.Benchmarks;

/// <summary>
/// Microbenchmarks for BIP-39/32 derivation. Each <c>SparkConnection.CreateWallet</c>
/// call performs one PBKDF2 (BIP-39 seed) + five BIP-32 hardened derivations,
/// so the cost matters per-wallet-creation.
/// </summary>
[MemoryDiagnoser]
public class KeyDerivationBenchmarks
{
    private const string Mnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private KeyDerivation _keys = null!;

    [GlobalSetup]
    public void Setup() => _keys = KeyDerivation.FromMnemonic(Mnemonic);

    [Benchmark]
    public KeyDerivation FromMnemonic_cold() => KeyDerivation.FromMnemonic(Mnemonic);

    [Benchmark]
    public byte[] DeriveLeafKey_warm() => _keys.DeriveLeafKey("leaf-benchmark").PrivateKey.ToBytes();

    [Benchmark]
    public byte[] ComputePreimage_warm() => _keys.ComputePreimage("transfer-benchmark");
}
