using TOLIK.Desktop.Motion;
using TOLIK.Desktop.Performance;
using TOLIK.Desktop.Render3D;
using Xunit;

namespace TOLIK.Desktop.Tests;

/// <summary>
/// The software 3D renderer core (the orchestrator's completion of the 3D rework —
/// des-7's session died mid-write; the pipeline was wired and fixed, these tests
/// pin its contract): the deterministic scene (the same inputs → the identical
/// frame), the painter's far→near group ordering, the finite bounded projections,
/// the fail-closed guards on garbage geometry, the LOD ladder (the higher quality
/// carries at least as many faces), the mote limit, and the honest animation flow
/// (time moves the scene). The mesh stays parametric — the VR/Android design source.
/// </summary>
public sealed class Cephalon3DTests
{
    private static Scene3DFrame Render(
        Cephalon3DPipeline pipeline,
        double timeSeconds = 10.0,
        RenderQuality quality = RenderQuality.High,
        int moteLimit = 32,
        CephalonParameters? p = null,
        double size = 180.0,
        double centerX = 400.0,
        double centerY = 400.0) =>
        pipeline.Render(p ?? new CephalonParameters(), wobbleHarmonics: 3, timeSeconds, quality, centerX, centerY, size, moteLimit);

    [Fact]
    public void Normal_render_emits_a_sane_scene()
    {
        var frameSnapshot = Snapshot(Render(new Cephalon3DPipeline()));
        Assert.True(frameSnapshot.FaceCount > 0, "the body must emit faces");
        Assert.True(frameSnapshot.GroupCount > 0, "the faces must be grouped for the painter");
        Assert.True(frameSnapshot.SliceCount > 0, "the depth slices must be used");
    }

    [Fact]
    public void All_projected_faces_finite_and_within_a_sane_box()
    {
        var pipeline = new Cephalon3DPipeline();
        var frame = Render(pipeline, quality: RenderQuality.High, moteLimit: Cephalon3D.MoteSeedCount);
        for (var i = 0; i < frame.FaceCount; i++)
        {
            var f = frame.Faces[i];
            Assert.True(double.IsFinite(f.X0) && double.IsFinite(f.Y0) &&
                        double.IsFinite(f.X1) && double.IsFinite(f.Y1) &&
                        double.IsFinite(f.X2) && double.IsFinite(f.Y2), $"face {i} has a non-finite vertex");
            // The viewport box: everything lands around the center (the body + wobble + drift margin).
            Assert.InRange(Math.Abs(f.X0 - 400.0), 0.0, 2000.0);
            Assert.InRange(Math.Abs(f.Y0 - 400.0), 0.0, 2000.0);
        }
    }

    [Fact]
    public void Groups_are_ordered_far_to_near()
    {
        var frame = Render(new Cephalon3DPipeline());
        for (var g = 1; g < frame.GroupCount; g++)
        {
            Assert.True(frame.Groups[g].Slice >= frame.Groups[g - 1].Slice,
                $"group {g} (slice {frame.Groups[g].Slice}) breaks the painter's far→near ordering after slice {frame.Groups[g - 1].Slice}");
        }
    }

    [Fact]
    public void Render_is_deterministic_for_the_same_inputs()
    {
        var pipeline = new Cephalon3DPipeline();
        var first = Snapshot(Render(pipeline, timeSeconds: 7.5));
        var second = Snapshot(Render(pipeline, timeSeconds: 7.5));
        Assert.Equal(first, second, SceneSnapshotComparer.Instance);
    }

    [Fact]
    public void Time_moves_the_scene()
    {
        var pipeline = new Cephalon3DPipeline();
        var early = Snapshot(Render(pipeline, timeSeconds: 7.5));
        var late = Snapshot(Render(pipeline, timeSeconds: 7.6));
        Assert.NotEqual(early, late, SceneSnapshotComparer.Instance); // the animation flows honestly
    }

    [Fact]
    public void Garbage_geometry_fails_closed_to_the_empty_frame()
    {
        var pipeline = new Cephalon3DPipeline();
        Assert.Equal(0, Render(pipeline, size: double.NaN).FaceCount);      // NaN size — nothing drawn
        Assert.Equal(0, Render(pipeline, size: 0.0).FaceCount);             // degenerate size
        Assert.Equal(0, Render(pipeline, size: 1.0).FaceCount);             // the boundary: ≤ 1 rejects
        Assert.Equal(0, Render(pipeline, centerX: double.PositiveInfinity).FaceCount); // a non-finite center
    }

