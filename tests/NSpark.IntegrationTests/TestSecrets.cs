using System.Collections.Concurrent;

namespace NSpark.Tests;

/// <summary>
/// Resolves secrets that integration tests need at runtime (BIP-39 mnemonics
/// for funded test wallets, regtest endpoints, etc.) from environment
/// variables or a repository-root <c>.env.local</c> file.
/// </summary>
/// <remarks>
/// <para>
/// Convention: every secret is sourced from an environment variable. The
/// loader also parses <c>.env.local</c> at the repository root the first time
/// any secret is requested. Values already set in the process environment
/// always win over <c>.env.local</c>.
/// </para>
/// <para>
/// When a required secret is missing, the property throws via
/// <see cref="Assert.Inconclusive(string)"/> so the test is reported as
/// inconclusive (skipped) rather than failing. This keeps CI green by
/// default — integration tests only run where the secrets are wired up.
/// </para>
/// <para>
/// <c>.env.local</c> is ignored by git (see <c>.gitignore</c>). Copy
/// <c>.env.local.example</c> from the repository root, fill in your own
/// BIP-39 mnemonics for funded test wallets, and never commit the result.
/// </para>
/// </remarks>
internal static class TestSecrets
{
    private const string DotEnvFileName = ".env.local";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> s_dotEnv =
        new(LoadDotEnvLocal, isThreadSafe: true);

    private static readonly ConcurrentDictionary<string, string> s_resolvedCache = new();

    /// <summary>Funded mainnet/regtest mnemonic A (primary test wallet).</summary>
    public static string MnemonicA => Require("NSPARK_TEST_MNEMONIC_A");

    /// <summary>Funded mainnet/regtest mnemonic B (counterparty for transfer/pay tests).</summary>
    public static string MnemonicB => Require("NSPARK_TEST_MNEMONIC_B");

    /// <summary>Funded mainnet/regtest mnemonic C (third-party for multi-wallet flows).</summary>
    public static string MnemonicC => Require("NSPARK_TEST_MNEMONIC_C");

    /// <summary>
    /// Resolve an arbitrary secret by name, returning <c>null</c> when not set.
    /// Use <see cref="Require(string)"/> when the test cannot run without it.
    /// </summary>
    public static string? TryGet(string name)
    {
        if (s_resolvedCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var fromEnv = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            s_resolvedCache[name] = fromEnv;
            return fromEnv;
        }

        if (s_dotEnv.Value.TryGetValue(name, out var fromFile) && !string.IsNullOrWhiteSpace(fromFile))
        {
            s_resolvedCache[name] = fromFile;
            return fromFile;
        }

        return null;
    }

    /// <summary>
    /// Resolve a required secret. Calls <see cref="Assert.Inconclusive(string)"/>
    /// when the value is missing — the test is reported as skipped, not failed.
    /// </summary>
    public static string Require(string name)
    {
        var value = TryGet(name);
        if (value is null)
        {
            Assert.Inconclusive(
                $"Integration test secret '{name}' is not configured. " +
                $"Copy .env.local.example to .env.local at the repository root " +
                $"and fill in a funded BIP-39 mnemonic, or export {name} in your shell. " +
                $"See tests/NSpark.IntegrationTests/README.md for details.");
            // Assert.Inconclusive throws — the line below is unreachable.
            throw new InvalidOperationException("unreachable");
        }
        return value;
    }

    /// <summary>
    /// Walk up from the test assembly location looking for <c>.env.local</c> at
    /// the repository root. Returns an empty dictionary when not found.
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadDotEnvLocal()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, DotEnvFileName);
            if (File.Exists(candidate))
            {
                return Parse(File.ReadAllLines(candidate));
            }
            dir = dir.Parent;
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Minimal <c>KEY=VALUE</c> parser. Skips blank lines and lines starting
    /// with <c>#</c>. Strips matching surrounding single or double quotes.
    /// Does not perform variable expansion or escape processing — keep your
    /// values simple, single-line, and ASCII-safe.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // Strip optional leading `export ` for shell-script compatibility.
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            result[key] = value;
        }
        return result;
    }
}
