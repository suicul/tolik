using System.Runtime.CompilerServices;
using TOLIK.Desktop.Motion;
using TOLIK.Desktop.Performance;
using TOLIK.Desktop.Rendering;

[assembly: InternalsVisibleTo("TOLIK.Desktop.Tests")]

namespace TOLIK.Desktop.Render3D;

/// <summary>One projected, shaded triangle ready for the WPF drawer.</summary>
public struct SceneFace
{
    public double X0, Y0, X1, Y1, X2, Y2;

    /// <summary>Internal scatter key: slice·(materials·buckets) + material·buckets + bucket.</summary>
    internal int Key;
}

/// <summary>One draw batch: a contiguous run of faces sharing (slice, material, shade bucket).</summary>
public readonly struct SceneGroup
{
    public SceneGroup(byte slice, byte material, byte bucket, int start, int count)
    {
        Slice = slice;
        Material = material;
        Bucket = bucket;
        Start = start;
        Count = count;
    }

    /// <summary>Painter depth slice — groups emit far (0) to near, and motes interleave per slice.</summary>
    public byte Slice { get; }

    public byte Material { get; }

    public byte Bucket { get; }

    public int Start { get; }

    public int Count { get; }
}

/// <summary>One projected energy mote (drawn as an ellipse by the drawer).</summary>
public struct SceneMote
{
    public double X;
    public double Y;
    public double Size;
    public double Alpha;
    public byte Slice;
}

/// <summary>
/// One frame of 3D output. Owned by the pipeline, reused every frame — the
/// drawer must consume it before the next Render call. Groups are ordered
/// far→near (the painter's algorithm); motes are sorted far→near by slice so
/// the drawer can interleave them with the face groups of their slice.
/// </summary>
public sealed class Scene3DFrame
{
    internal const int MaxMotes = Cephalon3D.MoteSeedCount;

    internal SceneFace[] Faces = new SceneFace[1024];
    internal SceneGroup[] Groups = new SceneGroup[MaxGroupKeys];
    internal SceneMote[] Motes = new SceneMote[MaxMotes];
    internal readonly int[] PartVisibleFaces = new int[16];

    /// <summary>Number of faces scattered into <see cref="Groups"/> (backfaces already culled).</summary>
    public int FaceCount { get; internal set; }

    /// <summary>Number of draw groups (far→near).</summary>
    public int GroupCount { get; internal set; }

    /// <summary>Number of motes (far→near by slice).</summary>
    public int MoteCount { get; internal set; }

    /// <summary>Painter slices used this frame.</summary>
    public int SliceCount { get; internal set; }

    /// <summary>Specular anchor: projected pupil-front position and radius in
    /// pixels; visible when the shutter leaves the pupil uncovered.</summary>
    public double GlintX { get; internal set; }

    public double GlintY { get; internal set; }

    public double GlintRadius { get; internal set; }

    public bool GlintVisible { get; internal set; }

    internal const int MaxDepthSlices = 20;
    internal const int MaxGroupKeys = MaxDepthSlices * (int)MaterialKind.Count * Cephalon3D.ShadeBuckets;
}

/// <summary>
/// The software 3D renderer core: per-frame vertex animation, perspective
/// projection, backface culling, key-light flat shading with the emissive
/// grade, and the depth-sliced painter's algorithm — all deterministic,
/// framework-free, allocation-free in the hot path. The mesh stays parametric
/// (CephalonMeshBuilder) so the same body ports to the VR/Android client
/// (docs/VR.md). The WPF side only turns the emitted draw list into brushes.
/// </summary>
public sealed class Cephalon3DPipeline
{
    private const double MinViewZ = 0.25;
    private const double BackfaceAreaEps = 0.01; // px² — slivers cost fill and read as noise

    private readonly Dictionary<RenderQuality, CephalonMesh> _meshes = new();
    private readonly Scene3DFrame _frame = new();

    // counting-sort scratch (grouped emission) — reused every frame
    private readonly int[] _groupCounts = new int[Scene3DFrame.MaxGroupKeys];
    private readonly int[] _groupStarts = new int[Scene3DFrame.MaxGroupKeys];
    private readonly int[] _groupCursor = new int[Scene3DFrame.MaxGroupKeys];
    private SceneFace[] _scratch = new SceneFace[1024];
    private readonly int[] _moteSliceCounts = new int[Scene3DFrame.MaxDepthSlices];
    private readonly SceneMote[] _moteScratch = new SceneMote[Scene3DFrame.MaxMotes];

