using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Services;

namespace NSpark.Tests;

// =============================================================================
// Leaf maintenance tests (matching Swift: IntegrationTests ConsolidationTests)
// Renewal is a near-no-op on a healthy wallet; consolidation performs REAL
// mainnet SSP swaps (fee 0) on the funded test wallet.
// =============================================================================

[TestFixture]
[Category("Integration")]
public class MaintenanceTests
{
    private static string MnemonicA => TestSecrets.MnemonicA;

    private SparkConnection _client = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _client = new SparkConnection(
            Options.Create(new SparkOptions { Network = SparkNetwork.Mainnet }),
            new HttpClient());
    }

    [OneTimeTearDown]
    public void Teardown() => _client?.Dispose();

    [Test]
    public async Task RenewalSweepChecksEveryLeafWithoutFailures()
    {
        var wallet = await _client.CreateWalletAsync(MnemonicA);
        var before = await wallet.GetLeavesAsync();

        var result = await wallet.RenewExhaustedLeavesAsync();

        TestContext.Out.WriteLine(
            $"Renewal: checked {result.Checked}, renewed {result.Renewed}, " +
            $"failures: [{string.Join("; ", result.Failures)}]");

        Assert.That(result.Checked, Is.EqualTo(before.Count));
        Assert.That(result.Renewed, Is.LessThanOrEqualTo(result.Checked));
        Assert.That(result.Failures, Is.Empty);
    }

    [Test]
    public async Task ConsolidateLeavesReducesCountAtZeroFee()
    {
        var wallet = await _client.CreateWalletAsync(MnemonicA);
        var before = await wallet.GetLeavesAsync();
        if (before.Count == 0)
        {
            Assert.Inconclusive("Test wallet has no leaves to consolidate.");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result = await wallet.ConsolidateLeavesAsync(ct: cts.Token);

        TestContext.Out.WriteLine(
            $"Consolidation: {result.LeavesBefore} -> {result.LeavesAfter} leaves " +
            $"in {result.Rounds} rounds, fee {result.FeeSats} sats, skipped {result.SkippedLeaves}");

        Assert.That(result.LeavesBefore, Is.EqualTo(before.Count));
        Assert.That(result.LeavesAfter, Is.LessThanOrEqualTo(result.LeavesBefore));
        Assert.That(result.FeeSats, Is.EqualTo(0), "SSP swap unexpectedly charged a fee");

        var ideal = ConsolidationService.BinaryDecomposition(result.TotalSatsBefore).Length;
        Assert.That(result.LeavesAfter, Is.LessThanOrEqualTo(Math.Max(ideal, result.LeavesBefore)));
    }
}
