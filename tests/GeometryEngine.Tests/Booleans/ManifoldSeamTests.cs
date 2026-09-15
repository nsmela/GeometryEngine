namespace GeometryEngine.Tests.Booleans;

[Suite("Booleans / native kernel seams")]
public sealed class ManifoldSeamTests
{
    /// <summary>
    /// An air channel through a mould's trough: a tube whose ring lands exactly on the basin
    /// floor it passes through. The native kernel's result is closed, but it carries slivers
    /// along that ring - triangles with a hair of area in double precision and none at all once
    /// the coordinates are rounded to 32-bit floats, as every STL, and every consumer working in
    /// single precision, does.
    ///
    /// The topology evaluator used to drop a zero-area triangle's edges from the pairing, so after
    /// that rounding the seam read as a ring of boundary edges, the solid as open, and its volume
    /// as zero. A sliver is degenerate; it is not a hole.
    /// </summary>
    [Fact]
    public void A_tube_cut_along_a_face_stays_closed_once_rounded_to_single_precision()
    {
        var body = Fixtures.Engine.Booleans.Subtract(
            Fixtures.Box(new Vec3(-10, -10, 0), new Vec3(10, 10, 10)),
            Fixtures.Box(new Vec3(-5, -5, 5), new Vec3(5, 5, 11))).Value;

        var tube = Fixtures.Engine.Generators.GenerateTube(new TubeSpec(
            [new Vec3(0, 0, 2), new Vec3(0, 0, 5), new Vec3(0, 0, 20)],
            [1, 2.5, 2.5])).Value;

        var cut = Fixtures.Engine.Booleans.Subtract(body, tube).Value;
        Check.Equal("GeometryEngine.Booleans.Manifold", cut.Metadata.CreatedBy);

        var rounded = Fixtures.Engine.CreateMesh(
            [.. cut.Vertices.Select(v => new Vec3((float)v.X, (float)v.Y, (float)v.Z))],
            cut.Triangles,
            cut.Metadata).Value;

        var topology = Fixtures.TopologyOf(rounded);
        Check.Equal(0, topology.BoundaryEdgeCount);
        Check.True(topology.IsWatertight);
        Check.RelativelyClose(Fixtures.VolumeOf(cut), Fixtures.VolumeOf(rounded), 1e-5);
    }
}
