using System.Globalization;
using System.Text;
using GeometryEngine.Internal.Csg;

namespace GeometryEngine.MeshIO;

/// <summary>Errors the mesh I/O slices report.</summary>
internal static class MeshIOErrors
{
    public static readonly Error FileExists = new(
        "MeshIO.FileExists",
        "The target file already exists. Pass overwrite: true to replace it.");

    public static readonly Error FileNotFound = new("MeshIO.FileNotFound", "No file at that path.");

    public static Error Unreadable(string description) => new("MeshIO.Unreadable", description);

    public static Error Unwritable(string description) => new("MeshIO.Unwritable", description);
}

/// <summary>Ask for a mesh to be written to disk as binary STL.</summary>
public sealed record ExportRequest(IMesh Mesh, string FilePath, bool Overwrite);

/// <summary>
/// Binary STL: an 80 byte header, a triangle count, then 50 bytes per facet.
/// STL has no notion of shared vertices, so export deliberately throws the index
/// buffer away and import welds the mesh back together.
/// </summary>
internal sealed class ExportHandler
{
    public Result Handle(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return Result.Failure(MeshErrors.EmptyOperand);
        }

        if (File.Exists(request.FilePath) && !request.Overwrite)
        {
            return Result.Failure(MeshIOErrors.FileExists);
        }

        try
        {
            using var stream = File.Create(request.FilePath);
            using var writer = new BinaryWriter(stream, Encoding.ASCII);

            var header = new byte[80];
            var label = Encoding.ASCII.GetBytes($"GeometryEngine {request.Mesh.Metadata.Name}");
            Array.Copy(label, header, Math.Min(label.Length, 79));

            writer.Write(header);
            writer.Write((uint)request.Mesh.TriangleCount);

            for (var t = 0; t < request.Mesh.TriangleCount; t++)
            {
                var (a, b, c) = request.Mesh.TriangleAt(t);
                var normal = Direction.From((b - a).Cross(c - a)).GetValueOrDefault(Direction.Z).Vector;

                WriteVector(writer, normal);
                WriteVector(writer, a);
                WriteVector(writer, b);
                WriteVector(writer, c);
                writer.Write((ushort)0);
            }

            return Result.Success();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or ArgumentException or NotSupportedException)
        {
            // A malformed path surfaces as ArgumentException/NotSupportedException; a
            // locked or unwritable target as IOException/UnauthorizedAccessException.
            return Result.Failure(MeshIOErrors.Unwritable(failure.Message));
        }
    }

    private static void WriteVector(BinaryWriter writer, Vec3 vector)
    {
        writer.Write((float)vector.X);
        writer.Write((float)vector.Y);
        writer.Write((float)vector.Z);
    }
}

/// <summary>Ask for a mesh to be read from a binary STL file.</summary>
public sealed record ImportRequest(string FilePath);

internal sealed class ImportHandler(Tolerance tolerance)
{
    private const int HeaderBytes = 80;
    private const int FacetBytes = 50;

    private readonly Tolerance _tolerance = tolerance;

