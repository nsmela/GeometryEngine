using System.Globalization;
using System.Text;
using GeometryEngine.Internal;
using GeometryEngine.Internal.Csg;

namespace GeometryEngine.MeshIO;

/// <summary>
/// STL: an 80 byte header, a triangle count, then 50 bytes per facet - or the ASCII form. STL
/// has no notion of shared vertices, so writing throws the index buffer away and reading welds
/// the mesh back together.
/// </summary>
internal static class StlFormat
{
    private const int HeaderBytes = 80;
    private const int FacetBytes = 50;

    public static Result<IMesh> Read(ReadOnlySpan<byte> bytes, string name, Tolerance tolerance)
    {
        // Binary and ASCII are told apart by arithmetic, not by the leading word: a binary file's
        // size is exactly the header, the count and one record per facet. Sniffing for "solid"
        // is less reliable, since binary writers also stamp it into the header.
        return IsBinary(bytes) ? ReadBinary(bytes, name, tolerance) : ReadAscii(bytes, name, tolerance);
    }

    public static byte[] Write(IMesh mesh)
    {
        var bytes = new byte[HeaderBytes + 4 + (mesh.TriangleCount * FacetBytes)];
        var label = Encoding.ASCII.GetBytes($"GeometryEngine {mesh.Metadata.Name}");
        Array.Copy(label, bytes, Math.Min(label.Length, 79));
        BitConverter.TryWriteBytes(bytes.AsSpan(HeaderBytes), (uint)mesh.TriangleCount);

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (a, b, c) = mesh.TriangleAt(t);
            var normal = Direction.From((b - a).Cross(c - a)).GetValueOrDefault(Direction.Z).Vector;

            var offset = HeaderBytes + 4 + (t * FacetBytes);
            WriteVector(bytes, offset, normal);
            WriteVector(bytes, offset + 12, a);
            WriteVector(bytes, offset + 24, b);
            WriteVector(bytes, offset + 36, c);
        }

        return bytes;
    }

    private static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes + 4)
        {
            return false;
        }

        var count = BitConverter.ToUInt32(bytes[HeaderBytes..]);
        return HeaderBytes + 4 + ((long)count * FacetBytes) == bytes.Length;
    }

    private static Result<IMesh> ReadBinary(ReadOnlySpan<byte> bytes, string name, Tolerance tolerance)
    {
        var count = (int)BitConverter.ToUInt32(bytes[HeaderBytes..]);
        var welder = new VertexWelder(tolerance.Value);
        var triangles = ImmutableArray.CreateBuilder<int>(count * 3);

        for (var facet = 0; facet < count; facet++)
        {
            var offset = HeaderBytes + 4 + (facet * FacetBytes) + 12;
            AddFacet(welder, triangles, ReadVector(bytes, offset), ReadVector(bytes, offset + 12), ReadVector(bytes, offset + 24));
        }

        return ImmutableMesh.Create([.. welder.Vertices], triangles.ToImmutable(), new MeshMetadata(name, "GeometryEngine.MeshIO"));
    }

    /// <summary>
    /// The grammar is verbose - solid/facet/outer loop/vertex/endloop/endfacet/endsolid - but only
    /// the vertex lines carry geometry, so the parser reads three coordinates after each "vertex"
    /// keyword and groups every three vertices into a facet. Normals are ignored, as they are for
    /// binary: writing recomputes them.
    /// </summary>
    private static Result<IMesh> ReadAscii(ReadOnlySpan<byte> bytes, string name, Tolerance tolerance)
    {
        // Latin-1 maps every byte, so a stray non-ASCII byte in a comment cannot throw.
        var tokens = Encoding.Latin1.GetString(bytes).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var welder = new VertexWelder(tolerance.Value);
        var triangles = ImmutableArray.CreateBuilder<int>();
        var facet = new Vec3[3];
        var corner = 0;

        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].Equals("vertex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 3 >= tokens.Length ||
                !TextTokens.TryParse(tokens[i + 1], out var x) ||
                !TextTokens.TryParse(tokens[i + 2], out var y) ||
                !TextTokens.TryParse(tokens[i + 3], out var z))
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

    private static void AddFacet(VertexWelder welder, ImmutableArray<int>.Builder triangles, Vec3 a, Vec3 b, Vec3 c)
    {
        var ia = welder.AddOrGet(a);
        var ib = welder.AddOrGet(b);
        var ic = welder.AddOrGet(c);

        if (ia != ib && ib != ic && ic != ia)
        {
            triangles.Add(ia);
            triangles.Add(ib);
            triangles.Add(ic);
        }
    }

    private static Vec3 ReadVector(ReadOnlySpan<byte> bytes, int offset) => new(
        BitConverter.ToSingle(bytes[offset..]),
        BitConverter.ToSingle(bytes[(offset + 4)..]),
        BitConverter.ToSingle(bytes[(offset + 8)..]));

    private static void WriteVector(byte[] bytes, int offset, Vec3 vector)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(offset), (float)vector.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 4), (float)vector.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 8), (float)vector.Z);
    }
}

