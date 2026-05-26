using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Exceptions;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Validation-path tests for the write-side TokenService methods. These never
/// hit the network — each test forces an argument-validation failure before
/// any gRPC call would be made, so they're safe to run without funded test
/// wallets.
/// </summary>
[TestFixture]
public sealed class TokenValidationTests
{
    private SparkConnection _connection = null!;
    private SparkWallet _wallet = null!;

    [SetUp]
    public async Task Setup()
    {
        var options = Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet });
        _connection = new SparkConnection(options, new HttpClient());
        _wallet = await _connection.CreateWalletAsync(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
    }

    [TearDown]
    public void Teardown() => _connection.Dispose();

    [Test]
    public async Task CreateTokenAsync_throws_on_name_too_long()
    {
        Func<Task> act = () => _wallet.CreateTokenAsync(
            tokenName: new string('A', 21),
            tokenTicker: "TST",
            decimals: 8,
            maxSupply: UInt128.Zero,
            isFreezable: false);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*1-20 UTF-8 bytes*");
    }

    [Test]
    public async Task CreateTokenAsync_throws_on_empty_name()
    {
        Func<Task> act = () => _wallet.CreateTokenAsync(
            tokenName: "",
            tokenTicker: "TST",
            decimals: 8,
            maxSupply: UInt128.Zero,
            isFreezable: false);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task CreateTokenAsync_throws_on_ticker_too_long()
    {
        Func<Task> act = () => _wallet.CreateTokenAsync(
            tokenName: "Test",
            tokenTicker: "TOOLONG",
            decimals: 8,
            maxSupply: UInt128.Zero,
            isFreezable: false);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*1-6 UTF-8 bytes*");
    }

    [Test]
    public async Task CreateTokenAsync_throws_on_decimals_above_255()
    {
        Func<Task> act = () => _wallet.CreateTokenAsync(
            tokenName: "Test",
            tokenTicker: "TST",
            decimals: 256,
            maxSupply: UInt128.Zero,
            isFreezable: false);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*<= 255*");
    }

    [Test]
    public async Task CreateTokenAsync_throws_on_oversized_extra_metadata()
    {
        Func<Task> act = () => _wallet.CreateTokenAsync(
            tokenName: "Test",
            tokenTicker: "TST",
            decimals: 8,
            maxSupply: UInt128.Zero,
            isFreezable: false,
            extraMetadata: new byte[1025]);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*<= 1024 bytes*");
    }

    [Test]
    public async Task MintTokensAsync_throws_on_zero_amount()
    {
        var fakeTokenId = TokenIdentifier.Encode(new byte[32], SparkNetwork.Mainnet);
        Func<Task> act = () => _wallet.MintTokensAsync(fakeTokenId, UInt128.Zero);
        await act.Should().ThrowAsync<SparkConfigurationException>()
            .WithMessage("*greater than 0*");
    }
}
