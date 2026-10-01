namespace TOLIK.Agent;

/// <summary>
/// Agent-side filesystem allowlist guard (§5 least privilege, §10/§11).
/// Resolves a requested path against explicit roots and walks parents for link
/// escapes. Separated from WindowsAgent for direct unit testing.
/// </summary>
public static class AgentPathGuard
{
    public static string ResolveAllowedPath(string path, string[] roots)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.");
        var canonicalRoots = (roots ?? []).Select(Path.GetFullPath).ToArray();
        if (canonicalRoots.Length == 0) throw new UnauthorizedAccessException("No Agent filesystem roots configured.");
        var fullPath = Path.GetFullPath(path);
        if (!canonicalRoots.Any(root => fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Path is outside the Agent allowlist.");
        if (File.Exists(fullPath)) DemandInsideRoots(ResolveLinkTargetSafe(new FileInfo(fullPath)), canonicalRoots, "File link is outside the Agent allowlist.");
        var current = new DirectoryInfo(Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath)!);
        while (current is not null)
        {
            // A volume root (Parent is null) cannot be a link escape; resolving it
            // throws DirectoryNotFound on Windows — stop the walk here.
            if (current.Parent is null) break;
            DemandInsideRoots(ResolveLinkTargetSafe(current), canonicalRoots, "Path link is outside the Agent allowlist.");
            current = current.Parent;
        }
        return fullPath;
    }

    /// <summary>Resolves a link target; unresolvable levels fail closed (deny).</summary>
    private static FileSystemInfo? ResolveLinkTargetSafe(DirectoryInfo directory)
    {
        try { return directory.ResolveLinkTarget(returnFinalTarget: true); }
        catch (IOException) { throw new UnauthorizedAccessException("Path link is outside the Agent allowlist."); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("Path link is outside the Agent allowlist."); }
    }

    /// <summary>Resolves a file link target; unresolvable files fail closed (deny).</summary>
    private static FileSystemInfo? ResolveLinkTargetSafe(FileInfo file)
    {
        try { return file.ResolveLinkTarget(returnFinalTarget: true); }
        catch (IOException) { throw new UnauthorizedAccessException("File link is outside the Agent allowlist."); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("File link is outside the Agent allowlist."); }
    }

    private static void DemandInsideRoots(FileSystemInfo? link, string[] roots, string message)
    {
        if (link is not null && !roots.Any(root => link.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) || link.FullName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException(message);
    }
}