/// <summary>Wavefront OBJ: positions and faces only. Faces may be polygons and are fanned into triangles.</summary>
internal static class ObjFormat
{
    public static Result<IMesh> Read(ReadOnlySpan<byte> bytes, string name)
    {
        var vertices = new List<Vec3>();
        var triangles = new List<int>();

        foreach (var rawLine in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            var parts = rawLine.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].StartsWith('#'))
            {
                continue;
            }

            if (parts[0] == "v" && parts.Length >= 4)
            {
                vertices.Add(new Vec3(TextTokens.Parse(parts[1]), TextTokens.Parse(parts[2]), TextTokens.Parse(parts[3])));
            }
            else if (parts[0] == "f" && parts.Length >= 4)
            {
                // Keep only the position index before any texture or normal index.
                var corners = new int[parts.Length - 1];
                for (var i = 1; i < parts.Length; i++)
                {
                    var token = parts[i];
                    var slash = token.IndexOf('/');
                    var index = int.Parse(slash >= 0 ? token[..slash] : token, CultureInfo.InvariantCulture);

                    // Positive indices count from one; negative ones count back from the latest vertex.
                    corners[i - 1] = index > 0 ? index - 1 : vertices.Count + index;
                }

                Fan(corners, triangles);
            }
        }

        return MeshFormatShared.Finish(vertices, triangles, name);
    }

    public static byte[] Write(IMesh mesh)
    {
        var text = new StringBuilder();
        foreach (var v in mesh.Vertices)
        {
            text.Append("v ").Append(TextTokens.Format(v.X)).Append(' ').Append(TextTokens.Format(v.Y)).Append(' ').Append(TextTokens.Format(v.Z)).Append('\n');
        }

        for (var i = 0; i + 2 < mesh.Triangles.Length; i += 3)
        {
            text.Append("f ").Append(mesh.Triangles[i] + 1).Append(' ').Append(mesh.Triangles[i + 1] + 1).Append(' ').Append(mesh.Triangles[i + 2] + 1).Append('\n');
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    public static void Fan(IReadOnlyList<int> corners, List<int> triangles)
    {
        for (var i = 1; i + 1 < corners.Count; i++)
        {
            triangles.Add(corners[0]);
            triangles.Add(corners[i]);
            triangles.Add(corners[i + 1]);
        }
    }
}

/// <summary>Object File Format: a vertex count, a face count, then the lists.</summary>
internal static class OffFormat
{
    public static Result<IMesh> Read(ReadOnlySpan<byte> bytes, string name)
    {
        var tokens = Encoding.UTF8.GetString(bytes)
            .Split('\n')
            .Select(line => line.Split('#')[0])
            .SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        var cursor = 0;
        if (cursor < tokens.Length && tokens[cursor].StartsWith("OFF", StringComparison.OrdinalIgnoreCase))
        {
            cursor++;
        }

        var vertexCount = int.Parse(tokens[cursor++], CultureInfo.InvariantCulture);
        var faceCount = int.Parse(tokens[cursor++], CultureInfo.InvariantCulture);
        cursor++; // The edge count, unused and usually zero.

        var vertices = new List<Vec3>(vertexCount);
        for (var i = 0; i < vertexCount; i++, cursor += 3)
        {
            vertices.Add(new Vec3(TextTokens.Parse(tokens[cursor]), TextTokens.Parse(tokens[cursor + 1]), TextTokens.Parse(tokens[cursor + 2])));
        }

        var triangles = new List<int>(faceCount * 3);
        for (var i = 0; i < faceCount; i++)
        {
            var corners = new int[int.Parse(tokens[cursor++], CultureInfo.InvariantCulture)];
            for (var c = 0; c < corners.Length; c++)
            {
                corners[c] = int.Parse(tokens[cursor++], CultureInfo.InvariantCulture);
            }

            ObjFormat.Fan(corners, triangles);
        }

        return MeshFormatShared.Finish(vertices, triangles, name);
    }

    public static byte[] Write(IMesh mesh)
    {
        var text = new StringBuilder();
        text.Append("OFF\n").Append(mesh.VertexCount).Append(' ').Append(mesh.TriangleCount).Append(" 0\n");

        foreach (var v in mesh.Vertices)
        {
            text.Append(TextTokens.Format(v.X)).Append(' ').Append(TextTokens.Format(v.Y)).Append(' ').Append(TextTokens.Format(v.Z)).Append('\n');
        }

        for (var i = 0; i + 2 < mesh.Triangles.Length; i += 3)
        {
            text.Append("3 ").Append(mesh.Triangles[i]).Append(' ').Append(mesh.Triangles[i + 1]).Append(' ').Append(mesh.Triangles[i + 2]).Append('\n');
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }
}

/// <summary>
/// Polygon File Format, ASCII and little-endian binary. Vertex properties are located by name, so
/// files carrying normals or colours read correctly; faces are read from the vertex index list.
/// </summary>
internal static class PlyFormat
{
    private sealed record Property(string Name, string Type, string? CountType);

    private sealed record Element(string Name, int Count, List<Property> Properties);

    public static Result<IMesh> Read(ReadOnlySpan<byte> bytes, string name)
    {
        // The header is ASCII however the body is encoded.
        var headerLength = FindHeaderEnd(bytes);
        if (headerLength < 0)
        {
            return MeshIOErrors.Unreadable("The PLY file has no end_header line.");
        }

        var headerLines = Encoding.ASCII.GetString(bytes[..headerLength]).Split('\n').Select(l => l.Trim()).ToList();
        var ascii = headerLines.Any(l => l.StartsWith("format ascii", StringComparison.OrdinalIgnoreCase));
        if (!ascii && !headerLines.Any(l => l.StartsWith("format binary_little_endian", StringComparison.OrdinalIgnoreCase)))
        {
            return MeshIOErrors.Unreadable("Only ASCII and little-endian binary PLY files are supported.");
        }

        var elements = new List<Element>();
        foreach (var parts in headerLines.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            if (parts.Length >= 3 && parts[0] == "element")
            {
                elements.Add(new Element(parts[1], int.Parse(parts[2], CultureInfo.InvariantCulture), []));
            }
            else if (parts.Length >= 5 && parts[0] == "property" && parts[1] == "list" && elements.Count > 0)
            {
                elements[^1].Properties.Add(new Property(parts[4], parts[3].ToLowerInvariant(), parts[2].ToLowerInvariant()));
            }
            else if (parts.Length >= 3 && parts[0] == "property" && elements.Count > 0)
            {
                elements[^1].Properties.Add(new Property(parts[2], parts[1].ToLowerInvariant(), null));
            }
        }

        var body = bytes[headerLength..];
        var vertices = new List<Vec3>();
        var triangles = new List<int>();

        // One element's values at a time, cleared between elements: Collect copies out what it
        // keeps, so a fresh dictionary and list per vertex and per face bought nothing.
        var values = new Dictionary<string, double>();
        var list = new List<int>();

        if (ascii)
        {
            var tokens = Encoding.ASCII.GetString(body).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var cursor = 0;
            foreach (var element in elements)
            {
                for (var item = 0; item < element.Count; item++)
                {
                    values.Clear();
                    list.Clear();
                    foreach (var property in element.Properties)
                    {
                        if (property.CountType is null)
                        {
                            values[property.Name] = TextTokens.Parse(tokens[cursor++]);
                            continue;
                        }

                        var length = int.Parse(tokens[cursor++], CultureInfo.InvariantCulture);
                        for (var k = 0; k < length; k++)
                        {
                            list.Add(int.Parse(tokens[cursor++], CultureInfo.InvariantCulture));
                        }
                    }

                    Collect(element, values, list, vertices, triangles);
                }
            }
        }
        else
        {
            var cursor = 0;
            foreach (var element in elements)
            {
                for (var item = 0; item < element.Count; item++)
                {
                    values.Clear();
                    list.Clear();
                    foreach (var property in element.Properties)
                    {
                        if (property.CountType is null)
                        {
                            values[property.Name] = ReadScalar(body, ref cursor, property.Type);
                            continue;
                        }

                        var length = (int)ReadScalar(body, ref cursor, property.CountType);
                        for (var k = 0; k < length; k++)
                        {
                            list.Add((int)ReadScalar(body, ref cursor, property.Type));
                        }
                    }

                    Collect(element, values, list, vertices, triangles);
                }
            }
        }

        return MeshFormatShared.Finish(vertices, triangles, name);
    }

    private static void Collect(Element element, Dictionary<string, double> values, List<int> list, List<Vec3> vertices, List<int> triangles)
    {
        if (element.Name == "vertex")
        {
            vertices.Add(new Vec3(values.GetValueOrDefault("x"), values.GetValueOrDefault("y"), values.GetValueOrDefault("z")));
        }
        else if (element.Name == "face")
        {
            ObjFormat.Fan(list, triangles);
        }
    }

    public static byte[] Write(IMesh mesh)
    {
        var header = Encoding.ASCII.GetBytes(
            "ply\nformat binary_little_endian 1.0\n" +
            $"element vertex {mesh.VertexCount}\n" +
            "property double x\nproperty double y\nproperty double z\n" +
            $"element face {mesh.TriangleCount}\n" +
            "property list uchar int vertex_indices\n" +
            "end_header\n");

        var bytes = new byte[header.Length + (mesh.VertexCount * 24) + (mesh.TriangleCount * 13)];
        header.CopyTo(bytes, 0);

        var cursor = header.Length;
        foreach (var v in mesh.Vertices)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(cursor), v.X);
            BitConverter.TryWriteBytes(bytes.AsSpan(cursor + 8), v.Y);
            BitConverter.TryWriteBytes(bytes.AsSpan(cursor + 16), v.Z);
            cursor += 24;
        }

        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            bytes[cursor++] = 3;
            for (var c = 0; c < 3; c++, cursor += 4)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(cursor), mesh.Triangles[(t * 3) + c]);
            }
        }

        return bytes;
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
    {
        var marker = "end_header"u8;
        var at = bytes.IndexOf(marker);
        if (at < 0)
        {
            return -1;
        }

        var end = at + marker.Length;
        if (end < bytes.Length && bytes[end] == '\r')
        {
            end++;
        }

        if (end < bytes.Length && bytes[end] == '\n')
        {
            end++;
        }

        return end;
    }

    /// <param name="type">A property type, already lower-cased when the header was read.</param>
    private static double ReadScalar(ReadOnlySpan<byte> body, ref int cursor, string type)
    {
        double value;
        switch (type)
        {
            case "char" or "int8":
                value = (sbyte)body[cursor];
                cursor += 1;
                break;
            case "uchar" or "uint8":
                value = body[cursor];
                cursor += 1;
                break;
            case "short" or "int16":
                value = BitConverter.ToInt16(body[cursor..]);
                cursor += 2;
                break;
            case "ushort" or "uint16":
                value = BitConverter.ToUInt16(body[cursor..]);
                cursor += 2;
                break;
            case "int" or "int32":
                value = BitConverter.ToInt32(body[cursor..]);
                cursor += 4;
                break;
            case "uint" or "uint32":
                value = BitConverter.ToUInt32(body[cursor..]);
                cursor += 4;
                break;
            case "float" or "float32":
                value = BitConverter.ToSingle(body[cursor..]);
                cursor += 4;
                break;
            case "double" or "float64":
                value = BitConverter.ToDouble(body[cursor..]);
                cursor += 8;
                break;
            default:
                throw new FormatException($"Unknown PLY property type '{type}'.");
        }

        return value;
    }
}

/// <summary>Number formatting that round-trips and ignores the machine's culture.</summary>
internal static class TextTokens
{
    public static bool TryParse(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static double Parse(string token) => double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>What every indexed format does once its arrays are read.</summary>
internal static class MeshFormatShared
{
    /// <summary>
    /// Validates and compacts freshly read geometry. Indexed formats already share vertices, so
    /// nothing is welded; unused vertices are dropped, as a loader that packs its mesh would.
    /// </summary>
    public static Result<IMesh> Finish(List<Vec3> vertices, List<int> triangles, string name)
    {
        if (triangles.Count == 0)
        {
            return MeshIOErrors.Unreadable("The file holds no triangles.");
        }

        if (triangles.Any(index => index < 0 || index >= vertices.Count))
        {
            return MeshIOErrors.Unreadable("A face refers to a vertex the file does not have.");
        }

        var (compactVertices, compactTriangles) = MeshCleanup.Compact(vertices, triangles);
        return ImmutableMesh.Create(compactVertices, compactTriangles, new MeshMetadata(name, "GeometryEngine.MeshIO"));
    }
}
