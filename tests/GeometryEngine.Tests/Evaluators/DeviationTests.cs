namespace GeometryEngine.Tests.Evaluators;

[Suite("Evaluators / deviation")]
public sealed class DeviationTests
{
    private static IGeometryEvaluators Evaluators => Fixtures.Engine.Evaluators;

    [Fact]
    public void A_mesh_does_not_deviate_from_itself()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 5, 32);

        var deviation = Evaluators.MeasureDeviation(sphere, sphere).Value;

        Check.Equal(sphere.VertexCount, deviation.Distances.Length);
        Check.Less(deviation.MaxOutside, 1e-9);
        Check.Less(deviation.MaxInside, 1e-9);
        Check.Less(deviation.RootMeanSquare, 1e-9);
    }

    [Fact]
    public void A_grown_copy_lies_outside_by_the_growth()
    {
        // Vertices of the larger sphere sit on its true surface, half a unit beyond the smaller
        // one's - less a little, since the smaller is faceted and its facets cut inside its radius.
        var reference = Fixtures.Sphere(Vec3.Zero, 5, 64);
        var grown = Fixtures.Sphere(Vec3.Zero, 5.5, 64);

        var deviation = Evaluators.MeasureDeviation(grown, reference).Value;

        Check.Close(0.5, deviation.MaxOutside, 0.01);
        Check.Equal(0.0, deviation.MaxInside);
        Check.Close(0.5, deviation.MeanAbsolute, 0.01);
        Check.True(deviation.Distances.All(d => d > 0));
    }

    [Fact]
    public void A_shrunk_copy_lies_inside_and_reads_negative()
    {
        var reference = Fixtures.Sphere(Vec3.Zero, 5, 64);
        var shrunk = Fixtures.Sphere(Vec3.Zero, 4.5, 64);

        var deviation = Evaluators.MeasureDeviation(shrunk, reference).Value;

        Check.Equal(0.0, deviation.MaxOutside);
        Check.Close(0.5, deviation.MaxInside, 0.01);
        Check.True(deviation.Distances.All(d => d < 0));
    }

    [Fact]
    public void Empty_meshes_cannot_be_compared()
    {
        Check.True(Evaluators.MeasureDeviation(ImmutableMesh.Empty, Fixtures.UnitCube()).IsFailure);
        Check.True(Evaluators.MeasureDeviation(Fixtures.UnitCube(), ImmutableMesh.Empty).IsFailure);
    }
}
