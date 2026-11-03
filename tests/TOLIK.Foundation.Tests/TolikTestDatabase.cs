using System.Text.RegularExpressions;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Resolves the PostgreSQL server connection for integration tests.
///
/// The password used to be a literal in this project, in appsettings.json, in two migration
/// scripts and in a scratch tool — i.e. committed to git, which AGENTS.md §12 forbids outright.
/// The value now comes from the environment or from the git-ignored .env at the repository
/// root, so the repository contains no credential at all. There is deliberately NO fallback
/// default: a fallback would put a credential back into git and would silently mask a rotation.
///
/// Resolution order: TOLIK_TEST_POSTGRES_SERVER (full server string, wins outright), then
/// TOLIK_POSTGRES_PASSWORD / TOLIK_POSTGRES_PORT from the environment, then the same keys in the
/// repository's .env.
/// </summary>
internal static class TolikTestDatabase
{
    private const string DefaultPort = "5432";

    private static readonly Lazy<string> LazyServerConnection = new(Resolve);

    public static string ServerConnection => LazyServerConnection.Value;

    private static string Resolve()
    {
        string? explicitServer = Environment.GetEnvironmentVariable("TOLIK_TEST_POSTGRES_SERVER");
        if (!string.IsNullOrWhiteSpace(explicitServer))
        {
            return explicitServer.Trim().TrimEnd(';');
        }

        Dictionary<string, string> envFile = ReadEnvFile();

        string? password = Environment.GetEnvironmentVariable("TOLIK_POSTGRES_PASSWORD");
        if (string.IsNullOrWhiteSpace(password) && envFile.TryGetValue("TOLIK_POSTGRES_PASSWORD", out string? fromFile))
        {
            password = fromFile;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "PostgreSQL password for integration tests is not configured. Set " +
                "TOLIK_POSTGRES_PASSWORD in the process environment, or put it in the " +
                "repository's .env (git-ignored). No default is baked in on purpose: a " +
                "committed credential is forbidden by AGENTS.md SECURITY §6.");
        }

        string? port = Environment.GetEnvironmentVariable("TOLIK_POSTGRES_PORT");
        if (string.IsNullOrWhiteSpace(port) && envFile.TryGetValue("TOLIK_POSTGRES_PORT", out string? portFromFile))
        {
            port = portFromFile;
        }

        string portValue = string.IsNullOrWhiteSpace(port) ? DefaultPort : port.Trim();
        return $"Host=localhost;Port={portValue};Username=tolik;Password={password}";
    }

    private static Dictionary<string, string> ReadEnvFile()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? path = FindRepositoryFile(".env");
        if (path is null)
        {
            return values;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            Match match = Regex.Match(line, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$");
            if (!match.Success)
            {
                continue;
            }

            string value = match.Groups[2].Value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            values[match.Groups[1].Value] = value;
        }

        return values;
    }

    private static string? FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            bool isRoot = File.Exists(Path.Combine(directory.FullName, "TOLIK.sln"));
            directory = directory.Parent;
            if (isRoot)
            {
                // Do not walk past the repository root into unrelated directories.
                return null;
            }
        }

        return null;
    }
}
