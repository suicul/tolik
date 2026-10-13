namespace TOLIK.Desktop.Render3D;

/// <summary>
/// One procedural body part: static topology (base vertices + faces) plus the
/// placement data the pipeline needs to animate it. Vertices are generated in
/// part-local space (rings in the XY plane, eye parts on the sphere around the
/// eye center); the pipeline applies spin, tilt, depth and the body tilt.
/// Framework-free and transferable to VR/Android (docs/VR.md).
/// </summary>
public sealed class MeshPart
{
    public PartKind Kind { get; }

    public MaterialKind Material { get; }

    /// <summary>Part-local vertices (dynamic parts: sized scratch, regenerated per frame).</summary>
    public V3[] BaseVerts { get; }

    /// <summary>Base polar angle per vertex (harmonic wobble input); null if the part never wobbles.</summary>
    public double[]? BasePolar { get; }

    /// <summary>Triangles as vertex-index triplets, wound so outward faces front the camera plane.</summary>
    public int[] Faces { get; }

    /// <summary>Fixed part-plane tilt (radians) — each ring leans in its own plane.</summary>
    public double TiltX { get; }

    public double TiltY { get; }

    /// <summary>Depth offset along the view axis (body-radius units).</summary>
    public double ZBase { get; }

    /// <summary>Reference radius in body units (bounding/tests).</summary>
    public double Radius { get; }

    /// <summary>True when the pipeline must regenerate vertices every frame
    /// (pupil follows dilation; lids follow the shutter travel).</summary>
    public bool DynamicVerts { get; }

    /// <summary>Scratch destination of the per-frame transform (world space).</summary>
    public V3[] WorldVerts { get; }

    /// <summary>Grid layout for dynamic sphere bands (lids): row/col counts of the vertex array.</summary>
    internal int GridRows { get; set; }

    internal int GridCols { get; set; }

    internal MeshPart(
        PartKind kind,
        MaterialKind material,
        V3[] baseVerts,
        double[]? basePolar,
        int[] faces,
        double tiltX,
        double tiltY,
        double zBase,
        double radius,
        bool dynamicVerts)
    {
        Kind = kind;
        Material = material;
        BaseVerts = baseVerts;
        BasePolar = basePolar;
        Faces = faces;
        TiltX = tiltX;
        TiltY = tiltY;
        ZBase = zBase;
        Radius = radius;
        DynamicVerts = dynamicVerts;
        WorldVerts = new V3[baseVerts.Length];
    }

    public int FaceCount => Faces.Length / 3;
}

/// <summary>
/// The full Cephalon body mesh at one LOD: the far hull band, the mid chassis
/// arcs, the outer anchor arcs, the eye sphere, the facet iris, the pupil cap
/// and the two shutter lids. Built once per quality tier, reused every frame.
/// </summary>
public sealed class CephalonMesh
{
    public MeshLod Lod { get; }

    public MeshPart[] Parts { get; }

    /// <summary>Total triangle count across all parts (the CPU budget metric).</summary>
    public int FaceCount { get; }

    internal CephalonMesh(MeshLod lod, MeshPart[] parts)
    {
        Lod = lod;
        Parts = parts;
        int total = 0;
        foreach (MeshPart part in parts)
        {
            total += part.FaceCount;
        }
        FaceCount = total;
    }
}

