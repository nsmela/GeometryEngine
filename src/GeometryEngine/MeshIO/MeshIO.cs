namespace GeometryEngine.MeshIO;

/// <summary>Errors the mesh I/O slices report.</summary>
internal static class MeshIOErrors
{
    public static readonly Error FileExists = new(
        "MeshIO.FileExists",
        "The target file already exists. Pass overwrite: true to replace it.");

    public static readonly Error FileNotFound = new("MeshIO.FileNotFound", "No file at that path.");

    public static Error UnsupportedFormat(string extension) => new(
        "MeshIO.UnsupportedFormat",
        $"'{extension}' is not a mesh format this library reads or writes. Supported: .stl, .obj, .off, .ply, .3mf.");

    public static Error Unreadable(string description) => new("MeshIO.Unreadable", description);

    public static Error Unwritable(string description) => new("MeshIO.Unwritable", description);
}

/// <summary>Ask for a mesh to be written to disk in the format its extension names.</summary>
public sealed record ExportRequest(IMesh Mesh, string FilePath, bool Overwrite);

internal sealed class ExportHandler(WriteHandler write)
{
    private readonly WriteHandler _write = write;

    public Result Handle(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        var format = MeshFileFormats.FromPath(request.FilePath);
        if (format.HasNoValue)
        {
            return Result.Failure(MeshIOErrors.UnsupportedFormat(Path.GetExtension(request.FilePath)));
        }

        if (File.Exists(request.FilePath) && !request.Overwrite)
        {
            return Result.Failure(MeshIOErrors.FileExists);
        }

        var bytes = _write.Handle(new WriteRequest(request.Mesh, format.Value));
        if (bytes.IsFailure)
        {
            return Result.Failure(bytes.Error);
        }

        try
        {
            File.WriteAllBytes(request.FilePath, bytes.Value);
            return Result.Success();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or ArgumentException or NotSupportedException)
        {
            // A malformed path surfaces as ArgumentException/NotSupportedException; a locked or
            // unwritable target, or a missing directory, as IOException/UnauthorizedAccessException.
            return Result.Failure(MeshIOErrors.Unwritable(failure.Message));
        }
    }
}

/// <summary>Ask for a mesh to be read from disk in the format its extension names.</summary>
public sealed record ImportRequest(string FilePath);

internal sealed class ImportHandler(ReadHandler read)
{
    private readonly ReadHandler _read = read;

    public Result<IMesh> Handle(ImportRequest request)
    {
        var format = MeshFileFormats.FromPath(request.FilePath);
        if (format.HasNoValue)
        {
            return MeshIOErrors.UnsupportedFormat(Path.GetExtension(request.FilePath));
        }

        if (!File.Exists(request.FilePath))
        {
            return MeshIOErrors.FileNotFound;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(request.FilePath);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or ArgumentException or NotSupportedException)
        {
            return MeshIOErrors.Unreadable(failure.Message);
        }

        return _read.Handle(bytes, format.Value, Path.GetFileNameWithoutExtension(request.FilePath));
    }
}

/// <summary>Ask for a mesh encoded as file contents.</summary>
public sealed record WriteRequest(IMesh Mesh, MeshFileFormat Format);

internal sealed class WriteHandler
{
    public Result<byte[]> Handle(WriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Mesh);

        if (request.Mesh.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        return request.Format switch
        {
            MeshFileFormat.Stl => StlFormat.Write(request.Mesh),
            MeshFileFormat.Obj => ObjFormat.Write(request.Mesh),
            MeshFileFormat.Off => OffFormat.Write(request.Mesh),
            MeshFileFormat.Ply => PlyFormat.Write(request.Mesh),
            MeshFileFormat.ThreeMf => ThreeMfFormat.Write(request.Mesh),
            _ => MeshIOErrors.UnsupportedFormat(request.Format.ToString()),
        };
    }
}

/// <summary>
/// Parses file contents. A malformed file is a failure, never an exception: every parser's
/// throw on truncated or garbled input is caught here and reported as unreadable.
/// </summary>
internal sealed class ReadHandler(Tolerance tolerance)
{
    private readonly Tolerance _tolerance = tolerance;

    public Result<IMesh> Handle(ReadOnlySpan<byte> bytes, MeshFileFormat format, string name)
    {
        try
        {
            return format switch
            {
                MeshFileFormat.Stl => StlFormat.Read(bytes, name, _tolerance),
                MeshFileFormat.Obj => ObjFormat.Read(bytes, name),
                MeshFileFormat.Off => OffFormat.Read(bytes, name),
                MeshFileFormat.Ply => PlyFormat.Read(bytes, name),
                MeshFileFormat.ThreeMf => ThreeMfFormat.Read(bytes, name),
                _ => MeshIOErrors.UnsupportedFormat(format.ToString()),
            };
        }
        catch (Exception failure) when (failure is FormatException or IndexOutOfRangeException
                                            or ArgumentException or OverflowException)
        {
            return MeshIOErrors.Unreadable($"The file is malformed: {failure.Message}");
        }
    }
}

/// <summary>The <see cref="IGeometryIO"/> facade over the I/O slices.</summary>
internal sealed class GeometryIO : IGeometryIO
{
    private readonly ReadHandler _read;
    private readonly WriteHandler _write = new();
    private readonly ImportHandler _import;
    private readonly ExportHandler _export;

    public GeometryIO(Tolerance tolerance)
    {
        _read = new ReadHandler(tolerance);
        _import = new ImportHandler(_read);
        _export = new ExportHandler(_write);
    }

    public Result Export(IMesh mesh, string filePath, bool overwrite = false) =>
        _export.Handle(new ExportRequest(mesh, filePath, overwrite));

    public Result<IMesh> Import(string filePath) => _import.Handle(new ImportRequest(filePath));

    public Result<IMesh> Read(ReadOnlySpan<byte> data, MeshFileFormat format, string name) => _read.Handle(data, format, name);

    public Result<byte[]> Write(IMesh mesh, MeshFileFormat format) => _write.Handle(new WriteRequest(mesh, format));

    public Result<MeshPackage> ReadPackage(ReadOnlySpan<byte> data, string name, PackageVendor vendor)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        return ThreeMfFormat.ReadPackage(data, name, vendor);
    }

    public Result<byte[]> WritePackage(MeshPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (package.Model.IsEmpty)
        {
            return MeshErrors.EmptyOperand;
        }

        return ThreeMfFormat.WritePackage(package);
    }
}
