using System.Text.Json;
using TOLIK.Desktop.Performance;
using TOLIK.Desktop.Render3D;

namespace TOLIK.Desktop.Export;

/// <summary>
/// The Cephalon design source export (docs/VR.md + docs/android-app.md — ONE DESIGN
/// SOURCE): the parametric mesh (LOD parameters + per-part topology and placement)
/// serialized to a JSON contract any renderer can consume. The design source is the
/// PARAMETERS — the same values regenerate the same body on every platform; never
/// baked renderer-specific vertices.
/// </summary>
public static class CephalonDesignSourceExporter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Export(RenderQuality quality)
    {
        var mesh = CephalonMeshBuilder.Build(Cephalon3D.Lod(quality));

        return JsonSerializer.Serialize(new
        {
            designSource = "cephalon-3d-v1",
            lod = new
            {
                mesh.Lod.SphereStacks,
                mesh.Lod.SphereSlices,
                mesh.Lod.OuterSegments,
                mesh.Lod.OuterArcQuads,
                mesh.Lod.OuterCross,
                mesh.Lod.MidSegments,
                mesh.Lod.MidArcQuads,
                mesh.Lod.MidCross,
                mesh.Lod.AnchorArcs,
                mesh.Lod.AnchorArcQuads,
                mesh.Lod.AnchorCross,
                mesh.Lod.LidSlices,
                mesh.Lod.LidBands,
                mesh.Lod.DepthSlices
            },
            faceCount = mesh.FaceCount,
            parts = mesh.Parts.Select(p => new
            {
                kind = p.Kind.ToString(),
                material = p.Material.ToString(),
                p.TiltX,
                p.TiltY,
                p.ZBase,
                p.Radius,
                p.DynamicVerts,
                faceCount = p.FaceCount,
                // Part-local vertices, flattened as x,y,z triplets (body-radius units).
                baseVerts = Flatten(p.BaseVerts),
                basePolar = p.BasePolar,
                // Triangles as vertex-index triplets into baseVerts.
                faces = p.Faces
            })
        }, Options);
    }

    private static double[] Flatten(V3[] verts)
    {
        var flat = new double[verts.Length * 3];
        for (var i = 0; i < verts.Length; i++)
        {
            flat[i * 3] = verts[i].X;
            flat[i * 3 + 1] = verts[i].Y;
            flat[i * 3 + 2] = verts[i].Z;
        }

        return flat;
    }
}
