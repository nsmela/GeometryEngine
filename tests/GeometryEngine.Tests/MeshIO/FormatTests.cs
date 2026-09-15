using System.Text;

namespace GeometryEngine.Tests.MeshIO;

[Suite("Mesh IO / OBJ, OFF, PLY, 3MF")]
public sealed class FormatTests
{
    private static readonly PackageVendor Vendor = new("fab", "http://fabolus.io/2026/metadata", "basemesh");

    private static string TempPath(string extension) => Path.Combine(Path.GetTempPath(), $"meshcsg-{Guid.NewGuid():N}{extension}");

    [Fact]
    public void A_mesh_survives_a_round_trip_through_every_format()
    {
        var original = Fixtures.Sphere(new Vec3(1, 2, 3), 2.5, 24);

        foreach (var extension in new[] { ".stl", ".obj", ".off", ".ply", ".3mf" })
        {
            var path = TempPath(extension);
            try
            {
                Check.True(Fixtures.Engine.IO.Export(original, path).IsSuccess, extension);

                var reloaded = Fixtures.Engine.IO.Import(path).Value;

                Check.Equal(original.TriangleCount, reloaded.TriangleCount);
                Check.Equal(original.VertexCount, reloaded.VertexCount);
                Check.RelativelyClose(Fixtures.VolumeOf(original), Fixtures.VolumeOf(reloaded), 1e-6);
                Check.True(Fixtures.TopologyOf(reloaded).IsWatertight);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Double_precision_formats_keep_coordinates_exactly()
    {
        var original = Fixtures.Sphere(new Vec3(0.1, 0.2, 0.3), 1.0 / 3.0, 12);

        foreach (var format in new[] { MeshFileFormat.Obj, MeshFileFormat.Off, MeshFileFormat.Ply, MeshFileFormat.ThreeMf })
        {
            var bytes = Fixtures.Engine.IO.Write(original, format).Value;
            var reloaded = Fixtures.Engine.IO.Read(bytes, format, "exact").Value;

            // Reading compacts vertices into first-use order, so compare corner by corner.
            for (var t = 0; t < original.TriangleCount; t++)
            {
                Check.Equal(original.TriangleAt(t), reloaded.TriangleAt(t));
            }
        }
    }

    [Fact]
    public void An_obj_with_polygon_faces_texture_indices_and_negative_indices_reads()
    {
        var obj = """
            # a unit square split two ways
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            vt 0 0
            f 1/1 2/1 3/1 4/1
            f -1 -2 -3
            """;

        var mesh = Fixtures.Engine.IO.Read(Encoding.UTF8.GetBytes(obj), MeshFileFormat.Obj, "square").Value;

        Check.Equal(3, mesh.TriangleCount);
        Check.Equal(4, mesh.VertexCount);
    }

    [Fact]
    public void An_ascii_ply_carrying_normals_reads_its_positions_by_name()
    {
        var ply = """
            ply
            format ascii 1.0
            element vertex 3
            property float nx
            property float ny
            property float nz
            property float x
            property float y
            property float z
            element face 1
            property list uchar int vertex_indices
            end_header
            0 0 1 0 0 0
            0 0 1 1 0 0
            0 0 1 0 1 0
            3 0 1 2
            """;

        var mesh = Fixtures.Engine.IO.Read(Encoding.ASCII.GetBytes(ply), MeshFileFormat.Ply, "tri").Value;

        Check.Equal(new Vec3(1, 0, 0), mesh.Vertices[1]);
        Check.Equal(1, mesh.TriangleCount);
    }

    [Fact]
    public void A_face_pointing_at_a_missing_vertex_is_unreadable_rather_than_a_throw()
    {
        var result = Fixtures.Engine.IO.Read(Encoding.UTF8.GetBytes("v 0 0 0\nf 1 2 3\n"), MeshFileFormat.Obj, "broken");

        Check.True(result.IsFailure);
        Check.Equal("MeshIO.Unreadable", result.Error.Code);
    }

    [Fact]
    public void Garbage_in_every_format_is_unreadable_rather_than_a_throw()
    {
        var junk = Encoding.UTF8.GetBytes("this is not a mesh 1 2 x");

        foreach (var format in Enum.GetValues<MeshFileFormat>())
        {
            var result = Fixtures.Engine.IO.Read(junk, format, "junk");

            Check.True(result.IsFailure, format.ToString());
            Check.Equal("MeshIO.Unreadable", result.Error.Code);
        }
    }

    [Fact]
    public void An_unknown_extension_is_refused()
    {
        Check.Equal("MeshIO.UnsupportedFormat", Fixtures.Engine.IO.Import("model.dae").Error.Code);
        Check.Equal("MeshIO.UnsupportedFormat", Fixtures.Engine.IO.Export(Fixtures.UnitCube(), TempPath(".dae")).Error.Code);
    }

    [Fact]
    public void A_package_round_trips_its_reference_mesh_and_metadata()
    {
        var model = Fixtures.Sphere(Vec3.Zero, 5, 24);
        var reference = Fixtures.Cube(-2, 4);
        var metadata = ImmutableDictionary<string, string>.Empty.Add("fab:Commands", "[{\"Type\":\"Rotate\"}]");

        var bytes = Fixtures.Engine.IO.WritePackage(new MeshPackage(model, Maybe<IMesh>.Some(reference), metadata, Vendor)).Value;
        var package = Fixtures.Engine.IO.ReadPackage(bytes, "package", Vendor).Value;

        Check.Equal(model.TriangleCount, package.Model.TriangleCount);
        Check.True(package.Reference.HasValue);
        Check.Equal(12, package.Reference.Value.TriangleCount);
        Check.Equal("[{\"Type\":\"Rotate\"}]", package.Metadata["fab:Commands"]);
    }

    [Fact]
    public void Importing_a_package_as_a_mesh_yields_its_model_not_its_reference()
    {
        var bytes = Fixtures.Engine.IO.WritePackage(new MeshPackage(
            Fixtures.Sphere(Vec3.Zero, 5, 24),
            Maybe<IMesh>.Some(Fixtures.UnitCube()),
            ImmutableDictionary<string, string>.Empty,
            Vendor)).Value;

        var mesh = Fixtures.Engine.IO.Read(bytes, MeshFileFormat.ThreeMf, "model").Value;

        Check.Greater(mesh.TriangleCount, 12);
    }

    [Fact]
    public void A_package_written_by_the_meshlib_era_fabolus_still_reads()
    {
        // Written by Fabolus through MeshLib's 3MF writer, before this library existed: arbitrary
        // object ids, a build item transform, and the command history as vendor metadata.
        var bytes = File.ReadAllBytes(Assets.BenchFile("chin_legacy_smooth.3mf"));

        var package = Fixtures.Engine.IO.ReadPackage(bytes, "chin", Vendor).Value;

        Check.Greater(package.Model.TriangleCount, 1000);
        Check.True(package.Metadata.ContainsKey("fab:Commands"));
        Check.True(package.Metadata["fab:Commands"].StartsWith("[{\"Type\":\"RotateCommand\"", StringComparison.Ordinal));
    }
}
