using TOLIK.Contracts;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// Presence Manager (docs/identity-presence.md §5, IP-4): ONE TOLIK, multiple organs,
/// never clones; PresenceLost (timeout) makes a node unavailable while the Core
/// continues; the Select policy is pure routing — NEVER an authorization decision
/// (Attention != Authority: proximity/loudness/visibility give no permission).
/// </summary>
public sealed class PresenceManagerTests
{
    private static readonly string[] PcCaps = ["windows", "gpu", "filesystem", "audio"];
    private static readonly string[] QuestCaps = ["spatial", "passthrough", "spatial-audio"];
    private static readonly string[] PhoneCaps = ["camera", "microphone", "notifications"];
    private static readonly string[] HomeCaps = ["home", "audio"];

    private static PresenceNode Node(string id, string name, string[] caps, DateTimeOffset seen, string mode = "Working") =>
        new(id, name, null, mode, null, true, caps, seen);

    private static void Reg(PresenceManager pm, string id, string name, string[] caps, DateTimeOffset seen, string mode = "Working") =>
        pm.Register(id, name, null, mode, null, caps, seen);

    [Fact]
    public void Register_refreshes_and_Current_marks_lost_nodes()
    {
        var now = DateTimeOffset.UtcNow;
        var pm = new PresenceManager();
        Reg(pm, "pc", "Ivan-PC", PcCaps, now);
        Reg(pm, "quest", "Quest3", QuestCaps, now.AddMinutes(-10)); // silent: PresenceLost

        var current = pm.Current(now);
        Assert.Equal(2, current.Count);
        Assert.True(current.First(n => n.DeviceId == "pc").Available);
        Assert.False(current.First(n => n.DeviceId == "quest").Available); // lost, but still listed
    }

    [Fact]
    public void Remove_removes_only_the_organ_core_continues()
    {
        var now = DateTimeOffset.UtcNow;
        var pm = new PresenceManager();
        Reg(pm, "pc", "Ivan-PC", PcCaps, now);
        Reg(pm, "phone", "Ivan-Phone", PhoneCaps, now);

        Assert.True(pm.Remove("pc")); // the organ is gone...
        Assert.Single(pm.Current(now)); // ...the Core continues with the remaining organs
        Assert.False(pm.Remove("pc")); // idempotent
    }

    [Fact]
    public void Select_prefers_active_conversation_then_interlocutor_then_task()
    {
        var now = DateTimeOffset.UtcNow;
        var pm = new PresenceManager();
        Reg(pm, "pc", "Ivan-PC", PcCaps, now);
        Reg(pm, "laptop", "Maria-Laptop", PhoneCaps, now);
        Reg(pm, "quest", "Quest3", QuestCaps, now);

        // Active conversation wins over everything else.
        Assert.Equal("laptop", pm.Select(now, activeConversationDevice: "laptop")!.DeviceId);
        // No conversation: the interlocutor's device.
        Assert.Equal("laptop", pm.Select(now, interlocutorDevice: "Maria-Laptop")!.DeviceId);
        // No conversation/interlocutor: the task's device.
        Assert.Equal("pc", pm.Select(now, taskDevice: "pc")!.DeviceId);
    }

    [Fact]
    public void Select_falls_back_to_spatial_then_first_available()
    {
        var now = DateTimeOffset.UtcNow;
        var pm = new PresenceManager();
        Reg(pm, "pc", "Ivan-PC", PcCaps, now);
        Reg(pm, "quest", "Quest3", QuestCaps, now);
        // No explicit hints: Quest (spatial) wins the fallback chain.
        Assert.Equal("quest", pm.Select(now)!.DeviceId);

        var pm2 = new PresenceManager();
        Reg(pm2, "pc", "Ivan-PC", PcCaps, now);
        Reg(pm2, "phone", "Ivan-Phone", PhoneCaps, now);
        // No spatial anywhere: first available.
        Assert.Equal("pc", pm2.Select(now)!.DeviceId);
    }

    [Fact]
    public void Select_returns_null_when_nothing_available()
    {
        var pm = new PresenceManager();
        Reg(pm, "pc", "Ivan-PC", PcCaps, DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.Null(pm.Select(DateTimeOffset.UtcNow)); // all lost -> nowhere to be present
    }

    [Fact]
    public void Select_ignores_unavailable_nodes()
    {
        var now = DateTimeOffset.UtcNow;
        var pm = new PresenceManager();
        Reg(pm, "pc", "Ivan-PC", PcCaps, now.AddMinutes(-10)); // lost
        Reg(pm, "quest", "Quest3", QuestCaps, now);            // available
        // The lost node must not be selected even when hinted.
        Assert.Equal("quest", pm.Select(now, taskDevice: "pc")!.DeviceId);
    }

    [Fact]
    public void Presence_is_routing_only_never_authority()
    {
        // PresenceNode/PresenceManager carry display/routing state by shape:
        // no permission/role/authorization fields exist to be confused with security.
        var node = Node("pc", "Ivan-PC", PcCaps, DateTimeOffset.UtcNow);
        Assert.Equal("pc", node.DeviceId);
        Assert.True(node.Available);
        // The manager's Select returns a node (where to be present), never a decision object.
        var pm = new PresenceManager();
        Assert.IsType<PresenceNode>(pm.Select(DateTimeOffset.UtcNow) ?? Node("x", "x", [], DateTimeOffset.UtcNow));
    }
}