    // deterministic mote cloud (same seed lineage as the 2D depth pass)
    private readonly MoteSeed[] _motes = CreateMotes(Cephalon3D.MoteSeedCount, seed: 0x5EEDC0DE7UL);

    /// <summary>
    /// Renders one frame from the animator's parameters. The returned frame is
    /// owned by the pipeline and stays valid until the next call. Pure function
    /// of (parameters, harmonics, time, quality, center, size, moteLimit).
    /// </summary>
    public Scene3DFrame Render(
        CephalonParameters p,
        int wobbleHarmonics,
        double timeSeconds,
        RenderQuality quality,
        double centerX,
        double centerY,
        double size,
        int moteLimit)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (!double.IsFinite(size) || size <= 1.0 || !double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            return EmptyFrame();
        }

        CephalonMesh mesh = MeshFor(quality);
        DepthShedding shed = CephalonDepth.Shed(quality);
        int slices = Math.Min(mesh.Lod.DepthSlices, Scene3DFrame.MaxDepthSlices);

        double time = double.IsFinite(timeSeconds) ? Math.Max(0.0, timeSeconds) : 0.0;
        double gx = San(p.PupilOffsetX, -1.0, 1.0);
        double gy = San(p.PupilOffsetY, -1.0, 1.0);
        double glow = San(p.GlowIntensity, 0.0, 1.0);
        double spec = San(p.SpectrumIntensity, 0.0, 1.0);
        double energy = San(p.ParticleEnergy, 0.0, 1.0);
        double expansion = San(p.ShellExpansion, 0.9, 1.1);
        double eyeScale = 1.0 + ((expansion - 1.0) * 0.3);
        double wobbleAmp = San(p.WobbleAmp, 0.0, CephalonGeometry.MaxWobble);
        double wobblePhase = San(p.WobblePhase, -Math.PI, Math.PI);
        int harmonics = Math.Clamp(wobbleHarmonics, 1, CephalonGeometry.MaxHarmonics);

        // Structural body tilt: a fixed default lean (volume is the default) +
        // the gaze head-look + the ShellTilt silhouette sway.
        double bodyTiltX = Cephalon3D.BodyTiltX + (gy * Cephalon3D.GazeBodyPitch);
        double bodyTiltY = Cephalon3D.BodyTiltY + (gx * Cephalon3D.GazeBodyYaw) + San(p.ShellTilt, -0.05, 0.05);
        Mat4 body = Mat4.RotateX(bodyTiltX) * Mat4.RotateY(bodyTiltY);

        // ---- stage 1: world vertices (no allocation; wobble is per-vertex) ----
        double zMin = double.PositiveInfinity;
        double zMax = double.NegativeInfinity;

        for (int pi = 0; pi < mesh.Parts.Length; pi++)
        {
            MeshPart part = mesh.Parts[pi];
            V3[] world = part.WorldVerts;

            Mat4 model = part.Kind switch
            {
                PartKind.RingOuter => RingModel(body, part, p.ShellRotation, expansion),
                PartKind.RingMiddle => RingModel(body, part, p.Shell2Rotation,
                    expansion * (1.0 + (0.03 * spec * Math.Sin(time * 9.0)))),
                PartKind.AnchorArcs => RingModel(body, part, p.BracketRotation,
                    1.0 + ((expansion - 1.0) * 0.6)),
                PartKind.EyeSphere => EyeModel(body, Mat4.Identity, eyeScale),
                PartKind.Iris => EyeModel(body,
                    GazeMatrix(gx, gy, Cephalon3D.GazeCapArc * 0.55) * Mat4.RotateZ(San(p.IrisRotation, -Math.PI, Math.PI)),
                    eyeScale),
                PartKind.Pupil => EyeModel(body, GazeMatrix(gx, gy, Cephalon3D.GazeCapArc), eyeScale),
                _ => EyeModel(body, Mat4.Identity, eyeScale), // lids
            };

            if (part.DynamicVerts)
            {
                GenerateDynamicVerts(part, p, model);
            }
            else if (part.BasePolar is null)
            {
                for (int i = 0; i < part.BaseVerts.Length; i++)
                {
                    world[i] = Mat4.TransformPoint(model, part.BaseVerts[i]);
                }
            }
            else
            {
                // Wobbling shell band: radial harmonic flex, then the model transform.
                for (int i = 0; i < part.BaseVerts.Length; i++)
                {
                    V3 v = part.BaseVerts[i];
                    if (wobbleAmp > 0.0)
                    {
                        double s = 1.0 + (wobbleAmp * Math.Sin((harmonics * part.BasePolar[i]) + wobblePhase));
                        v = new V3(v.X * s, v.Y * s, v.Z);
                    }
                    world[i] = Mat4.TransformPoint(model, v);
                }
            }

            for (int i = 0; i < world.Length; i++)
            {
                double z = world[i].Z;
                if (z < zMin) zMin = z;
                if (z > zMax) zMax = z;
            }
        }

