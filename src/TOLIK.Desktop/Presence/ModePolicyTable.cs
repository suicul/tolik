namespace TOLIK.Desktop.Presence;

/// <summary>
/// The typed policy table: mode → policy. Adding a mode later means adding a row,
/// not scattering conditionals through the renderer.
/// </summary>
public static class ModePolicyTable
{
    public static IReadOnlyDictionary<PresenceMode, ModePolicy> Table { get; } =
        new Dictionary<PresenceMode, ModePolicy>
        {
            //                 fps  idle  inter  part   mot    cap    dbg    passive
            [PresenceMode.Normal]       = new(30, 1.00, 1.00, true,  true,  true,  true,  false),
            [PresenceMode.Quiet]        = new(24, 0.55, 0.30, true,  true,  true,  true,  false),
            [PresenceMode.Focus]        = new(20, 0.35, 0.20, true,  true,  true,  true,  false),
            [PresenceMode.Gaming]       = new(15, 0.25, 0.00, false, true,  false, false, true),
            [PresenceMode.Presentation] = new(10, 0.15, 0.00, false, true,  false, false, true),
            [PresenceMode.Hidden]       = new(5,  0.00, 0.00, false, false, false, false, true),
            // Maximum responsiveness — the alarm-adjacent ceiling (60 fps).
            // Phase 1 has no alarm source: the palette stays calm blue; red is
            // never faked. The row differs from Normal only in responsiveness.
            [PresenceMode.Emergency]    = new(60, 1.00, 1.00, true,  true,  true,  true,  false),
        };

    /// <summary>Fail-closed lookup: an unmapped mode falls back to the calm Normal policy
    /// rather than inventing behavior. (All enum members are mapped today; this guards
    /// future enum additions that lag the table.)</summary>
    public static ModePolicy GetSafe(PresenceMode mode) =>
        Table.TryGetValue(mode, out var policy) ? policy : Table[PresenceMode.Normal];

    public static IEnumerable<PresenceMode> AllModes => Table.Keys;
}
