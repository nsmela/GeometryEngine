namespace GeometryEngine.Tests.Csg;

[Suite("Csg / coplanar merge")]
public sealed class CoplanarMergeTests
{
    [Fact]
    public void Boring_a_hole_collapses_the_redundant_coplanar_fragments()
    {
        var box = Fixtures.Box(new Vec3(-5, -5, -1), new Vec3(5, 5, 1));
        var drill = Fixtures.Cylinder(new Vec3(0, 0, -2), 0.5, 4, 12);

        var holed = Fixtures.Engine.Booleans.Subtract(box, drill).Value;

        // The two large faces the drill passes through are sliced across their whole
        // extent by its infinite side planes; without merging this lands near 256
        // triangles. Merging brings it well under 200 while keeping the surface sound.
        Check.True(holed.TriangleCount < 200);
        Check.True(Fixtures.TopologyOf(holed).IsWatertight);
        Check.RelativelyClose(200 - (Math.PI * 0.25 * 2), Fixtures.VolumeOf(holed), 1e-3);
    }

    [Fact]
    public void Merging_keeps_a_drilled_plate_watertight_and_correct()
    {
        // This is exact synthetic geometry, so it pins the tight tolerance that suits
        // exact coordinates. The default engine scales tolerance to the operand size
        // (right for float-precision meshes imported from STL, but looser than an exact
        // plate at this scale wants); the fixed engine keeps the merge on exact input.
        var engine = BspGeometryEngine.Create(Tolerance.From(1e-9).Value);
        var part = engine.Generators.GenerateBox(new Vec3(-5, -5, -1), new Vec3(5, 5, 1)).Value;

        for (var i = 0; i < 8; i++)
        {
            var angle = 2 * Math.PI * i / 8;
            var drill = engine.Generators.GenerateCylinder(new Vec3(3 * Math.Cos(angle), 3 * Math.Sin(angle), -2), 0.3, 4, 12).Value;
            part = engine.Booleans.Subtract(part, drill).Value;
        }

        Check.True(Fixtures.TopologyOf(part).IsWatertight);

        // Each 12-gon drill removes a little less than the ideal cylinder, so allow the
        // usual tessellation slack rather than an exact analytic match.
        Check.RelativelyClose(200 - (8 * Math.PI * 0.09 * 2), Fixtures.VolumeOf(part), 0.02);
    }

    [Fact]
    public void Merging_leaves_a_curved_union_watertight()
    {
        // Spheres have no broad flat faces to merge; the pass must fall back cleanly
        // rather than flatten their facets into slivers (an asymmetric pair, to avoid
        // the coincident-face degeneracy of two equal spheres meeting on a plane).
        var left = Fixtures.Sphere(Vec3.Zero, 1, 40);
        var right = Fixtures.Sphere(new Vec3(0.9, 0.2, 0), 1, 40);

        var united = Fixtures.Engine.Booleans.Union(left, right).Value;

        Check.True(Fixtures.TopologyOf(united).IsWatertight);
    }
}
