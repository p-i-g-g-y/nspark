// Minimal NSpark example: create a wallet, mint an invoice, print the balance.
//
//   dotnet run --project samples/QuickStart
//
// Set NSPARK_MNEMONIC to use your own wallet; otherwise a static test mnemonic
// is used. Set NSPARK_NETWORK=regtest to talk to local operators.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Services;

const string defaultMnemonic =
    "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

var mnemonic = Environment.GetEnvironmentVariable("NSPARK_MNEMONIC") ?? defaultMnemonic;
var network = string.Equals(Environment.GetEnvironmentVariable("NSPARK_NETWORK"), "regtest", StringComparison.OrdinalIgnoreCase)
    ? SparkNetwork.Regtest
    : SparkNetwork.Mainnet;

using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.IncludeScopes = true;
        o.TimestampFormat = "HH:mm:ss ";
    })
    .SetMinimumLevel(LogLevel.Information));

var options = Options.Create(new SparkOptions
{
    Network = network,
});

using var http = new HttpClient();
await using var spark = new SparkConnection(options, http, loggerFactory);

var wallet = spark.CreateWallet(mnemonic);
Console.WriteLine($"Wallet identity: {wallet.IdentityPublicKeyHex}");
Console.WriteLine($"Spark address:   {wallet.GetSparkAddress()}");
Console.WriteLine();

try
{
    Console.WriteLine("Creating a 1,000 sat invoice...");
    var invoice = await wallet.CreateLightningInvoiceAsync(
        amountSats: 1_000,
        memo: "NSpark QuickStart");

    Console.WriteLine();
    Console.WriteLine($"  payment_request : {invoice.PaymentRequest}");
    Console.WriteLine($"  payment_hash    : {invoice.PaymentHash}");
    Console.WriteLine();

    Console.WriteLine("Reading balance...");
    var balance = await wallet.GetBalanceAsync();
    Console.WriteLine($"  available : {balance.SatsBalance.Available} sats");
    Console.WriteLine($"  owned     : {balance.SatsBalance.Owned} sats");
    Console.WriteLine($"  incoming  : {balance.SatsBalance.Incoming} sats");
    Console.WriteLine($"  leaves    : {balance.Leaves.Count}");
    Console.WriteLine($"  tokens    : {balance.TokenBalances.Count}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
    Environment.ExitCode = 1;
}
