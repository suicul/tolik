using TOLIK.Contracts;
using Xunit;

namespace TOLIK.Foundation.Tests;

/// <summary>
/// The bounded GUI target spec (CU-6 GUI actions v1): ONLY semantic addressing —
/// automation_id/name, length-capped, control-char free, no wildcards. Anything else
/// fails closed. The Core gates dispatch with IsWellFormed; the Agent re-parses with
/// TryParse (defense in depth — the same rules both sides, tested once here).
/// </summary>
public sealed class GuiTargetSpecTests
{
    [Theory]
    [InlineData("automation_id=btnSubmit")]
    [InlineData("name=Сохранить")]
    [InlineData("automation_id=a")] // the shortest legal id
    [InlineData("name= x ")] // the value is trimmed
    public void Well_formed_specs_parse(string spec)
    {
        Assert.True(GuiTargetSpec.IsWellFormed(spec));
        var target = GuiTargetSpec.TryParse(spec);
        Assert.NotNull(target);
    }

    [Fact]
    public void Automation_id_kind_and_value_are_exact()
    {
        var target = GuiTargetSpec.TryParse("automation_id=btnSubmit");
        Assert.NotNull(target);
        Assert.Equal(GuiTargetKind.AutomationId, target.Kind);
        Assert.Equal("btnSubmit", target.Value);
    }

    [Fact]
    public void Name_kind_and_value_are_exact_and_trimmed()
    {
        var target = GuiTargetSpec.TryParse("name=  Сохранить  ");
        Assert.NotNull(target);
        Assert.Equal(GuiTargetKind.Name, target.Kind);
        Assert.Equal("Сохранить", target.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("btnSubmit")] // no kind separator
    [InlineData("automation_id=")] // empty value
    [InlineData("automation_id")] // separator missing entirely
    [InlineData("=btnSubmit")] // empty kind
    [InlineData("xpath=//button[1]")] // unknown kind — fail closed
    [InlineData("coordinate=100,200")] // coordinates are NEVER addressable (SkillStore parity)
    [InlineData("automation_id=btn*")] // wildcard — fail closed
    [InlineData("name=Сохранить?")] // wildcard — fail closed
    [InlineData("name=line1\nline2")] // control char — fail closed
    public void Malformed_specs_fail_closed(string? spec)
    {
        Assert.False(GuiTargetSpec.IsWellFormed(spec));
        Assert.Null(GuiTargetSpec.TryParse(spec));
    }

    [Fact]
    public void Oversized_id_fails_closed_at_128()
    {
        var oversized = new string('a', GuiTargetSpec.MaxIdLength + 1);
        Assert.False(GuiTargetSpec.IsWellFormed($"automation_id={oversized}"));
        Assert.Null(GuiTargetSpec.TryParse($"automation_id={oversized}"));
        var exact = new string('a', GuiTargetSpec.MaxIdLength);
        Assert.True(GuiTargetSpec.IsWellFormed($"automation_id={exact}"));
    }

    [Fact]
    public void Oversized_name_fails_closed_at_256()
    {
        var oversized = new string('n', GuiTargetSpec.MaxNameLength + 1);
        Assert.False(GuiTargetSpec.IsWellFormed($"name={oversized}"));
        var exact = new string('n', GuiTargetSpec.MaxNameLength);
        Assert.True(GuiTargetSpec.IsWellFormed($"name={exact}"));
    }
}
