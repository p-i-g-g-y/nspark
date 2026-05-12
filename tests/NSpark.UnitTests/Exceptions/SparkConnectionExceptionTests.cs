using Grpc.Core;
using NSpark.Exceptions;

namespace NSpark.UnitTests.Exceptions;

[TestFixture]
public sealed class SparkConnectionExceptionTests
{
    [TestCase(StatusCode.Unavailable, true)]
    [TestCase(StatusCode.DeadlineExceeded, true)]
    [TestCase(StatusCode.ResourceExhausted, true)]
    [TestCase(StatusCode.Aborted, true)]
    [TestCase(StatusCode.Unauthenticated, false)]
    [TestCase(StatusCode.PermissionDenied, false)]
    [TestCase(StatusCode.InvalidArgument, false)]
    [TestCase(StatusCode.NotFound, false)]
    public void IsRetryable_classifies_status_codes(StatusCode code, bool expected)
    {
        var rpc = new RpcException(new Status(code, "test"));
        var ex = SparkConnectionException.FromRpc("test.op", "https://so.example", rpc);

        ex.IsRetryable.Should().Be(expected);
        ex.GrpcStatusCode.Should().Be(code);
        ex.Endpoint.Should().Be("https://so.example");
        ex.Operation.Should().Be("test.op");
        ex.InnerException.Should().BeSameAs(rpc);
    }

    [Test]
    public void Message_describes_endpoint_status_and_detail()
    {
        var rpc = new RpcException(new Status(StatusCode.Unavailable, "channel closed"));
        var ex = SparkConnectionException.FromRpc("so.authenticate", "https://so.example", rpc);

        ex.Message.Should().Contain("so.example");
        ex.Message.Should().Contain("Unavailable");
        ex.Message.Should().Contain("channel closed");
    }
}
