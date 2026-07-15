using NSpark.Exceptions;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TimelockHelper"/> — the shared refund-timelock
/// decrement used by every spend path.
/// </summary>
/// <remarks>
/// The floor guard is the load-bearing part: <c>uint</c> subtraction wraps
/// silently in C#, so a leaf at the floor would otherwise produce a garbage
/// sequence (65,436+) instead of a typed error telling the caller to renew.
/// </remarks>
[TestFixture]
public sealed class TimelockHelperTests
{
    private const uint Bit30 = 1u << 30;

    [Test]
    public void ComputeNextSequences_decrements_one_interval_and_offsets_direct()
    {
        var (cpfp, direct) = TimelockHelper.ComputeNextSequences(MakeRawTx(2000), "test.op");

        cpfp.Should().Be(1900);
        direct.Should().Be(1950);
    }

    [Test]
    public void ComputeNextSequences_preserves_bit30_across_the_decrement()
    {
        var (cpfp, direct) = TimelockHelper.ComputeNextSequences(MakeRawTx(Bit30 | 2000), "test.op");

        cpfp.Should().Be(Bit30 | 1900);
        direct.Should().Be(Bit30 | 1950);
    }

    [Test]
    public void ComputeNextSequences_allows_the_last_decrement_above_the_floor()
    {
        // 101 is the smallest timelock that can still move: 101 - 100 = 1.
        var (cpfp, direct) = TimelockHelper.ComputeNextSequences(MakeRawTx(101), "test.op");

        cpfp.Should().Be(1);
        direct.Should().Be(51);
    }

    [Test]
    public void ComputeNextSequences_throws_at_the_floor_instead_of_underflowing()
    {
        // 100 - 100 would reach zero, which the coordinator rejects; anything
        // below would wrap the uint. Both must throw the typed exception.
        var act = () => TimelockHelper.ComputeNextSequences(MakeRawTx(100), "test.op", "leaf-1");

        act.Should().Throw<SparkLeafTimelockExhaustedException>()
            .Which.Should().Match<SparkLeafTimelockExhaustedException>(e =>
                e.Operation == "test.op" && e.LeafId == "leaf-1");
    }

    [Test]
    public void ComputeNextSequences_throws_below_the_floor()
    {
        var act = () => TimelockHelper.ComputeNextSequences(MakeRawTx(40), "test.op");

        act.Should().Throw<SparkLeafTimelockExhaustedException>();
    }

    [Test]
    public void ComputeNextSequences_ignores_bit30_when_checking_the_floor()
    {
        var act = () => TimelockHelper.ComputeNextSequences(MakeRawTx(Bit30 | 100), "test.op");

        act.Should().Throw<SparkLeafTimelockExhaustedException>();
    }

    [TestCase(101u, true)]
    [TestCase(2000u, true)]
    [TestCase(100u, false)]
    [TestCase(0u, false)]
    [TestCase(Bit30 | 100u, false)]
    [TestCase(Bit30 | 101u, true)]
    public void TimelockCanDecrement_is_strictly_greater_than_one_interval(uint sequence, bool expected)
    {
        TimelockHelper.TimelockCanDecrement(MakeRawTx(sequence)).Should().Be(expected);
    }

    /// <summary>
    /// Minimal single-input non-segwit tx: version(4) + input count(1) +
    /// prevout(36) + empty script(1) + nSequence(4 LE). ParseInputSequence
    /// never reads past the first input's sequence.
    /// </summary>
    private static byte[] MakeRawTx(uint sequence)
    {
        var tx = new byte[4 + 1 + 36 + 1 + 4];
        tx[0] = 0x02; // version 2
        tx[4] = 0x01; // one input
        // prevout hash (32) + index (4) stay zero; script length byte stays 0x00
        BitConverter.GetBytes(sequence).CopyTo(tx, 42);
        return tx;
    }
}
