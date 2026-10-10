namespace TOLIK.Desktop.Motion;

/// <summary>
/// Expression states of the Cephalon eye. Phase 1 was honest: only Idle was ever
/// activated (no Core connection existed). CE-2 wires the REAL Core expression
/// stream: Working/Recovering/Alert complete the machine — traits stay table rows,
/// the renderer never grows state-specific branches.
/// </summary>
public enum CephalonState
{
    Idle = 0,
    Thinking = 1,
    Speaking = 2,
    Denied = 3,
    Locked = 4,
    Working = 5,
    Recovering = 6,
    Alert = 7,
}