    public Result<IMesh> Handle(ImportRequest request)
    {
        if (!File.Exists(request.FilePath))
        {
            return MeshIOErrors.FileNotFound;
        }

        try
        {
            var bytes = File.ReadAllBytes(request.FilePath);
            var name = Path.GetFileNameWithoutExtension(request.FilePath);

            // Binary and ASCII STL are told apart by arithmetic, not by the leading
            // word: a binary file's size is exactly the header, the count and one
            // record per facet, whereas an ASCII file's is not. This is more reliable
            // than sniffing for "solid", which binary writers also emit in the header.
            return IsBinary(bytes)
                ? ParseBinary(bytes, name)
                : ParseAscii(bytes, name);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or ArgumentException or NotSupportedException)
        {
            // Mirror the export side: a locked or unreadable file, or a malformed path,
            // becomes a domain failure rather than escaping as an exception.
            return MeshIOErrors.Unreadable(failure.Message);
        }
    }

    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length < HeaderBytes + 4)
        {
            return false;
        }

        var count = BitConverter.ToUInt32(bytes, HeaderBytes);
        return (long)HeaderBytes + 4 + ((long)count * FacetBytes) == bytes.Length;
    }

    private Result<IMesh> ParseBinary(byte[] bytes, string name)
    {
        var count = BitConverter.ToUInt32(bytes, HeaderBytes);

        // STL repeats every position once per facet, so weld on the way in.
        var welder = new VertexWelder(_tolerance.Value);
        var triangles = ImmutableArray.CreateBuilder<int>((int)count * 3);

        for (var facet = 0; facet < count; facet++)
        {
            var offset = HeaderBytes + 4 + (facet * FacetBytes) + 12;
            AddFacet(welder, triangles, ReadVector(bytes, offset), ReadVector(bytes, offset + 12), ReadVector(bytes, offset + 24));
        }

        return ImmutableMesh.Create([.. welder.Vertices], triangles.ToImmutable(), new MeshMetadata(name, "GeometryEngine.MeshIO"));
    }

    /// <summary>
    /// Parses ASCII STL. The grammar is verbose - solid/facet/outer loop/vertex/endloop
    /// /endfacet/endsolid - but only the vertex lines carry geometry, so the parser
    /// walks the whitespace-separated tokens and reads three coordinates after each
    /// "vertex" keyword, grouping every three vertices into a facet. Normals are
    /// ignored, as they are for binary, since export recomputes them.
    /// </summary>
    private Result<IMesh> ParseAscii(byte[] bytes, string name)
    {
        // Latin-1 maps every byte, so a stray non-ASCII byte in a comment cannot throw.
        var tokens = Encoding.Latin1.GetString(bytes)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var welder = new VertexWelder(_tolerance.Value);
        var triangles = ImmutableArray.CreateBuilder<int>();

        Span<Vec3> facet = stackalloc Vec3[3];
        var corner = 0;

        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].Equals("vertex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 3 >= tokens.Length ||
                !TryParse(tokens[i + 1], out var x) ||
                !TryParse(tokens[i + 2], out var y) ||
                !TryParse(tokens[i + 3], out var z))
            {
                return MeshIOErrors.Unreadable("An ASCII STL vertex line did not carry three numbers.");
            }

            facet[corner++] = new Vec3(x, y, z);
            i += 3;

            if (corner == 3)
            {
                AddFacet(welder, triangles, facet[0], facet[1], facet[2]);
                corner = 0;
            }
        }

        if (triangles.Count == 0)
        {
            return MeshIOErrors.Unreadable("No triangles were found; the file is not a recognisable STL.");
        }

        return ImmutableMesh.Create([.. welder.Vertices], triangles.ToImmutable(), new MeshMetadata(name, "GeometryEngine.MeshIO"));
    }

    private static bool TryParse(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static void AddFacet(VertexWelder welder, ImmutableArray<int>.Builder triangles, Vec3 a, Vec3 b, Vec3 c)
    {
        var ia = welder.AddOrGet(a);
        var ib = welder.AddOrGet(b);
        var ic = welder.AddOrGet(c);

        if (ia == ib || ib == ic || ic == ia)
        {
            return;
        }

        triangles.Add(ia);
        triangles.Add(ib);
        triangles.Add(ic);
    }

    private static Vec3 ReadVector(byte[] bytes, int offset) => new(
        BitConverter.ToSingle(bytes, offset),
        BitConverter.ToSingle(bytes, offset + 4),
        BitConverter.ToSingle(bytes, offset + 8));
}

/// <summary>The <see cref="IGeometryIO"/> facade over the I/O slices.</summary>
internal sealed class GeometryIO(Tolerance tolerance) : IGeometryIO
{
    private readonly ExportHandler _export = new();
    private readonly ImportHandler _import = new(tolerance);

    public Result Export(IMesh mesh, string filePath, bool overwrite = false) =>
        _export.Handle(new ExportRequest(mesh, filePath, overwrite));

    public Result<IMesh> Import(string filePath) => _import.Handle(new ImportRequest(filePath));
}