        if (!(zMax > zMin))
        {
            zMin = 0.0;
            zMax = 1.0;
        }

        // ---- stage 2: project → cull → shade → slice (counting pass) ----
        double focal = Cephalon3D.FocalPerSize * size;
        double sliceSpan = (zMax - zMin) / slices;
        Array.Clear(_groupCounts, 0, _groupCounts.Length);
        Array.Clear(_moteSliceCounts, 0, slices);
        Array.Clear(_frame.PartVisibleFaces, 0, _frame.PartVisibleFaces.Length);

        int emitted = 0;
        EnsureScratchCapacity(mesh.FaceCount);

        for (int pi = 0; pi < mesh.Parts.Length; pi++)
        {
            MeshPart part = mesh.Parts[pi];
            Cephalon3D.MaterialShade def = Cephalon3D.Material(part.Material);
            bool directional = shed.DirectionalShading;
            int visible = 0;

            for (int fi = 0; fi < part.Faces.Length; fi += 3)
            {
                V3 wa = part.WorldVerts[part.Faces[fi]];
                V3 wb = part.WorldVerts[part.Faces[fi + 1]];
                V3 wc = part.WorldVerts[part.Faces[fi + 2]];

                double vza = Cephalon3D.CameraDist - wa.Z;
                double vzb = Cephalon3D.CameraDist - wb.Z;
                double vzc = Cephalon3D.CameraDist - wc.Z;
                if (vza < MinViewZ || vzb < MinViewZ || vzc < MinViewZ)
                {
                    continue;
                }

                double ax = centerX + (focal * wa.X / vza);
                double ay = centerY - (focal * wa.Y / vza);
                double bx = centerX + (focal * wb.X / vzb);
                double by = centerY - (focal * wb.Y / vzb);
                double cx = centerX + (focal * wc.X / vzc);
                double cy = centerY - (focal * wc.Y / vzc);

                // Backface cull: the projection flips Y, so front-facing
                // triangles are CLOCKWISE on screen (negative signed area).
                double area2 = ((bx - ax) * (cy - ay)) - ((cx - ax) * (by - ay));
                if (area2 >= -BackfaceAreaEps)
                {
                    continue;
                }

                V3 normal = V3.Cross(wb - wa, wc - wa).Normalized();
                if (normal.LengthSquared < 0.5)
                {
                    continue; // degenerate
                }

                double zc = (wa.Z + wb.Z + wc.Z) * (1.0 / 3.0);

                // Key-light flat shading + the emissive grade (the shade floor
                // IS the glow; the lit face approaches the hot blue).
                double floor = def.FloorBase + (def.GlowBoost * glow);
                double shade = directional
                    ? floor + ((1.0 - floor) * Math.Max(0.0, V3.Dot(normal, Cephalon3D.KeyLightDir)))
                    : floor;
                if (def.UseAtmosphere)
                {
                    shade *= CephalonDepth.Atmosphere(WorldToDepth01(zc));
                }
                if (def.SpectrumBoost > 0.0 && spec > 0.0)
                {
                    // Voice spectrum shimmers the mid chassis (CE-3 axis).
                    shade += def.SpectrumBoost * spec *
                        (0.55 + (0.45 * Math.Sin((time * def.SpectrumShimmerRate) + (fi * 0.7))));
                }

                int bucket = (int)Math.Clamp(shade * Cephalon3D.ShadeBuckets, 0.0, Cephalon3D.ShadeBuckets - 1.0);
                int slice = Math.Clamp((int)((zc - zMin) / sliceSpan), 0, slices - 1);
                int key = ((slice * (int)MaterialKind.Count) + (int)part.Material) * Cephalon3D.ShadeBuckets + bucket;

                _groupCounts[key]++;
                _scratch[emitted] = new SceneFace
                {
                    X0 = ax, Y0 = ay, X1 = bx, Y1 = by, X2 = cx, Y2 = cy, Key = key,
                };
                emitted++;
                visible++;
            }

            _frame.PartVisibleFaces[pi] = visible;
        }

