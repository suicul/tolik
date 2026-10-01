using System.Text;

namespace TOLIK.Infrastructure.Authorization;

public sealed class FileToolService(PathIsolationPolicy policy)
{
    public async Task<string> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(path);
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    public Task<string[]> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(path);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = Directory.EnumerateFileSystemEntries(path)
            .Where(policy.IsAllowed)
            .Take(1000)
            .ToArray();
        return Task.FromResult(entries);
    }

    public async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(path);
        if (content.Length > 1_000_000) throw new InvalidOperationException("File size limit exceeded.");
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
    }

    private void EnsureAllowed(string path)
    {
        if (!policy.IsAllowed(path)) throw new UnauthorizedAccessException("Path is outside the allowlist.");
    }
}