/// <summary>
/// Procedural mesh generation — every vertex is placed from the parameters of
/// the part (radius, width, camber, counts), never from baked data. Pure,
/// deterministic, framework-free: this file is the 3D design source.
///
/// Winding convention (pinned by pipeline tests): a triangle whose projected
/// screen area is clockwise (negative signed area after the Y flip) faces the
/// camera; the painter culls everything else.
/// </summary>
public static class CephalonMeshBuilder
{
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>Builds the whole body at one LOD.</summary>
    public static CephalonMesh Build(MeshLod lod)
    {
        ArgumentNullException.ThrowIfNull(lod);

        var parts = new MeshPart[]
        {
            // The layered shell — the 2D layers became 3D arc bands at real
            // depths, each tilted in its own plane, spinning around its axis.
            BuildRingBand(
                PartKind.RingOuter, MaterialKind.ShellOuter,
                Cephalon3D.OuterRadius, Cephalon3D.OuterWidth, Cephalon3D.OuterCamber,
                segments: lod.OuterSegments, spanDeg: 40.0, gapDeg: 20.0,
                arcQuads: lod.OuterArcQuads, crossSteps: lod.OuterCross,
                tiltX: Cephalon3D.OuterTiltX, tiltY: Cephalon3D.OuterTiltY,
                zBase: Cephalon3D.ZOuter, wobbles: true),

            BuildRingBand(
                PartKind.RingMiddle, MaterialKind.ShellMiddle,
                Cephalon3D.MidRadius, Cephalon3D.MidWidth, Cephalon3D.MidCamber,
                segments: lod.MidSegments, spanDeg: 78.0, gapDeg: 42.0,
                arcQuads: lod.MidArcQuads, crossSteps: lod.MidCross,
                tiltX: Cephalon3D.MidTiltX, tiltY: Cephalon3D.MidTiltY,
                zBase: Cephalon3D.ZMiddle, wobbles: true),

            // The bracket replacement: 2–3 wide arcs of the OUTERMOST ring at
            // their own depth and tilt — a natural cage, never a UI frame
            // (owner verdict: "ограничивающие рамки, их прям видно").
            BuildRingBand(
                PartKind.AnchorArcs, MaterialKind.ShellAnchor,
                Cephalon3D.AnchorRadius, Cephalon3D.AnchorWidth, Cephalon3D.AnchorCamber,
                segments: lod.AnchorArcs, spanDeg: 110.0, gapDeg: 10.0,
                arcQuads: lod.AnchorArcQuads, crossSteps: lod.AnchorCross,
                tiltX: Cephalon3D.AnchorTiltX, tiltY: Cephalon3D.AnchorTiltY,
                zBase: Cephalon3D.ZAnchor, wobbles: false),

            // The eye: a real sphere.
            BuildSphereBand(
                PartKind.EyeSphere, MaterialKind.Sclera,
                radius: Cephalon3D.EyeRadius,
                thetaFromDeg: 0.0, thetaToDeg: 180.0,
                bands: lod.SphereStacks, slices: lod.SphereSlices,
                zBase: Cephalon3D.ZEye, dynamicVerts: false),

            // The facet iris — the octagonal cut-gem signature.
            BuildIris(),

            // The pupil cap (vertices regenerated per frame from the dilation).
            BuildPupil(),

            // The hemispherical shutter lids (vertices follow the aperture).
            BuildLid(PartKind.LidTop, MaterialKind.Lid, top: true, lod),
            BuildLid(PartKind.LidBottom, MaterialKind.Lid, top: false, lod),
        };

        return new CephalonMesh(lod, parts);
    }

    /// <summary>
    /// Builds a wide arc band: a fraction of a torus-like shell. The
    /// cross-section bows toward the viewer (camber), so every ring reads as a
    /// curved armor plate, not a flat ribbon. Vertex layout: row-major
    /// (arc column j = 0..arcQuads, cross step k = 0..crossSteps).
    /// </summary>
    private static MeshPart BuildRingBand(
        PartKind kind, MaterialKind material,
        double radius, double width, double camber,
        int segments, double spanDeg, double gapDeg,
        int arcQuads, int crossSteps,
        double tiltX, double tiltY, double zBase, bool wobbles)
    {
        int segs = Math.Max(1, segments);
        int quads = Math.Max(1, arcQuads);
        int cross = Math.Max(1, crossSteps);
        double rIn = Math.Max(0.01, radius - (width * 0.5));
        double rOut = rIn + Math.Max(0.01, width);

        int cols = quads + 1;
        int rows = cross + 1;
        int vertsPerSeg = cols * rows;
        int faceCount = segs * quads * cross * 2;

        var verts = new V3[segs * vertsPerSeg];
        var polar = wobbles ? new double[verts.Length] : null;
        var faces = new int[faceCount * 3];

        double span = spanDeg * DegToRad;
        double step = (spanDeg + gapDeg) * DegToRad;

        int vi = 0;
        for (int s = 0; s < segs; s++)
        {
            double segStart = s * step;
            for (int j = 0; j < cols; j++)
            {
                double a = segStart + (span * j / quads);
                double ca = Math.Cos(a), sa = Math.Sin(a);
                for (int k = 0; k < rows; k++)
                {
                    double r = rIn + ((rOut - rIn) * k / cross);
                    double z = camber * Math.Sin(Math.PI * k / cross);
                    verts[vi] = new V3(r * ca, r * sa, z);
                    if (polar is not null)
                    {
                        polar[vi] = a;
                    }
                    vi++;
                }
            }
        }

        int fi = 0;
        for (int s = 0; s < segs; s++)
        {
            int baseIdx = s * vertsPerSeg;
            for (int j = 0; j < quads; j++)
            {
                for (int k = 0; k < cross; k++)
                {
                    int a = baseIdx + (j * rows) + k;
                    int b = baseIdx + ((j + 1) * rows) + k;
                    int c = baseIdx + ((j + 1) * rows) + k + 1;
                    int d = baseIdx + (j * rows) + k + 1;
                    AddQuad(faces, ref fi, a, b, c, d);
                }
            }
        }

        return new MeshPart(kind, material, verts, polar, faces, tiltX, tiltY, zBase, radius, dynamicVerts: false);
    }

