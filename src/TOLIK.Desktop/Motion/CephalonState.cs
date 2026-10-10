namespace TOLIK.Desktop.Motion;

/// <summary>
/// Expression states of the Cephalon eye. Phase 1 is honest: only <see cref="Idle"/>
/// is ever activated (there is no Core connection to report thinking/speaking/denial).
/// Thinking/Speaking/Denied/Locked rows exist in the mapping table for Phase 2/3 —
/// the renderer reads traits from the table, never from hardcoded branches.
/// </summary>
public enum CephalonState
{
    Idle = 0,
    Thinking = 1,
    Speaking = 2,
    Denied = 3,
    Locked = 4,
}
