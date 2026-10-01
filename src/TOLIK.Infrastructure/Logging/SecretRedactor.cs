using System.Text.RegularExpressions;

namespace TOLIK.Infrastructure.Logging;

public static partial class SecretRedactor
{
    public const string Replacement = "[REDACTED]";

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey",
        "accesstoken",
        "refreshtoken",
        "token",
        "password",
        "passwd",
        "pwd",
        "secret",
        "clientsecret",
        "authorization",
        "cookie",
        "setcookie",
        "connectionstring"
    };

    public static bool IsSensitiveName(string name)
    {
        var normalized = string.Concat(name.Where(char.IsLetterOrDigit));
        return SensitiveNames.Contains(normalized)
            || normalized.EndsWith("password", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("token", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("apikey", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("privatekey", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("authorization", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("cookie", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("connectionstring", StringComparison.OrdinalIgnoreCase);
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        return NamedSecretPattern().Replace(
            BearerTokenPattern().Replace(
                PrivateKeyPattern().Replace(value, Replacement),
                match => $"{match.Groups["prefix"].Value}{Replacement}"),
            match =>
            {
                var quote = match.Groups["quote"].Value;
                return $"{match.Groups["prefix"].Value}{quote}{Replacement}{quote}";
            });
    }

    [GeneratedRegex(
        "-----BEGIN [^-\\r\\n]*PRIVATE KEY-----[\\s\\S]*?-----END [^-\\r\\n]*PRIVATE KEY-----",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(
        "(?<prefix>\\bBearer\\s+)[A-Za-z0-9._~+/=-]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(
        "(?<prefix>[\\\"']?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|passwd|pwd|secret|client[_-]?secret|authorization|cookie|set-cookie|connectionstrings?)[\\\"']?\\s*(?:=|:)\\s*)(?:(?<quote>[\\\"'])(?<value>[^\\r\\n]*?)\\k<quote>|(?<value>[^&\\s,;\\\"'}]+))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NamedSecretPattern();
}
