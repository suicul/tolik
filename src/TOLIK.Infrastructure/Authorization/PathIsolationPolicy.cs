namespace TOLIK.Infrastructure.Authorization;

public sealed class PathIsolationPolicy
{
    private readonly string[] roots;
    public PathIsolationPolicy(IEnumerable<string> allowedRoots)
    {
        roots = allowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Select(x => x.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    public bool IsAllowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = Path.GetFullPath(path);
        if (full.Contains(".env", StringComparison.OrdinalIgnoreCase) || full.Contains("id_rsa", StringComparison.OrdinalIgnoreCase) || full.Contains("credentials", StringComparison.OrdinalIgnoreCase)) return false;
        return roots.Any(root => string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