    /// <summary>
    /// Builds a lat-long band on a sphere around the eye center (polar θ from
    /// +Y). Full range = the eye globe; partial pole ranges = the shutter lids.
    /// Vertex layout: row-major (stack row s = 0..bands, slice col i = 0..slices,
    /// last column duplicates the first to close the ring).
    /// </summary>
    private static MeshPart BuildSphereBand(
        PartKind kind, MaterialKind material,
        double radius, double thetaFromDeg, double thetaToDeg,
        int bands, int slices, double zBase, bool dynamicVerts)
    {
        int bandCount = Math.Max(1, bands);
        int sliceCount = Math.Max(3, slices);
        int rows = bandCount + 1;
        int cols = sliceCount + 1;
        int faceCount = bandCount * sliceCount * 2;

        var verts = new V3[rows * cols];
        var faces = new int[faceCount * 3];

        double t0 = thetaFromDeg * DegToRad;
        double t1 = thetaToDeg * DegToRad;

        // Static parts get their true positions now; dynamic parts (lids) get
        // scratch that the pipeline rewrites every frame (same index layout).
        if (!dynamicVerts)
        {
            for (int s = 0; s < rows; s++)
            {
                double theta = t0 + ((t1 - t0) * s / (rows - 1));
                double st = Math.Sin(theta), ct = Math.Cos(theta);
                for (int i = 0; i < cols; i++)
                {
                    double phi = Math.PI * 2.0 * i / sliceCount;
                    verts[(s * cols) + i] = new V3(
                        radius * st * Math.Cos(phi),
                        radius * ct,
                        radius * st * Math.Sin(phi));
                }
            }
        }

        int fi = 0;
        for (int s = 0; s < rows - 1; s++)
        {
            for (int i = 0; i < cols - 1; i++)
            {
                int a = (s * cols) + i;
                int b = ((s + 1) * cols) + i;
                int c = ((s + 1) * cols) + i + 1;
                int d = (s * cols) + i + 1;
                AddQuad(faces, ref fi, a, b, c, d);
            }
        }

        MeshPart part = new(kind, material, verts, basePolar: null, faces,
            tiltX: 0.0, tiltY: 0.0, zBase, radius, dynamicVerts);
        part.GridRows = rows;
        part.GridCols = cols;
        return part;
    }

