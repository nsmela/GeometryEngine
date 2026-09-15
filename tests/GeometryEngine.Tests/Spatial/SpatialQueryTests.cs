using GeometryEngine.Internal.Native;

namespace GeometryEngine.Tests.Spatial;

[Suite("Spatial / queries")]
public sealed class SpatialQueryTests
{
    private static ISpatialIndex IndexOf(IMesh mesh) => Fixtures.Engine.Spatial.BuildIndex(mesh).Value;

    [Fact]
    public void The_native_distance_field_ships_and_loads_on_this_platform()
    {
        // Everything below passes without it, only slower - which is exactly why a silent
        // fallback needs a test of its own. Windows x64 is the one platform binaries ship for.
        if (OperatingSystem.IsWindows() && Environment.Is64BitProcess)
        {
            Check.True(DistanceFieldNative.IsAvailable);
        }
    }

    [Fact]
    public void A_ray_fired_at_a_sphere_hits_its_near_side_with_an_outward_normal()
    {
        using var index = IndexOf(Fixtures.Sphere(Vec3.Zero, 5, 64));

        var hit = index.Raycast(new Vec3(0, 0, 20), Direction.From(new Vec3(0, 0, -1)).Value);

        Check.True(hit.HasValue);
        Check.Close(15, hit.Value.Distance, 1e-9);
        Check.Close(5, hit.Value.Point.Z, 1e-9);
        Check.Greater(hit.Value.Normal.Z, 0.99);
    }

    [Fact]
    public void A_ray_pointing_away_from_a_mesh_misses_it()
    {
        using var index = IndexOf(Fixtures.UnitCube());

        Check.False(index.Raycast(new Vec3(0.5, 0.5, 5), Direction.Z).HasValue);
    }

    [Fact]
    public void A_ray_through_a_shared_edge_does_not_slip_between_its_triangles()
    {
        // The cube's faces are split along their diagonals; aim exactly at one.
        using var index = IndexOf(Fixtures.UnitCube());

        var hit = index.Raycast(new Vec3(0.5, 0.5, 3), Direction.From(new Vec3(0, 0, -1)).Value);

        Check.True(hit.HasValue);
        Check.Close(2, hit.Value.Distance, 1e-9);
    }

    [Fact]
    public void The_closest_point_to_a_point_beside_a_cube_is_on_the_facing_side()
    {
        using var index = IndexOf(Fixtures.UnitCube());

        var closest = index.ClosestPoint(new Vec3(3, 0.25, 0.75)).Value;

        Check.Close(1, closest.Point.X, 1e-12);
        Check.Close(0.25, closest.Point.Y, 1e-12);
        Check.Close(0.75, closest.Point.Z, 1e-12);
        Check.Close(2, closest.Distance, 1e-12);
        Check.Close(1, closest.Normal.X, 1e-12);
    }

    [Fact]
    public void Signed_distance_is_negative_inside_and_positive_outside()
    {
        using var index = IndexOf(Fixtures.Sphere(Vec3.Zero, 10, 64));

        Check.Less(index.SignedDistance(Vec3.Zero), -9.9);
        Check.Greater(index.SignedDistance(new Vec3(0, 0, 15)), 4.9);
    }

    [Fact]
    public void Signed_distance_holds_its_sign_in_a_concave_crease()
    {
        // Outside a convex solid every face near a point agrees on the sign, so a per-face sign
        // test looks perfect on a sphere. A crease is where it goes wrong: two overlapping spheres
        // make one, and points just inside and outside it must still read correctly.
        var peanut = Fixtures.Engine.Booleans.Union(
            Fixtures.Sphere(new Vec3(-7, 0, 0), 12, 48),
            Fixtures.Sphere(new Vec3(7, 0, 0), 12, 48)).Value;

        using var index = IndexOf(peanut);

        // The crease ring sits at x = 0, radius sqrt(144 - 49), about 9.75.
        Check.Less(index.SignedDistance(new Vec3(0, 9.3, 0)), 0);
        Check.Greater(index.SignedDistance(new Vec3(0, 10.2, 0)), 0);
        Check.Less(index.SignedDistance(new Vec3(0, 0, 9.3)), 0);
    }

    [Fact]
    public void A_batch_of_signed_distances_agrees_with_one_at_a_time()
    {
        // A batch this size goes to the native field where it is present, so this is also the
        // check that the native and managed fields agree on a real scanned bolus.
        var bolus = Assets.LoadBench("ear_bolus.stl");
        using var index = IndexOf(bolus);

        var stats = Fixtures.Engine.Evaluators.GetStatistics(bolus).Value;
        var random = new Random(1234);
        var points = ImmutableArray.CreateRange(Enumerable.Range(0, 2000).Select(_ => new Vec3(
            stats.BoundsMin.X + (random.NextDouble() * stats.BoundsSize.X),
            stats.BoundsMin.Y + (random.NextDouble() * stats.BoundsSize.Y),
            stats.BoundsMin.Z + (random.NextDouble() * stats.BoundsSize.Z))));

        var batch = index.SignedDistances(points);

        var signDisagreements = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var single = index.SignedDistance(points[i]);
            Check.Close(Math.Abs(single), Math.Abs(batch[i]), 1e-6);

            // The two fields sign differently - pseudonormal against winding number - and may
            // disagree within a hair of the surface, where the sign barely matters. Away from it
            // they must agree.
            if (Math.Abs(single) > 0.05 && Math.Sign(single) != Math.Sign(batch[i]))
            {
                signDisagreements++;
            }
        }

        Check.Equal(0, signDisagreements);
    }

    [Fact]
    public void Querying_a_disposed_index_is_refused()
    {
        var index = IndexOf(Fixtures.UnitCube());
        index.Dispose();

        Check.Throws<ObjectDisposedException>(() => index.SignedDistance(Vec3.Zero));
    }

    [Fact]
    public void An_empty_mesh_cannot_be_indexed()
    {
        var result = Fixtures.Engine.Spatial.BuildIndex(ImmutableMesh.Empty);

        Check.True(result.IsFailure);
        Check.Equal("Mesh.EmptyOperand", result.Error.Code);
    }
}
