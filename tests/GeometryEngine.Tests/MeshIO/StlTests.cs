using System.Text;

namespace GeometryEngine.Tests.MeshIO;

[Suite("Mesh IO / STL")]
public sealed class StlTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"meshcsg-{Guid.NewGuid():N}.stl");

    [Fact]
    public void A_mesh_survives_a_round_trip_through_a_file()
    {
        var path = TempPath();
        var original = Fixtures.Sphere(new Vec3(1, 2, 3), 2, 16);

        try
        {
            Check.True(Fixtures.Engine.IO.Export(original, path).IsSuccess);

            var reloaded = Fixtures.Engine.IO.Import(path);

            Check.True(reloaded.IsSuccess);
            Check.Equal(original.TriangleCount, reloaded.Value.TriangleCount);
            Check.RelativelyClose(Fixtures.VolumeOf(original), Fixtures.VolumeOf(reloaded.Value), 1e-5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Exporting_over_an_existing_file_needs_permission()
    {
        var path = TempPath();

        try
        {
            Check.True(Fixtures.Engine.IO.Export(Fixtures.UnitCube(), path).IsSuccess);

            var blocked = Fixtures.Engine.IO.Export(Fixtures.UnitCube(), path);

            Check.True(blocked.IsFailure);
            Check.Equal("MeshIO.FileExists", blocked.Error.Code);
            Check.True(Fixtures.Engine.IO.Export(Fixtures.UnitCube(), path, overwrite: true).IsSuccess);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Importing_a_file_that_is_not_there_is_refused()
    {
        var missing = Fixtures.Engine.IO.Import(Path.Combine(Path.GetTempPath(), "definitely-not-here.stl"));

        Check.True(missing.IsFailure);
        Check.Equal("MeshIO.FileNotFound", missing.Error.Code);
    }

    [Fact]
    public void An_ascii_stl_file_imports_and_welds_into_a_solid()
    {
        var path = TempPath();

        // A unit tetrahedron in ASCII STL: four facets, each vertex repeated across the
        // facets that share it, so a correct importer welds it to four vertices.
        var ascii = """
            solid tetra
             facet normal 0 0 0
              outer loop
               vertex 0 0 0
               vertex 1 0 0
               vertex 0 1 0
              endloop
             endfacet
             facet normal 0 0 0
              outer loop
               vertex 0 0 0
               vertex 0 1 0
               vertex 0 0 1
              endloop
             endfacet
             facet normal 0 0 0
              outer loop
               vertex 0 0 0
               vertex 0 0 1
               vertex 1 0 0
              endloop
             endfacet
             facet normal 0 0 0
              outer loop
               vertex 1 0 0
               vertex 0 0 1
               vertex 0 1 0
              endloop
             endfacet
            endsolid tetra
            """;

        try
        {
            File.WriteAllText(path, ascii);

            var result = Fixtures.Engine.IO.Import(path);

            Check.True(result.IsSuccess);
            Check.Equal(4, result.Value.TriangleCount);
            Check.Equal(4, result.Value.VertexCount);
            Check.True(Fixtures.TopologyOf(result.Value).IsWatertight);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_binary_header_beginning_with_solid_is_still_read_as_binary()
    {
        // Binary writers stamp arbitrary text into the 80-byte header, sometimes the
        // word "solid" - which would fool a parser that sniffed for it. Detection must
        // go by the size arithmetic instead. Hand-build a one-facet binary file whose
        // header starts with "solid" and confirm it reads back as one triangle.
        var path = TempPath();
        var file = new byte[80 + 4 + 50];
        Encoding.ASCII.GetBytes("solid not-really-ascii").CopyTo(file, 0);
        BitConverter.GetBytes((uint)1).CopyTo(file, 80);

        var coords = new float[] { 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0 }; // normal, then three vertices
        for (var i = 0; i < coords.Length; i++)
        {
            BitConverter.GetBytes(coords[i]).CopyTo(file, 84 + (i * 4));
        }

        try
        {
            File.WriteAllBytes(path, file);

            var reloaded = Fixtures.Engine.IO.Import(path);

            Check.True(reloaded.IsSuccess);
            Check.Equal(1, reloaded.Value.TriangleCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Importing_a_corrupt_file_is_a_failure_rather_than_a_throw()
    {
        var path = TempPath();

        try
        {
            // A byte or two of junk: present, but far too short to be a real STL.
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

            var result = Fixtures.Engine.IO.Import(path);

            Check.True(result.IsFailure);
            Check.Equal("MeshIO.Unreadable", result.Error.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Exporting_into_a_missing_directory_is_a_failure_rather_than_a_throw()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meshcsg-{Guid.NewGuid():N}", "nested", "cube.stl");

        var result = Fixtures.Engine.IO.Export(Fixtures.UnitCube(), path);

        Check.True(result.IsFailure);
        Check.Equal("MeshIO.Unwritable", result.Error.Code);
    }

    [Fact]
    public void An_imported_mesh_is_welded_back_into_shared_vertices()
    {
        var path = TempPath();
        var cube = Fixtures.UnitCube();

        try
        {
            _ = Fixtures.Engine.IO.Export(cube, path);
            var reloaded = Fixtures.Engine.IO.Import(path).Value;

            Check.Equal(8, reloaded.VertexCount);
            Check.True(Fixtures.TopologyOf(reloaded).IsWatertight);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
