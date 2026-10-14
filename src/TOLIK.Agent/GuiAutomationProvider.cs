using System.Windows.Automation;
using TOLIK.Contracts;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TOLIK.Desktop.Tests")]

namespace TOLIK.Agent;

/// <summary>
/// CU-6 GUI actions v1 — the UIA executor that gives the CU stack its hands. Semantic
/// addressing ONLY (<see cref="GuiTargetSpec"/>: automation_id/name — never coordinates,
/// never window handles, never wildcards; SkillStore parity). READ (GuiRead) and
/// INVOKE (GuiClick: Invoke → Toggle → SelectionItem — honest failure when the element
/// exposes none). The search scope is the interactive session's UI tree from the root —
/// the agent runs in the user's session, so it sees what the user sees (§50: the same
/// trust class as CaptureScreen; AuditAlways on both capabilities). Stateless, pure
/// dispatch logic; UIA handles the platform. Fail closed everywhere: a malformed spec,
/// a missing element, an uninvocable element — all honest failures, never guesses.
/// </summary>
internal static class GuiAutomationProvider
{
    private const int MaxResultLength = 4096;

    /// <summary>Reads a UI element's observable state (GuiRead — READ_ONLY).</summary>
    public static string Read(string targetSpec)
    {
        var element = FindOrThrow(targetSpec);
        var current = element.Current;

        var parts = new List<string>
        {
            $"controlType={current.ControlType?.ProgrammaticName ?? "unknown"}",
            $"name={Truncate(current.Name ?? string.Empty, 256)}",
            $"isEnabled={current.IsEnabled.ToString().ToLowerInvariant()}",
            $"isOffscreen={current.IsOffscreen.ToString().ToLowerInvariant()}"
        };

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
            valuePatternObject is ValuePattern valuePattern)
        {
            parts.Add($"value={Truncate(valuePattern.Current.Value ?? string.Empty, 512)}");
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePatternObject) &&
            togglePatternObject is TogglePattern togglePattern)
        {
            parts.Add($"toggleState={togglePattern.Current.ToggleState}");
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPatternObject) &&
            selectionPatternObject is SelectionItemPattern selectionPattern)
        {
            parts.Add($"isSelected={selectionPattern.Current.IsSelected.ToString().ToLowerInvariant()}");
        }

        var result = string.Join("; ", parts);
        return Truncate(result, MaxResultLength);
    }

    /// <summary>Invokes a UI element (GuiClick — MODIFY): Invoke → Toggle → Select;
    /// an honest failure when the element exposes none of the invocation patterns.</summary>
    public static string Click(string targetSpec)
    {
        var element = FindOrThrow(targetSpec);

        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObject) &&
            invokeObject is InvokePattern invoke)
        {
            invoke.Invoke();
            return $"invoked={element.Current.AutomationId ?? element.Current.Name ?? "element"}";
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggleObject) &&
            toggleObject is TogglePattern toggle)
        {
            toggle.Toggle();
            return $"toggled={element.Current.AutomationId ?? element.Current.Name ?? "element"};state={toggle.Current.ToggleState}";
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionObject) &&
            selectionObject is SelectionItemPattern selection)
        {
            selection.Select();
            return $"selected={element.Current.AutomationId ?? element.Current.Name ?? "element"}";
        }

        throw new InvalidOperationException(
            "gui_click: the element exposes no invocation pattern (Invoke/Toggle/Select) — refusing to guess.");
    }

    private static AutomationElement FindOrThrow(string targetSpec)
    {
        // Defense in depth: the Core validated well-formedness before dispatch, the
        // Agent re-parses (the wire could carry anything — §23 tool output is untrusted).
        var target = GuiTargetSpec.TryParse(targetSpec)
            ?? throw new UnauthorizedAccessException($"gui target spec is malformed (bounded forms only: automation_id=<id> | name=<text>): {Truncate(targetSpec, 64)}");

        var condition = target.Kind switch
        {
            GuiTargetKind.AutomationId => new PropertyCondition(AutomationElement.AutomationIdProperty, target.Value),
            GuiTargetKind.Name => new PropertyCondition(AutomationElement.NameProperty, target.Value),
            _ => throw new UnauthorizedAccessException("gui target kind is not supported (fail closed).")
        };

        // The interactive session's UI tree from the root; FindFirst (no caching layer
        // in v1 — a documented boundary: a busy desktop can make this search slow).
        var element = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition)
            ?? throw new InvalidOperationException(
                $"gui element not found ({target.Kind}={Truncate(target.Value, 64)}) — the honest failure, never a guess.");

        return element;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
