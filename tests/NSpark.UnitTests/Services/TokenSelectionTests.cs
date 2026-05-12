using Google.Protobuf;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Proto.Token;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TokenService.SelectTokenOutputs"/> ported from the
/// Swift Spark SDK's <c>TokenTests.swift</c>.
/// </summary>
[TestFixture]
public sealed class TokenSelectionTests
{
    [Test]
    public void Exact_match_picks_single_output()
    {
        var outputs = MakeOutputs([100, 200, 300, 500]);
        var selected = TokenService.SelectTokenOutputs(outputs, 200, TokenSelectionStrategy.SmallFirst);
        selected.Should().HaveCount(1);
        TokenService.DecodeUInt128(selected[0].Output.TokenAmount).Should().Be((UInt128)200);
    }

    [Test]
    public void SmallFirst_walks_smallest_outputs_until_sum_reaches_target()
    {
        var outputs = MakeOutputs([10, 20, 30, 50, 100]);
        var selected = TokenService.SelectTokenOutputs(outputs, 55, TokenSelectionStrategy.SmallFirst);

        UInt128 total = UInt128.Zero;
        foreach (var o in selected)
        {
            total += TokenService.DecodeUInt128(o.Output.TokenAmount);
        }
        total.Should().BeGreaterThanOrEqualTo((UInt128)55);
    }

    [Test]
    public void LargeFirst_uses_fewer_outputs_than_SmallFirst()
    {
        var outputs = MakeOutputs([10, 20, 30, 50, 100]);
        var selected = TokenService.SelectTokenOutputs(outputs, 55, TokenSelectionStrategy.LargeFirst);

        UInt128 total = UInt128.Zero;
        foreach (var o in selected)
        {
            total += TokenService.DecodeUInt128(o.Output.TokenAmount);
        }
        total.Should().BeGreaterThanOrEqualTo((UInt128)55);
        selected.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Test]
    public void Insufficient_balance_throws()
    {
        var outputs = MakeOutputs([10, 20, 30]);
        Action act = () => TokenService.SelectTokenOutputs(outputs, 100, TokenSelectionStrategy.SmallFirst);
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*Insufficient token balance*");
    }

    [Test]
    public void Zero_amount_throws()
    {
        var outputs = MakeOutputs([100]);
        Action act = () => TokenService.SelectTokenOutputs(outputs, UInt128.Zero, TokenSelectionStrategy.SmallFirst);
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*greater than 0*");
    }

    [Test]
    public void Single_output_larger_than_amount_is_used_whole()
    {
        var outputs = MakeOutputs([500]);
        var selected = TokenService.SelectTokenOutputs(outputs, 300, TokenSelectionStrategy.SmallFirst);
        selected.Should().HaveCount(1);
        TokenService.DecodeUInt128(selected[0].Output.TokenAmount).Should().Be((UInt128)500);
    }

    [Test]
    public void All_outputs_required_when_total_equals_amount()
    {
        var outputs = MakeOutputs([10, 20, 30]);
        var selected = TokenService.SelectTokenOutputs(outputs, 60, TokenSelectionStrategy.SmallFirst);

        selected.Should().HaveCount(3);

        UInt128 total = UInt128.Zero;
        foreach (var o in selected)
        {
            total += TokenService.DecodeUInt128(o.Output.TokenAmount);
        }
        total.Should().Be((UInt128)60);
    }

    [Test]
    public void LargeFirst_picks_single_largest_when_sufficient()
    {
        var outputs = MakeOutputs([10, 200, 50, 5]);
        var selected = TokenService.SelectTokenOutputs(outputs, 150, TokenSelectionStrategy.LargeFirst);
        selected.Should().HaveCount(1);
        TokenService.DecodeUInt128(selected[0].Output.TokenAmount).Should().Be((UInt128)200);
    }

    private static IReadOnlyList<OutputWithPreviousTransactionData> MakeOutputs(UInt128[] amounts)
    {
        var result = new List<OutputWithPreviousTransactionData>(amounts.Length);
        for (uint i = 0; i < amounts.Length; i++)
        {
            result.Add(new OutputWithPreviousTransactionData
            {
                Output = new TokenOutput
                {
                    OwnerPublicKey = ByteString.CopyFrom(Enumerable.Repeat<byte>(0x02, 33).ToArray()),
                    TokenIdentifier = ByteString.CopyFrom(new byte[32]),
                    TokenAmount = TokenService.EncodeUInt128(amounts[i]),
                },
                PreviousTransactionHash = ByteString.CopyFrom(new byte[32]),
                PreviousTransactionVout = i,
            });
        }
        return result;
    }
}