        // ---- stage 3: motes (3D orbits; parallax comes free) ----
        int moteCount = Math.Clamp(moteLimit, 0, Scene3DFrame.MaxMotes);
        int motesEmitted = 0;
        for (int i = 0; i < moteCount; i++)
        {
            ref readonly MoteSeed q = ref _motes[i];
            double speed = q.Speed * (0.6 + (0.8 * energy) + (0.6 * spec));
            double a = q.Angle + (speed * time);
            double rm = q.Radius * expansion * (1.0 + (0.02 * Math.Sin((time * q.Speed * 3.0) + q.Phase)));

            V3 local = new V3(
                rm * Math.Cos(a) * Math.Cos(q.Incline),
                rm * Math.Sin(a),
                -rm * Math.Cos(a) * Math.Sin(q.Incline));
            V3 w = Mat4.TransformPoint(body, local);

            double vz = Cephalon3D.CameraDist - w.Z;
            if (vz < MinViewZ)
            {
                continue;
            }

            double mx = centerX + (focal * w.X / vz);
            double my = centerY - (focal * w.Y / vz);
            double atmosphere = CephalonDepth.Atmosphere(WorldToDepth01(w.Z));
            double alpha = Math.Clamp(
                (0.25 + (0.75 * energy)) * (0.55 + (0.45 * Math.Sin((time * 2.2) + q.Phase))) * atmosphere,
                0.0, 1.0);
            double moteSize = q.Size * Cephalon3D.MoteSizePerFocal * focal / vz;

            _moteScratch[motesEmitted] = new SceneMote
            {
                X = mx,
                Y = my,
                Size = moteSize,
                Alpha = alpha,
                Slice = (byte)Math.Clamp((int)((w.Z - zMin) / sliceSpan), 0, slices - 1),
            };
            _moteSliceCounts[_moteScratch[motesEmitted].Slice]++;
            motesEmitted++;
        }

        // counting-sort motes far→near by slice
        int moteRun = 0;
        for (int s = 0; s < slices; s++)
        {
            int c = _moteSliceCounts[s];
            _moteSliceCounts[s] = moteRun;
            moteRun += c;
        }
        for (int i = 0; i < motesEmitted; i++)
        {
            _frame.Motes[_moteSliceCounts[_moteScratch[i].Slice]++] = _moteScratch[i];
        }

        // ---- stage 4: scatter faces into painter order + emit groups ----
        int running = 0;
        for (int key = 0; key < _groupCounts.Length; key++)
        {
            _groupStarts[key] = running;
            _groupCursor[key] = running;
            running += _groupCounts[key];
        }

        for (int i = 0; i < emitted; i++)
        {
            _frame.Faces[_groupCursor[_scratch[i].Key]++] = _scratch[i];
        }

        int groupCount = 0;
        int sliceStride = (int)MaterialKind.Count * Cephalon3D.ShadeBuckets;
        for (int slice = 0; slice < slices; slice++)
        {
            for (int mat = 0; mat < (int)MaterialKind.Count; mat++)
            {
                for (int bucket = 0; bucket < Cephalon3D.ShadeBuckets; bucket++)
                {
                    int key = (slice * sliceStride) + (mat * Cephalon3D.ShadeBuckets) + bucket;
                    int count = _groupCounts[key];
                    if (count > 0)
                    {
                        _frame.Groups[groupCount++] = new SceneGroup(
                            (byte)slice, (byte)mat, (byte)bucket, _groupStarts[key], count);
                    }
                }
            }
        }

        // ---- specular anchor (pupil front) ----
        ComputeGlint(mesh, p, gx, gy, eyeScale, centerX, centerY, focal);

