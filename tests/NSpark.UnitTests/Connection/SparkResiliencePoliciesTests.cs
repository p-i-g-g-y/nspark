using Grpc.Core;
using NSpark.Connection;
using Polly;

namespace NSpark.UnitTests.Connection;

/// <summary>
/// Tests for the Polly v8 pipeline used to wrap gRPC calls to Signing Operators.
/// </summary>
[TestFixture]
public sealed class SparkResiliencePoliciesTests
{
    [Test]
    public async Task Pipeline_returns_result_on_success_without_retries()
    {
        var pipeline = SparkResiliencePolicies.Build();
        int attempts = 0;

        var result = await pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            return ValueTask.FromResult("ok");
        });

        result.Should().Be("ok");
        attempts.Should().Be(1);
    }

    [Test]
    public async Task Pipeline_retries_on_transient_RpcException_then_succeeds()
    {
        var pipeline = SparkResiliencePolicies.Build(
            timeout: TimeSpan.FromSeconds(5),
            maxRetries: 3);

        int attempts = 0;
        var result = await pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "transient"));
            }
            return ValueTask.FromResult("ok-after-retries");
        });

        result.Should().Be("ok-after-retries");
        attempts.Should().Be(3);
    }

    [Test]
    public async Task Pipeline_does_not_retry_on_non_transient_status()
    {
        var pipeline = SparkResiliencePolicies.Build(
            timeout: TimeSpan.FromSeconds(5),
            maxRetries: 3);

        int attempts = 0;
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(_ =>
            {
                attempts++;
                throw new RpcException(new Status(StatusCode.PermissionDenied, "nope"));
            });
        };

        await act.Should().ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.PermissionDenied);
        attempts.Should().Be(1);
    }

    [Test]
    public async Task Pipeline_with_zero_retries_does_not_retry_even_transient_errors()
    {
        var pipeline = SparkResiliencePolicies.Build(
            timeout: TimeSpan.FromSeconds(5),
            maxRetries: 0);

        int attempts = 0;
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(_ =>
            {
                attempts++;
                throw new RpcException(new Status(StatusCode.Unavailable, "still no retry"));
            });
        };

        await act.Should().ThrowAsync<RpcException>();
        attempts.Should().Be(1);
    }

    [TestCase(StatusCode.Unavailable)]
    [TestCase(StatusCode.DeadlineExceeded)]
    [TestCase(StatusCode.ResourceExhausted)]
    [TestCase(StatusCode.Aborted)]
    public async Task Pipeline_retries_on_classified_transient_status_codes(StatusCode code)
    {
        var pipeline = SparkResiliencePolicies.Build(
            timeout: TimeSpan.FromSeconds(5),
            maxRetries: 2);

        int attempts = 0;
        Func<Task> act = async () =>
        {
            await pipeline.ExecuteAsync(_ =>
            {
                attempts++;
                throw new RpcException(new Status(code, "transient"));
            });
        };

        await act.Should().ThrowAsync<RpcException>();
        // 1 initial + 2 retries = 3 attempts total.
        attempts.Should().Be(3);
    }
}
