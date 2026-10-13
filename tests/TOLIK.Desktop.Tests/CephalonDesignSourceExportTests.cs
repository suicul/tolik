using System.Text.Json;
using TOLIK.Desktop.Export;
using TOLIK.Desktop.Performance;
using Xunit;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The design-source export contract (docs/VR.md + docs/android-app.md — ONE DESIGN
/// SOURCE): the export is valid JSON carrying the parametric mesh (LOD ladder +
/// per-part topology and placement); the vertex/face data is consistent (indices in
/// bounds, face counts matching); the export is deterministic (the same quality → the
/// identical document); and every quality tier produces a sane document (the
/// undefined tiers fail closed through the Lod ladder). The parameters are the design
/// source — never baked renderer-specific vertices.
/// </summary>
public sealed class CephalonDesignSourceExportTests
{
    private static JsonDocument Parse(RenderQuality quality)
    {
        var json = CephalonDesignSourceExporter.Export(quality);
        return JsonDocument.Parse(json);
    }

    [Theory]
    [InlineData(RenderQuality.Minimal)]
    [InlineData(RenderQuality.Low)]
    [InlineData(RenderQuality.Medium)]
    [InlineData(RenderQuality.High)]
    public void Export_is_valid_json_with_the_design_source_marker(RenderQuality quality)
    {
        using var doc = Parse(quality);
        Assert.Equal("cephalon-3d-v1", doc.RootElement.GetProperty("designSource").GetString());
        Assert.True(doc.RootElement.GetProperty("faceCount").GetInt32() > 0, "the body must carry faces");
        Assert.True(doc.RootElement.GetProperty("parts").GetArrayLength() > 0, "the body must carry parts");
        Assert.True(doc.RootElement.GetProperty("lod").GetProperty("SphereStacks").GetInt32() > 0);
    }

    [Fact]
    public void Part_topology_is_consistent_indices_in_bounds_counts_matching()
    {
        using var doc = Parse(RenderQuality.High);
        var totalFaces = 0;
        foreach (var part in doc.RootElement.GetProperty("parts").EnumerateArray())
        {
            var vertCount = part.GetProperty("baseVerts").GetArrayLength();
            Assert.True(vertCount > 0, "every part carries vertices");
            Assert.Equal(0, vertCount % 3); // x,y,z triplets
            var faces = part.GetProperty("faces").GetArrayLength();
            Assert.True(faces > 0, "every part carries faces");
            Assert.Equal(0, faces % 3); // index triplets
            Assert.Equal(faces / 3, part.GetProperty("faceCount").GetInt32());
            var maxIndex = -1;
            foreach (var idx in part.GetProperty("faces").EnumerateArray())
                maxIndex = Math.Max(maxIndex, idx.GetInt32());
            Assert.True(maxIndex < vertCount / 3, "face indices must reference existing vertices");
            Assert.False(string.IsNullOrWhiteSpace(part.GetProperty("kind").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(part.GetProperty("material").GetString()));
            totalFaces += faces / 3;
        }

        Assert.Equal(totalFaces, doc.RootElement.GetProperty("faceCount").GetInt32());
    }

    [Fact]
    public void Export_is_deterministic_same_quality_same_document()
    {
        var a = CephalonDesignSourceExporter.Export(RenderQuality.Medium);
        var b = CephalonDesignSourceExporter.Export(RenderQuality.Medium);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(RenderQuality.Minimal, RenderQuality.Low)]
    [InlineData(RenderQuality.Low, RenderQuality.Medium)]
    [InlineData(RenderQuality.Medium, RenderQuality.High)]
    public void Higher_quality_carries_at_least_as_many_faces(RenderQuality lower, RenderQuality higher)
    {
        var lowDoc = Parse(lower);
        using var highDoc = Parse(higher);
        Assert.True(
            highDoc.RootElement.GetProperty("faceCount").GetInt32() >= lowDoc.RootElement.GetProperty("faceCount").GetInt32(),
            "the LOD ladder must not shrink with rising quality");
    }
}