        _frame.FaceCount = running;
        _frame.GroupCount = groupCount;
        _frame.MoteCount = motesEmitted;
        _frame.SliceCount = slices;
        return _frame;
    }

    /// <summary>The mesh at one quality tier (built lazily once; world verts valid after Render).</summary>
    public CephalonMesh MeshFor(RenderQuality quality)
    {
        if (!_meshes.TryGetValue(quality, out CephalonMesh? mesh))
        {
            mesh = CephalonMeshBuilder.Build(Cephalon3D.Lod(quality));
            _meshes[quality] = mesh;
        }
        return mesh;
    }

    /// <summary>
    /// Backface test in screen space: front-facing triangles (camera on +Z,
    /// projection flips Y) are CLOCKWISE — negative signed area.
    /// </summary>
    public static bool IsFrontFacing(double ax, double ay, double bx, double by, double cx, double cy) =>
        (((bx - ax) * (cy - ay)) - ((cx - ax) * (by - ay))) < -BackfaceAreaEps;

    // ---------- helpers ----------

    private Scene3DFrame EmptyFrame()
    {
        _frame.FaceCount = 0;
        _frame.GroupCount = 0;
        _frame.MoteCount = 0;
        _frame.SliceCount = 0;
        _frame.GlintVisible = false;
        return _frame;
    }

    private static Mat4 RingModel(Mat4 body, MeshPart part, double spin, double expansion) =>
        body
        * Mat4.Translate(0.0, 0.0, part.ZBase)
        * Mat4.RotateX(part.TiltX)
        * Mat4.RotateY(part.TiltY)
        * Mat4.RotateZ(San(spin, -Math.PI, Math.PI))
        * Mat4.Scale(expansion, expansion, 1.0);

    private static Mat4 EyeModel(Mat4 body, Mat4 local, double eyeScale) =>
        body
        * Mat4.Translate(0.0, 0.0, Cephalon3D.ZEye)
        * local
        * Mat4.Scale(eyeScale, eyeScale, eyeScale);

    /// <summary>Cap gaze rotation: +Z cap axis turns toward the gaze direction.</summary>
    private static Mat4 GazeMatrix(double gx, double gy, double arc) =>
        Mat4.RotateY(gx * arc) * Mat4.RotateX(gy * arc);

    /// <summary>Per-frame vertices for pupil (dilation + gaze) and lids (shutter travel).</summary>
    private static void GenerateDynamicVerts(MeshPart part, CephalonParameters p, Mat4 model)
    {
        V3[] world = part.WorldVerts;
        switch (part.Kind)
        {
            case PartKind.Pupil:
            {
                const int n = Cephalon3D.IrisFacets;
                double eyeR = Cephalon3D.EyeRadius;
                double scale = San(p.PupilScale, CephalonParameters.PupilScaleMin, CephalonParameters.PupilScaleMax);
                double sinT = Math.Min(0.85, Cephalon3D.PupilRadiusFrac * scale);
                double t = Math.Asin(sinT);
                for (int k = 0; k < n; k++)
                {
                    double phi = (Math.PI * 2.0 * k / n) + (Math.PI / 8.0);
                    world[k] = Mat4.TransformPoint(model, new V3(
                        eyeR * sinT * Math.Cos(phi),
                        eyeR * sinT * Math.Sin(phi),
                        eyeR * Math.Cos(t)));
                }
                world[n] = Mat4.TransformPoint(model, new V3(0.0, 0.0, eyeR * (1.0 + Cephalon3D.PupilApexLift)));
                break;
            }

            case PartKind.LidTop:
            case PartKind.LidBottom:
            {
                double open = San(p.EyeOpen, 0.0, 1.0);
                double edge = Cephalon3D.LidParkedDeg
                    + ((Cephalon3D.LidClosedDeg - Cephalon3D.LidParkedDeg) * (1.0 - open));
                double edgeRad = edge * Math.PI / 180.0;

                int rows = part.GridRows;
                int cols = part.GridCols;
                double rl = Cephalon3D.EyeRadius * Cephalon3D.LidRadiusFactor;
                bool top = part.Kind == PartKind.LidTop;
                double seam = top ? 0.0 : Math.PI / (cols - 1); // staggered shutter seams

                int vi = 0;
                for (int r = 0; r < rows; r++)
                {
                    double frac = (double)r / (rows - 1); // 0 at first row … 1 at last
                    double theta = top
                        ? edgeRad * frac
                        : Math.PI - (edgeRad * (1.0 - frac));
                    double st = Math.Sin(theta), ct = Math.Cos(theta);
                    for (int i = 0; i < cols; i++, vi++)
                    {
                        double phi = seam + (Math.PI * 2.0 * i / (cols - 1));
                        world[vi] = Mat4.TransformPoint(model, new V3(
                            rl * st * Math.Cos(phi),
                            rl * ct,
                            rl * st * Math.Sin(phi)));
                    }
                }
                break;
            }
        }
    }

    private void ComputeGlint(
        CephalonMesh mesh, CephalonParameters p, double gx, double gy, double eyeScale,
        double centerX, double centerY, double focal)
    {
        // Pupil apex is the specular anchor; project it through the camera.
        MeshPart pupil = mesh.Parts[PupilPartIndex(mesh)];
        int apexIdx = pupil.BaseVerts.Length - 1;
        V3 apex = pupil.WorldVerts[apexIdx];
        double vz = Cephalon3D.CameraDist - apex.Z;
        if (vz < MinViewZ)
        {
            _frame.GlintVisible = false;
            return;
        }

        _frame.GlintX = centerX + (focal * apex.X / vz);
        _frame.GlintY = centerY - (focal * apex.Y / vz);
        double pupilRadiusUnits = Cephalon3D.EyeRadius * eyeScale
            * Cephalon3D.PupilRadiusFrac
            * San(p.PupilScale, CephalonParameters.PupilScaleMin, CephalonParameters.PupilScaleMax);
        _frame.GlintRadius = focal * pupilRadiusUnits / vz;

        // The shutter must uncover the pupil before a specular makes sense.
        _frame.GlintVisible = San(p.EyeOpen, 0.0, 1.0) > 0.35;
    }

    private void EnsureScratchCapacity(int faceCount)
    {
        if (_scratch.Length < faceCount)
        {
            int grow = Math.Max(faceCount, _scratch.Length * 2);
            _scratch = new SceneFace[grow];
            _frame.Faces = new SceneFace[grow];
        }
    }

    private static int PupilPartIndex(CephalonMesh mesh)
    {
        for (int i = 0; i < mesh.Parts.Length; i++)
        {
            if (mesh.Parts[i].Kind == PartKind.Pupil)
            {
                return i;
            }
        }
        return 0; // unreachable: the builder always emits a pupil part
    }

    private static double WorldToDepth01(double worldZ) =>
        Math.Clamp((worldZ - Cephalon3D.ZRangeMin) / (Cephalon3D.ZRangeMax - Cephalon3D.ZRangeMin), 0.0, 1.0);

    private static double San(double value, double min, double max)
    {
        if (!double.IsFinite(value))
        {
            return (min + max) * 0.5;
        }
        return Math.Clamp(value, min, max);
    }

    // ---------- deterministic mote cloud ----------

    private readonly struct MoteSeed
    {
        public MoteSeed(double angle, double radius, double speed, double size, double phase, double incline)
        {
            Angle = angle;
            Radius = radius;
            Speed = speed;
            Size = size;
            Phase = phase;
            Incline = incline;
        }

        public double Angle { get; }
        public double Radius { get; }
        public double Speed { get; }
        public double Size { get; }
        public double Phase { get; }

        /// <summary>Orbit plane inclination (radians) — the mote cloud fills real depth.</summary>
        public double Incline { get; }
    }

    private static MoteSeed[] CreateMotes(int count, ulong seed)
    {
        var rand = new XorShiftRandom(seed);
        var motes = new MoteSeed[count];
        for (int i = 0; i < count; i++)
        {
            double radius = rand.Next(Cephalon3D.MoteRadiusMin, Cephalon3D.MoteRadiusMax);
            motes[i] = new MoteSeed(
                angle: (Math.PI * 2.0 * i / count) + rand.Next(-0.2, 0.2),
                radius: radius,
                speed: rand.Next(0.12, 0.5) * (i % 2 == 0 ? 1 : -1),
                size: rand.Next(1.0, 2.4),
                phase: rand.Next(0.0, Math.PI * 2.0),
                incline: rand.Next(-0.7, 0.7));
        }
        return motes;
    }
}
