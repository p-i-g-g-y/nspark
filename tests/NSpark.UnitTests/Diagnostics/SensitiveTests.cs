using NSpark.Diagnostics;

namespace NSpark.UnitTests.Diagnostics;

[TestFixture]
public sealed class SensitiveTests
{
    [Test]
    public void ToString_returns_redaction_marker()
    {
        Sensitive<string> wrapped = "hunter2";
        wrapped.ToString().Should().Be("***");
    }

    [Test]
    public void Value_returns_underlying_value()
    {
        Sensitive<int> wrapped = 42;
        wrapped.Value.Should().Be(42);
    }

    [Test]
    public void Equality_is_based_on_underlying_value()
    {
        Sensitive<string> a = "secret";
        Sensitive<string> b = "secret";
        Sensitive<string> c = "different";

        (a == b).Should().BeTrue();
        (a == c).Should().BeFalse();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Test]
    public void Implicit_conversion_wraps_raw_value()
    {
        Sensitive<long> wrapped = 12345L;
        wrapped.Value.Should().Be(12345L);
    }

    [Test]
    public void Format_Redact_emits_length_only()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        SensitiveFormat.Redact(bytes).Should().Be("<redacted 5 bytes>");
    }

    [Test]
    public void Format_Fingerprint_uses_first_four_bytes()
    {
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 };
        SensitiveFormat.Fingerprint(bytes).Should().Be("<deadbeef…>");
    }

    [Test]
    public void Format_Fingerprint_too_short_returns_redacted()
    {
        SensitiveFormat.Fingerprint(new byte[] { 0xAA, 0xBB, 0xCC }).Should().Be("<redacted>");
    }
}
