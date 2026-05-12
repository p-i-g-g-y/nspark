using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NSpark.Exceptions;
using NSpark.Proto;
using NSpark.Proto.Token;
using NSpark.Services;

namespace NSpark.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TokenHashing"/> ported from the Swift Spark SDK.
/// </summary>
[TestFixture]
public sealed class TokenHashingTests
{
    [Test]
    public void V2_transfer_partial_and_full_hashes_are_both_32_bytes_and_differ()
    {
        var tx = new TokenTransaction
        {
            Version = 2,
            Network = Network.Mainnet,
            ClientCreatedTimestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        tx.SparkOperatorIdentityPublicKeys.Add(ByteString.CopyFrom(new byte[33]));

        var transferInput = new TokenTransferInput();
        transferInput.OutputsToSpend.Add(new TokenOutputToSpend
        {
            PrevTokenTransactionHash = ByteString.CopyFrom(new byte[32]),
            PrevTokenTransactionVout = 0,
        });
        tx.TransferInput = transferInput;

        tx.TokenOutputs.Add(new TokenOutput
        {
            OwnerPublicKey = ByteString.CopyFrom(new byte[33]),
            TokenAmount = TokenService.EncodeUInt128(1000),
        });

        var partial = TokenHashing.HashTokenTransactionV2(tx, partialHash: true);
        partial.Length.Should().Be(32);

        var full = TokenHashing.HashTokenTransactionV2(tx, partialHash: false);
        full.Length.Should().Be(32);

        partial.Should().NotEqual(full);
    }

    [Test]
    public void V2_hashing_is_deterministic_for_same_input()
    {
        var tx = new TokenTransaction
        {
            Version = 2,
            Network = Network.Regtest,
            ClientCreatedTimestamp = new Timestamp { Seconds = 1_700_000_000, Nanos = 0 },
        };
        tx.SparkOperatorIdentityPublicKeys.Add(
            ByteString.CopyFrom(Enumerable.Repeat<byte>(0x02, 33).ToArray()));

        var mintInput = new TokenMintInput
        {
            IssuerPublicKey = ByteString.CopyFrom(Enumerable.Repeat<byte>(0x02, 33).ToArray()),
            TokenIdentifier = ByteString.CopyFrom(new byte[32]),
        };
        tx.MintInput = mintInput;

        tx.TokenOutputs.Add(new TokenOutput
        {
            OwnerPublicKey = ByteString.CopyFrom(Enumerable.Repeat<byte>(0x03, 33).ToArray()),
            TokenAmount = TokenService.EncodeUInt128(42),
        });

        var h1 = TokenHashing.HashTokenTransactionV2(tx, partialHash: true);
        var h2 = TokenHashing.HashTokenTransactionV2(tx, partialHash: true);
        h1.Should().Equal(h2);
    }

    [Test]
    public void OperatorSpecificPayloadHash_is_deterministic_and_input_sensitive()
    {
        var txHash = new byte[32];
        var opKey = Enumerable.Repeat<byte>(0x02, 33).ToArray();

        var h1 = TokenHashing.HashOperatorSpecificPayload(txHash, opKey);
        var h2 = TokenHashing.HashOperatorSpecificPayload(txHash, opKey);
        h1.Length.Should().Be(32);
        h1.Should().Equal(h2);

        var differentKey = Enumerable.Repeat<byte>(0x03, 33).ToArray();
        var h3 = TokenHashing.HashOperatorSpecificPayload(txHash, differentKey);
        h1.Should().NotEqual(h3);
    }

    [Test]
    public void OperatorSpecificPayloadHash_throws_on_wrong_hash_length()
    {
        Action act = () => TokenHashing.HashOperatorSpecificPayload(new byte[16], new byte[33]);
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*must be 32 bytes*");
    }

    [Test]
    public void OperatorSpecificPayloadHash_throws_on_empty_operator_key()
    {
        Action act = () => TokenHashing.HashOperatorSpecificPayload(new byte[32], Array.Empty<byte>());
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*Operator identity public key cannot be empty*");
    }

    [Test]
    public void V2_create_input_partial_and_full_differ()
    {
        var tx = new TokenTransaction
        {
            Version = 2,
            Network = Network.Mainnet,
            ClientCreatedTimestamp = new Timestamp { Seconds = 1_700_000_000, Nanos = 0 },
        };
        tx.SparkOperatorIdentityPublicKeys.Add(
            ByteString.CopyFrom(Enumerable.Repeat<byte>(0x02, 33).ToArray()));

        var createInput = new TokenCreateInput
        {
            IssuerPublicKey = ByteString.CopyFrom(Enumerable.Repeat<byte>(0x02, 33).ToArray()),
            TokenName = "TestToken",
            TokenTicker = "TST",
            Decimals = 8,
            MaxSupply = TokenService.EncodeUInt128(21_000_000),
            IsFreezable = false,
        };
        tx.CreateInput = createInput;

        var partial = TokenHashing.HashTokenTransactionV2(tx, partialHash: true);
        partial.Length.Should().Be(32);

        var full = TokenHashing.HashTokenTransactionV2(tx, partialHash: false);
        full.Length.Should().Be(32);

        partial.Should().NotEqual(full);
    }

    [Test]
    public void V2_mint_input_hash_is_32_bytes()
    {
        var tx = new TokenTransaction
        {
            Version = 2,
            Network = Network.Regtest,
            ClientCreatedTimestamp = new Timestamp { Seconds = 1_700_000_000, Nanos = 0 },
        };
        tx.SparkOperatorIdentityPublicKeys.Add(
            ByteString.CopyFrom(Enumerable.Repeat<byte>(0x02, 33).ToArray()));

        var mintInput = new TokenMintInput
        {
            IssuerPublicKey = ByteString.CopyFrom(Enumerable.Repeat<byte>(0x03, 33).ToArray()),
            TokenIdentifier = ByteString.CopyFrom(Enumerable.Repeat<byte>(0xAB, 32).ToArray()),
        };
        tx.MintInput = mintInput;

        tx.TokenOutputs.Add(new TokenOutput
        {
            OwnerPublicKey = ByteString.CopyFrom(Enumerable.Repeat<byte>(0x03, 33).ToArray()),
            TokenIdentifier = ByteString.CopyFrom(Enumerable.Repeat<byte>(0xAB, 32).ToArray()),
            TokenAmount = TokenService.EncodeUInt128(5000),
        });

        var hash = TokenHashing.HashTokenTransactionV2(tx, partialHash: true);
        hash.Length.Should().Be(32);
    }

    [Test]
    public void V2_hashing_throws_on_missing_input_oneof()
    {
        var tx = new TokenTransaction
        {
            Version = 2,
            Network = Network.Mainnet,
            ClientCreatedTimestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        Action act = () => TokenHashing.HashTokenTransactionV2(tx, partialHash: true);
        act.Should().Throw<SparkConfigurationException>()
            .WithMessage("*exactly one input type*");
    }
}
