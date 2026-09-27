// Verbatim copies of the code Fabolus main runs when smoothing, lifted from nsmela/Fabolus@main
// so the reference side of the comparison is main's pipeline and not a paraphrase of it.
//
//   Fabolus.Core/Extensions/MRMeshExtensions.cs   ToDMesh
//   Fabolus.Core/Extensions/g3Extensions.cs       ToMesh
//   Fabolus.Core/Meshes/MeshTools/OffsetMesh.cs   OffsetMesh
//   Fabolus.Core/Meshes/MeshTools/ResizeMesh.cs   Resize
//   Fabolus.Core/Meshes/MeshTools/CentreMesh.cs   OrientationCentre
//   Fabolus.Core/Smoothing/MarchingCubesSmoothing.cs
//
// Only two things are changed, and neither touches the geometry: the MeshModel/Bolus wrappers
// are collapsed into the (DMesh3, Mesh) pair they hold, because MeshModel lives in a WPF
// assembly that will not build off Windows; and OrientationCentre returns the pair rather than
// mutating a class field.

using g3;
using static MR.DotNet;

namespace Fabolus.SmoothingCompare.Main;

internal static class FabolusMain
{
    // ---- Fabolus.Core/Extensions/MRMeshExtensions.cs ------------------------------------

    public static DMesh3 ToDMesh(this Mesh mesh)
    {
        DMesh3 result = new();

        foreach (var p in mesh.Points)
        {
            result.AppendVertex(new Vector3d(p.X, p.Y, p.Z));
        }
        foreach (var t in mesh.Triangulation)
        {
            result.AppendTriangle(t.v0.Id, t.v1.Id, t.v2.Id);
        }

        return result;
    }

    // ---- Fabolus.Core/Extensions/g3Extensions.cs ---------------------------------------

    public static Mesh ToMesh(this DMesh3 mesh)
    {
        List<MR.DotNet.Vector3f> verts = mesh.Vertices().Select(v => new MR.DotNet.Vector3f((float)v.x, (float)v.y, (float)v.z)).ToList();
        List<ThreeVertIds> tris = mesh.Triangles().Select(t => new ThreeVertIds(t.a, t.b, t.c)).ToList();
        return Mesh.FromTriangles(verts, tris);
    }

    // ---- Fabolus.Core/Meshes/MeshTools/OffsetMesh.cs ------------------------------------

    public static Mesh OffsetMesh(Mesh mesh, float offsetDistance, float cellSize = 0.0f)
    {
        MeshPart mp = new(mesh);

        OffsetParameters parms = new()
        {
            voxelSize = cellSize > 0 ? cellSize : Offset.SuggestVoxelSize(mp, 1e6f),
        };

        var result = Offset.OffsetMesh(mp, offsetDistance, parms);

        return result;
    }

    /// <summary>The voxel size main's un-parameterised inflation offset ends up using.</summary>
    public static float SuggestedVoxelSize(Mesh mesh) => Offset.SuggestVoxelSize(new MeshPart(mesh), 1e6f);

    // ---- Fabolus.Core/Meshes/MeshTools/ResizeMesh.cs ------------------------------------

    public static Mesh Resize(Mesh meshModel, int targetTriangleCount)
    {
        if (meshModel is null || meshModel.ValidFaces.Count() == 0 || meshModel.ValidFaces.Count() <= targetTriangleCount)
        { return meshModel; }

        // reduce mesh size
        return Resize(meshModel.ToDMesh(), targetTriangleCount).ToMesh();
    }

    public static DMesh3 Resize(DMesh3 mesh, int targetTriangleCount)
    {
        if (mesh is null || mesh.VertexCount == 0 || mesh.TriangleCount == 0 || mesh.TriangleCount <= targetTriangleCount) { return mesh; }

        // reduce mesh size
        DMeshAABBTree3 tree = new(new DMesh3(mesh), true);
        MeshProjectionTarget target = new()
        {
            Mesh = tree.Mesh,
            Spatial = tree,
        };

        Reducer reducer = new(mesh);
        reducer.SetProjectionTarget(target);
        reducer.ReduceToTriangleCount(targetTriangleCount);
        mesh.CompactInPlace(); //reorganize the triangles and verts

        return mesh;
    }

    // ---- Fabolus.Core/Meshes/MeshTools/CentreMesh.cs ------------------------------------
    //
    // MeshModel holds both representations: Mesh (the g3 DMesh3) and _mesh (the MeshLib Mesh).
    // OrientationCentre reads the bounding box off the MeshLib one, translates the DMesh3, then
    // rebuilds the MeshLib one from it. BolusStore calls it twice on import; the second call
    // measures an already-centred box and so translates by nothing.

    public static (DMesh3 Dmesh, Mesh MrMesh) OrientationCentre(DMesh3 dmesh, Mesh mrMesh)
    {
        float x = mrMesh.BoundingBox.Center().X;
        float y = mrMesh.BoundingBox.Center().Y;
        float z = mrMesh.BoundingBox.Center().Z;

        MeshTransforms.Translate(dmesh, -new Vector3d(x, y, z));
        return (dmesh, dmesh.ToMesh());
    }

    // ---- Fabolus.Core/Smoothing/MarchingCubesSmoothing.cs ------------------------------

    public static Mesh Smooth(Mesh model, float deflateDistance, float inflateDistance, int iterations, float cellSize,
        out double offsetCycleMs, out double inflateMs, out double resizeMs, out float inflationVoxelSize)
    {
        int triangleCount = model.ValidFaces.Count();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (deflateDistance > 0)
        {
            for (int i = 0; i < iterations; i++)
            {
                model = OffsetMesh(model, deflateDistance, cellSize);
                model = OffsetMesh(model, -deflateDistance, cellSize);
            }
        }
        offsetCycleMs = watch.Elapsed.TotalMilliseconds;

        inflationVoxelSize = SuggestedVoxelSize(model);

        watch.Restart();
        Mesh smoothed = OffsetMesh(model, inflateDistance);
        inflateMs = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        smoothed = Resize(smoothed, triangleCount * 2);
        resizeMs = watch.Elapsed.TotalMilliseconds;

        return smoothed;
    }
}