    [Fact]
    public void Higher_quality_carries_at_least_as_many_faces()
    {
        var pipeline = new Cephalon3DPipeline();
        var high = Render(pipeline, quality: RenderQuality.High).FaceCount;
        var low = Render(pipeline, quality: RenderQuality.Low).FaceCount;
        var minimal = Render(pipeline, quality: RenderQuality.Minimal).FaceCount;
        Assert.True(high >= low, $"High ({high}) must beat Low ({low})");
        Assert.True(low >= minimal, $"Low ({low}) must beat Minimal ({minimal})");
    }

    [Fact]
    public void Motes_respect_the_limit_and_stay_finite()
    {
        var pipeline = new Cephalon3DPipeline();
        var frame = Render(pipeline, quality: RenderQuality.High, moteLimit: 8);
        Assert.True(frame.MoteCount <= 8);
        for (var i = 0; i < frame.MoteCount; i++)
        {
            Assert.True(double.IsFinite(frame.Motes[i].X) && double.IsFinite(frame.Motes[i].Y));
            Assert.True(double.IsFinite(frame.Motes[i].Size) && double.IsFinite(frame.Motes[i].Alpha));
        }
    }

    // ── The snapshot helpers: the frame is REUSED by the pipeline, so the deep
    //    comparisons copy the live arrays before the next Render call. ──

    private sealed record SceneSnapshot(
        int FaceCount, int GroupCount, int MoteCount, int SliceCount,
        double[] Faces, int[] Groups, double[] Motes, double GlintX, double GlintY, double GlintRadius, bool GlintVisible);

    private static SceneSnapshot Snapshot(Scene3DFrame frame)
    {
        var faces = new double[frame.FaceCount * 6];
        for (var i = 0; i < frame.FaceCount; i++)
        {
            var f = frame.Faces[i];
            faces[i * 6] = f.X0; faces[i * 6 + 1] = f.Y0; faces[i * 6 + 2] = f.X1;
            faces[i * 6 + 3] = f.Y1; faces[i * 6 + 4] = f.X2; faces[i * 6 + 5] = f.Y2;
        }
        var groups = new int[frame.GroupCount * 5];
        for (var i = 0; i < frame.GroupCount; i++)
        {
            var g = frame.Groups[i];
            groups[i * 5] = g.Slice; groups[i * 5 + 1] = g.Material; groups[i * 5 + 2] = g.Bucket;
            groups[i * 5 + 3] = g.Start; groups[i * 5 + 4] = g.Count;
        }
        var motes = new double[frame.MoteCount * 4];
        for (var i = 0; i < frame.MoteCount; i++)
        {
            var m = frame.Motes[i];
            motes[i * 4] = m.X; motes[i * 4 + 1] = m.Y; motes[i * 4 + 2] = m.Size; motes[i * 4 + 3] = m.Alpha;
        }
        return new SceneSnapshot(frame.FaceCount, frame.GroupCount, frame.MoteCount, frame.SliceCount,
            faces, groups, motes, frame.GlintX, frame.GlintY, frame.GlintRadius, frame.GlintVisible);
    }

    private sealed class SceneSnapshotComparer : IEqualityComparer<SceneSnapshot>
    {
        public static readonly SceneSnapshotComparer Instance = new();
        public bool Equals(SceneSnapshot? x, SceneSnapshot? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.FaceCount == y.FaceCount && x.GroupCount == y.GroupCount && x.MoteCount == y.MoteCount &&
                   x.SliceCount == y.SliceCount && x.GlintX.Equals(y.GlintX) && x.GlintY.Equals(y.GlintY) &&
                   x.GlintRadius.Equals(y.GlintRadius) && x.GlintVisible == y.GlintVisible &&
                   x.Faces.SequenceEqual(y.Faces) && x.Groups.SequenceEqual(y.Groups) && x.Motes.SequenceEqual(y.Motes);
        }
        public int GetHashCode(SceneSnapshot obj) => obj.FaceCount;
    }
}
