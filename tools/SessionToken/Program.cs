// Prints (or writes) a TOLIK session token for scripts and live probes.
//
// This exists so scripts/get-session-token.ps1 does NOT reimplement the device
// challenge/signature exchange in PowerShell. A second implementation had already drifted:
// it read the DPAPI blob as base64 and called an API that Windows PowerShell 5.1 does not
// have. Here the script and the desktop client drive exactly one code path —
// ClientTokenSource, which prefers the enrolled device key and falls back to the dev token
// only when no identity exists.
//
// Usage:
//   dotnet run --project tools/SessionToken [-- --out <path>] [-- --quiet]
// Writes the token to stdout, or (with --out) to that file. Exit 0 = token obtained.
using TOLIK.Desktop.Bridge;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string? outPath = null;
bool quiet = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;
        case "--quiet":
            quiet = true;
            break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            Console.Error.WriteLine("usage: SessionToken [--out <path>] [--quiet]");
            return 2;
    }
}

CoreLinkOptions options = CoreLinkOptions.FromEnvironment();
using ClientTokenSource tokens = ClientTokenSource.Create(options);

SessionToken? token = await tokens.AcquireAsync(CancellationToken.None);
if (token is null)
{
    Console.Error.WriteLine(
        $"session update failed: {tokens.LastDetail ?? "Core did not return a session"}");
    return 1;
}

if (outPath is not null)
{
    string? directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
    File.WriteAllText(outPath, token.Value.Value);
    if (!quiet)
    {
        Console.WriteLine($"token written to {outPath}");
    }
}
else
{
    Console.WriteLine(token.Value.Value);
}

return 0;
