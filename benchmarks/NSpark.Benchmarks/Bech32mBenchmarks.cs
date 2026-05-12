using BenchmarkDotNet.Attributes;
using NSpark.Services;

namespace NSpark.Benchmarks;

/// <summary>
/// Microbenchmarks for the Bech32m encoder. Spark addresses are encoded on
/// every wallet creation and frequently on hot paths during transfer flows.
/// </summary>
[MemoryDiagnoser]
public class Bech32mBenchmarks
{
    [Params(0, 32, 64, 256)]
    public int DataSize { get; set; }

    private byte[] _data = [];

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[DataSize];
        for (int i = 0; i < DataSize; i++)
        {
            _data[i] = (byte)(i & 0xff);
        }
    }

    [Benchmark]
    public string Encode() => Bech32mHelper.Encode("spark", _data);
}
