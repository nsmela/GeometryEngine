using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GeometryEngine.MeshIO;

/// <summary>
/// 3MF: a zip holding an XML model. The model object is the first mesh object that is not the
/// reference; the reference is marked with the vendor's role attribute, or failing that with
/// type="other", which is what keeps slicers from printing it. Metadata is written as the core
/// schema's own metadata elements under the vendor prefix, which strict consumers accept and
/// ignore.
///
/// Reading honours the build item's transform on the model object, so a file positioned by
/// another tool comes in where that tool put it.
/// </summary>
internal static class ThreeMfFormat
{
    private static readonly XNamespace Core = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";

    private const string ModelEntry = "3D/3dmodel.model";

    public static Result<MeshPackage> ReadPackage(ReadOnlySpan<byte> bytes, string name, PackageVendor vendor)
    {
        XDocument document;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes.ToArray()), ZipArchiveMode.Read);
            var entry = archive.GetEntry(ModelEntry)
                ?? archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return MeshIOErrors.Unreadable("The 3MF package holds no model.");
            }

            using var stream = entry.Open();
            document = XDocument.Load(stream);
        }
        catch (Exception failure) when (failure is InvalidDataException or XmlException or IOException)
        {
            return MeshIOErrors.Unreadable($"The 3MF package could not be opened: {failure.Message}");
        }

        var root = document.Root;
        var resources = root?.Element(Core + "resources");
        if (root is null || resources is null)
        {
            return MeshIOErrors.Unreadable("The 3MF model has no resources.");
        }

        XNamespace vendorNs = vendor.NamespaceUri;
        var objects = resources.Elements(Core + "object").Where(o => o.Element(Core + "mesh") is not null).ToList();
        if (objects.Count == 0)
        {
            return MeshIOErrors.Unreadable("The 3MF model holds no mesh objects.");
        }

        var reference = objects.FirstOrDefault(o =>
            string.Equals(o.Attribute(vendorNs + "role")?.Value, vendor.ReferenceRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(o.Attribute("type")?.Value, "other", StringComparison.OrdinalIgnoreCase));

        var model = objects.FirstOrDefault(o => o != reference);
        if (model is null)
        {
            // Only one object, and it looked like a reference: it is the model after all.
            model = objects[0];
            reference = null;
        }

        try
        {
            var transform = BuildTransform(root, model.Attribute("id")?.Value);
            var modelMesh = ReadObject(model, name, transform);
            if (modelMesh.IsFailure)
            {
                return Result.Failure<MeshPackage>(modelMesh.Error);
            }

            var referenceMesh = Maybe<IMesh>.None();
            if (reference is not null)
            {
                var read = ReadObject(reference, $"{name} reference", null);
                if (read.IsFailure)
                {
                    return Result.Failure<MeshPackage>(read.Error);
                }

                referenceMesh = Maybe<IMesh>.Some(read.Value);
            }

            var metadata = root.Elements(Core + "metadata")
                .Where(e => e.Attribute("name") is not null)
                .GroupBy(e => e.Attribute("name")!.Value, StringComparer.Ordinal)
                .ToImmutableDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

            return new MeshPackage(modelMesh.Value, referenceMesh, metadata, vendor);
        }
        catch (Exception failure) when (failure is FormatException or OverflowException or InvalidDataException)
        {
            return MeshIOErrors.Unreadable($"The 3MF model is malformed: {failure.Message}");
        }
    }

    public static byte[] WritePackage(MeshPackage package)
    {
        XNamespace vendorNs = package.Vendor.NamespaceUri;
        var model = new XElement(Core + "model",
            new XAttribute("unit", "millimeter"),
            new XAttribute(XNamespace.Xmlns + package.Vendor.Prefix, package.Vendor.NamespaceUri));

        foreach (var (key, value) in package.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            model.Add(new XElement(Core + "metadata", new XAttribute("name", key), value));
        }

        var resources = new XElement(Core + "resources", BuildObject(1, package.Model, "model"));
        if (package.Reference.HasValue)
        {
            // "other" keeps slicers from treating the unreferenced copy as printable; the role
            // attribute is what a reader keys on.
            var reference = BuildObject(2, package.Reference.Value, "other");
            reference.SetAttributeValue(vendorNs + "role", package.Vendor.ReferenceRole);
            resources.Add(reference);
        }

        model.Add(resources);
        model.Add(new XElement(Core + "build", new XElement(Core + "item", new XAttribute("objectid", "1"))));

        return Zip(model);
    }

    public static Result<IMesh> Read(ReadOnlySpan<byte> bytes, string name)
    {
        var package = ReadPackage(bytes, name, new PackageVendor("ge", "urn:geometryengine:3mf", "reference"));
        return package.IsSuccess ? Result.Success(package.Value.Model) : Result.Failure<IMesh>(package.Error);
    }

    public static byte[] Write(IMesh mesh) =>
        WritePackage(new MeshPackage(
            mesh,
            Maybe<IMesh>.None(),
            ImmutableDictionary<string, string>.Empty,
            new PackageVendor("ge", "urn:geometryengine:3mf", "reference")));

    private static byte[] Zip(XElement model)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteText(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\" />" +
                "<Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\" />" +
                "</Types>");

            WriteText(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" " +
                "Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\" />" +
                "</Relationships>");

            var entry = archive.CreateEntry(ModelEntry);
            using var stream = entry.Open();
            using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
            new XDocument(model).Save(writer);
        }

        return buffer.ToArray();
    }

    private static void WriteText(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static XElement BuildObject(int id, IMesh mesh, string type)
    {
        var vertices = new XElement(Core + "vertices");
        foreach (var v in mesh.Vertices)
        {
            vertices.Add(new XElement(Core + "vertex",
                new XAttribute("x", TextTokens.Format(v.X)),
                new XAttribute("y", TextTokens.Format(v.Y)),
                new XAttribute("z", TextTokens.Format(v.Z))));
        }

        var triangles = new XElement(Core + "triangles");
        for (var i = 0; i + 2 < mesh.Triangles.Length; i += 3)
        {
            triangles.Add(new XElement(Core + "triangle",
                new XAttribute("v1", mesh.Triangles[i]),
                new XAttribute("v2", mesh.Triangles[i + 1]),
                new XAttribute("v3", mesh.Triangles[i + 2])));
        }

        return new XElement(Core + "object",
            new XAttribute("id", id.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("type", type),
            new XElement(Core + "mesh", vertices, triangles));
    }

    private static Result<IMesh> ReadObject(XElement element, string name, double[]? transform)
    {
        var mesh = element.Element(Core + "mesh")!;
        var vertexElements = mesh.Element(Core + "vertices")?.Elements(Core + "vertex");
        var triangleElements = mesh.Element(Core + "triangles")?.Elements(Core + "triangle");
        if (vertexElements is null || triangleElements is null)
        {
            return MeshIOErrors.Unreadable("A 3MF mesh object is missing its vertices or triangles.");
        }

        var vertices = new List<Vec3>();
        foreach (var vertex in vertexElements)
        {
            var point = new Vec3(Number(vertex, "x"), Number(vertex, "y"), Number(vertex, "z"));
            vertices.Add(transform is null ? point : Apply(transform, point));
        }

        var triangles = new List<int>();
        foreach (var triangle in triangleElements)
        {
            triangles.Add(Index(triangle, "v1"));
            triangles.Add(Index(triangle, "v2"));
            triangles.Add(Index(triangle, "v3"));
        }

        return MeshFormatShared.Finish(vertices, triangles, name);
    }

    /// <summary>The 3x4 affine matrix on the build item that places an object, if it has one.</summary>
    private static double[]? BuildTransform(XElement root, string? objectId)
    {
        var item = root.Element(Core + "build")?.Elements(Core + "item")
            .FirstOrDefault(i => i.Attribute("objectid")?.Value == objectId);

        var text = item?.Attribute("transform")?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var values = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(TextTokens.Parse).ToArray();
        return values.Length == 12 ? values : null;
    }

    /// <summary>3MF matrices apply to row vectors: the last three entries are the translation.</summary>
    private static Vec3 Apply(double[] m, Vec3 p) => new(
        (p.X * m[0]) + (p.Y * m[3]) + (p.Z * m[6]) + m[9],
        (p.X * m[1]) + (p.Y * m[4]) + (p.Z * m[7]) + m[10],
        (p.X * m[2]) + (p.Y * m[5]) + (p.Z * m[8]) + m[11]);

    private static double Number(XElement element, string attribute) =>
        TextTokens.Parse(element.Attribute(attribute)?.Value ?? throw new FormatException($"A vertex is missing '{attribute}'."));

    private static int Index(XElement element, string attribute) =>
        int.Parse(element.Attribute(attribute)?.Value ?? throw new FormatException($"A triangle is missing '{attribute}'."), CultureInfo.InvariantCulture);
}
