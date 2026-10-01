namespace TOLIK.Core;

/// <summary>
/// Memory provenance origin (§10-11: development memory must never silently
/// become permanent memory).
/// - lived: interactive experience with the owner (default for all live writes).
/// - imported: ceremony/batch import (Genesis initial knowledge).
/// - system: machine-derived summaries (compaction, daily review).
/// - development: explicitly marked dev/test writes. Test suites use separate
///   databases by design; this value exists for explicit dev marking.
/// </summary>
public static class MemoryOrigin
{
    public const string Lived = "lived";
    public const string Imported = "imported";
    public const string System = "system";
    public const string Development = "development";

    public const string Default = Lived;

    public static bool IsValid(string? origin) =>
        origin is Lived or Imported or System or Development;

    public static string Normalize(string? origin) =>
        IsValid(origin) ? origin! : throw new ArgumentException("Invalid memory origin.");
}
