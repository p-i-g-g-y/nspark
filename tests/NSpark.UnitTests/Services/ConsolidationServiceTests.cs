using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ConsolidationService.BinaryDecomposition"/> — the
/// target denomination set consolidation swaps toward. Vectors mirror the
/// Swift SDK's ConsolidationServiceTests.
/// </summary>
[TestFixture]
public sealed class ConsolidationServiceTests
{
    [Test]
    public void Zero_and_negative_totals_decompose_to_nothing()
    {
        ConsolidationService.BinaryDecomposition(0).Should().BeEmpty();
        ConsolidationService.BinaryDecomposition(-5).Should().BeEmpty();
    }

    [Test]
    public void Powers_of_two_decompose_to_themselves()
    {
        ConsolidationService.BinaryDecomposition(1).Should().Equal(1);
        ConsolidationService.BinaryDecomposition(65536).Should().Equal(65536);
    }

    [Test]
    public void Set_bits_come_out_largest_first()
    {
        ConsolidationService.BinaryDecomposition(121).Should().Equal(64, 32, 16, 8, 1);
        ConsolidationService.BinaryDecomposition(72965)
            .Should().Equal(65536, 4096, 2048, 1024, 256, 4, 1);
    }

    [TestCase(1L)]
    [TestCase(121L)]
    [TestCase(72965L)]
    [TestCase(1234567891234L)]
    [TestCase(long.MaxValue)]
    public void Decomposition_sums_back_to_the_total(long total)
    {
        ConsolidationService.BinaryDecomposition(total).Sum().Should().Be(total);
    }

    [Test]
    public void Denomination_count_equals_the_popcount()
    {
        ConsolidationService.BinaryDecomposition(72965)
            .Should().HaveCount((int)long.PopCount(72965));
    }
}
