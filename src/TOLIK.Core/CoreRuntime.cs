namespace TOLIK.Core;

public static class CoreRuntime
{
    public static CoreRuntimeDescriptor Describe() => new(
        ComponentName: "TOLIK.Core",
        IsAuthorizationAuthority: true,
        ExecutesOperatingSystemCommands: false);
}

public sealed record CoreRuntimeDescriptor(
    string ComponentName,
    bool IsAuthorizationAuthority,
    bool ExecutesOperatingSystemCommands);