    /// <summary>
    /// Builds the facet iris: the 8-corner cut-gem cap on the sphere front
    /// (cap axis +Z, facing the camera). The rim ring sits ON the globe; the
    /// inner ring and apex rise proud of it — the cut-gem table.
    /// </summary>
    private static MeshPart BuildIris()
    {
        const int n = Cephalon3D.IrisFacets;
        double eyeR = Cephalon3D.EyeRadius;
        double tRim = Math.Asin(Math.Min(0.999, Cephalon3D.IrisRadiusFrac));
        double tInner = Math.Asin(Math.Min(0.999, Cephalon3D.IrisInnerFrac));
        double lift = 1.0 + Cephalon3D.IrisLift;

        var verts = new V3[(2 * n) + 1];
        for (int k = 0; k < n; k++)
        {
            double phi = (Math.PI * 2.0 * k / n) + (Math.PI / 8.0); // flat edge at screen top, like the tray octagon
            double cp = Math.Cos(phi), sp = Math.Sin(phi);
            verts[k] = new V3(          // rim ring — on the globe
                eyeR * Math.Sin(tRim) * cp,
                eyeR * Math.Sin(tRim) * sp,
                eyeR * Math.Cos(tRim));
            verts[n + k] = new V3(      // raised gem table ring
                eyeR * lift * Math.Sin(tInner) * cp,
                eyeR * lift * Math.Sin(tInner) * sp,
                eyeR * lift * Math.Cos(tInner));
        }
        verts[2 * n] = new V3(0.0, 0.0, eyeR * (1.0 + Cephalon3D.IrisApexLift)); // apex

        // 8 facet plates + 8 apex triangles = 24 faces.
        var faces = new int[24 * 3];
        int fi = 0;
        for (int k = 0; k < n; k++)
        {
            int k1 = (k + 1) % n;
            int r0a = k, r0b = k1, r1a = n + k, r1b = n + k1;
            faces[fi++] = r0a; faces[fi++] = r0b; faces[fi++] = r1b; // facet plate 1
            faces[fi++] = r0a; faces[fi++] = r1b; faces[fi++] = r1a; // facet plate 2
            faces[fi++] = r1a; faces[fi++] = r1b; faces[fi++] = 2 * n; // apex fan
        }

        return new MeshPart(PartKind.Iris, MaterialKind.Iris, verts, basePolar: null, faces,
            tiltX: 0.0, tiltY: 0.0, Cephalon3D.ZEye, Cephalon3D.EyeRadius * Cephalon3D.IrisRadiusFrac, dynamicVerts: false);
    }

    /// <summary>Builds the pupil cap (fan around the +Z cap axis; the pipeline regenerates the ring from the dilation).</summary>
    private static MeshPart BuildPupil()
    {
        const int n = Cephalon3D.IrisFacets;
        var verts = new V3[n + 1]; // 0..n-1 ring + apex
        var faces = new int[n * 3];
        int fi = 0;
        for (int k = 0; k < n; k++)
        {
            faces[fi++] = k;
            faces[fi++] = (k + 1) % n;
            faces[fi++] = n; // apex
        }
        return new MeshPart(PartKind.Pupil, MaterialKind.Pupil, verts, basePolar: null, faces,
            tiltX: 0.0, tiltY: 0.0, Cephalon3D.ZEye, Cephalon3D.EyeRadius * Cephalon3D.PupilRadiusFrac, dynamicVerts: true);
    }

    private static MeshPart BuildLid(PartKind kind, MaterialKind material, bool top, MeshLod lod)
    {
        // Bands span the full aperture range in θ-from-pole ([0°, 94°]); the
        // pipeline rewrites the actual θ rows every frame from EyeOpen. The
        // bottom lid keeps the same orientation (its rows simply live near the
        // south pole), so one winding serves both halves; its meridian seams
        // are offset half a slice by the per-frame generator (mechanical plates).
        _ = top;
        return BuildSphereBand(
            kind, material,
            Cephalon3D.EyeRadius * Cephalon3D.LidRadiusFactor,
            thetaFromDeg: 0.0, thetaToDeg: Cephalon3D.LidClosedDeg,
            bands: lod.LidBands, slices: lod.LidSlices,
            zBase: Cephalon3D.ZEye, dynamicVerts: true);
    }

    /// <summary>
    /// Adds a quad as two front-facing triangles: (A,C,B) and (A,D,C) for
    /// A=(j,k), B=(j+1,k), C=(j+1,k+1), D=(j,k+1) — verified against the camera
    /// plane (+Z toward the viewer) by the pipeline tests.
    /// </summary>
    private static void AddQuad(int[] faces, ref int fi, int a, int b, int c, int d)
    {
        faces[fi++] = a; faces[fi++] = c; faces[fi++] = b;
        faces[fi++] = a; faces[fi++] = d; faces[fi++] = c;
    }
}
