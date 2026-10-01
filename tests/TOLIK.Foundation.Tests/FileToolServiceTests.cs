using TOLIK.Infrastructure.Authorization;

namespace TOLIK.Foundation.Tests;

public sealed class FileToolServiceTests
{
    [Fact]
    public async Task Read_and_write_require_allowlisted_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "tolik-file-tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new FileToolService(new PathIsolationPolicy([root]));
            var path = Path.Combine(root, "ok.txt");
            await service.WriteAsync(path, "safe");
            Assert.Equal("safe", await service.ReadAsync(path));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(Path.Combine(root, "..", "outside.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task List_directory_returns_only_allowlisted_entries_and_supports_root_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "tolik-file-tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var safeFile = Path.Combine(root, "safe.txt");
            var secretFile = Path.Combine(root, ".env");
            await File.WriteAllTextAsync(safeFile, "safe");
            await File.WriteAllTextAsync(secretFile, "secret");

            var service = new FileToolService(new PathIsolationPolicy([root]));
            var entries = await service.ListDirectoryAsync(root);

            Assert.Contains(Path.GetFullPath(safeFile), entries);
            Assert.DoesNotContain(Path.GetFullPath(secretFile), entries);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListDirectoryAsync(Path.Combine(root, "..")));
        }
        finally { Directory.Delete(root, true); }
    }
}
