namespace GeometryEngine.Tests.Primitives;

[Suite("Primitives / Vec3")]
public sealed class Vec3Tests
{
    [Fact]
    public void Two_vectors_with_the_same_components_are_equal()
    {
        Check.Equal(new Vec3(1, 2, 3), new Vec3(1, 2, 3));
        Check.NotEqual(new Vec3(1, 2, 3), new Vec3(1, 2, 4));
    }

    [Fact]
    public void Cross_of_x_and_y_is_z()
    {
        Check.Equal(Vec3.UnitZ, Vec3.UnitX.Cross(Vec3.UnitY));
    }

    [Fact]
    public void Dot_of_perpendicular_vectors_is_zero()
    {
        Check.Close(0, Vec3.UnitX.Dot(Vec3.UnitY), 0);
    }

    [Fact]
    public void Arithmetic_produces_new_values_rather_than_mutating()
    {
        var original = new Vec3(1, 1, 1);
        var moved = original + new Vec3(2, 0, 0);

        Check.Equal(new Vec3(1, 1, 1), original);
        Check.Equal(new Vec3(3, 1, 1), moved);
    }

    [Fact]
    public void Component_wise_min_and_max_build_bounds()
    {
        var a = new Vec3(-1, 5, 0);
        var b = new Vec3(2, 1, 7);

        Check.Equal(new Vec3(-1, 1, 0), a.ComponentMin(b));
        Check.Equal(new Vec3(2, 5, 7), a.ComponentMax(b));
    }
}

[Suite("Primitives / Direction")]
public sealed class DirectionTests
{
    [Fact]
    public void A_zero_vector_has_no_direction()
    {
        Check.True(Direction.From(Vec3.Zero).HasNoValue);
    }

    [Fact]
    public void A_non_finite_vector_has_no_direction()
    {
        Check.True(Direction.From(new Vec3(double.NaN, 0, 0)).HasNoValue);
    }

    [Fact]
    public void A_direction_is_always_unit_length()
    {
        var direction = Direction.From(new Vec3(3, 4, 0));

        Check.True(direction.HasValue);
        Check.Close(1.0, direction.Value.Vector.Length, 1e-15);
    }

    [Fact]
    public void Flipping_a_direction_reverses_it()
    {
        Check.Equal(new Vec3(-1, 0, 0), Direction.X.Flipped().Vector);
    }
}

[Suite("Primitives / Plane")]
public sealed class PlaneTests
{
    private static Plane Xy() => Plane.FromPoints(Vec3.Zero, Vec3.UnitX, Vec3.UnitY).Value;

    [Fact]
    public void Collinear_points_do_not_define_a_plane()
    {
        var degenerate = Plane.FromPoints(Vec3.Zero, new Vec3(1, 0, 0), new Vec3(2, 0, 0));

        Check.True(degenerate.HasNoValue);
    }

    [Fact]
    public void Three_points_wound_counter_clockwise_face_along_the_normal()
    {
        Check.Equal(Vec3.UnitZ, Xy().Normal.Vector);
    }

    [Fact]
    public void Points_are_classified_against_the_plane()
    {
        var plane = Xy();

        Check.Equal(PointSide.Front, plane.Classify(new Vec3(0, 0, 1), Tolerance.Planar));
        Check.Equal(PointSide.Back, plane.Classify(new Vec3(0, 0, -1), Tolerance.Planar));
        Check.Equal(PointSide.Coplanar, plane.Classify(new Vec3(5, 5, 0), Tolerance.Planar));
    }

    [Fact]
    public void A_point_inside_the_tolerance_band_counts_as_coplanar()
    {
        var plane = Xy();

        Check.Equal(PointSide.Coplanar, plane.Classify(new Vec3(0, 0, 1e-9), Tolerance.Planar));
        Check.Equal(PointSide.Front, plane.Classify(new Vec3(0, 0, 1e-4), Tolerance.Planar));
    }

    [Fact]
    public void Flipping_a_plane_swaps_front_and_back()
    {
        var flipped = Xy().Flipped();

        Check.Equal(PointSide.Back, flipped.Classify(new Vec3(0, 0, 1), Tolerance.Planar));
    }

    [Fact]
    public void Segment_intersection_lands_on_the_plane()
    {
        var plane = Xy();
        var crossing = plane.IntersectSegment(new Vec3(1, 2, -3), new Vec3(1, 2, 5));

        Check.Close(0, plane.SignedDistanceTo(crossing), 1e-14);
        Check.Close(1, crossing.X, 1e-14);
        Check.Close(2, crossing.Y, 1e-14);
    }
}

[Suite("Primitives / Result and Maybe")]
public sealed class ResultTests
{
    [Fact]
    public void A_failure_carries_its_error_code()
    {
        var failure = Result.Failure<int>(new Error("Some.Code", "something went wrong"));

        Check.True(failure.IsFailure);
        Check.Equal("Some.Code", failure.Error.Code);
    }

    [Fact]
    public void Reading_the_value_of_a_failure_is_refused()
    {
        var failure = Result.Failure<int>(new Error("Some.Code", "nope"));

        Check.Throws<InvalidOperationException>(() => _ = failure.Value);
    }

    [Fact]
    public void A_failure_cannot_be_built_from_the_none_error()
    {
        Check.Throws<ArgumentException>(() => Result.Failure(Error.None));
    }

    [Fact]
    public void Map_transforms_a_success_and_passes_a_failure_through()
    {
        var doubled = Result.Success(21).Map(value => value * 2);
        var propagated = Result.Failure<int>(new Error("X", "x")).Map(value => value * 2);

        Check.Equal(42, doubled.Value);
        Check.Equal("X", propagated.Error.Code);
    }

    [Fact]
    public void An_empty_maybe_refuses_to_hand_out_a_value()
    {
        Check.Throws<InvalidOperationException>(() => _ = Maybe<string>.None().Value);
    }

    [Fact]
    public void A_maybe_converts_into_a_result_with_a_named_error()
    {
        var converted = Maybe<string>.None().ToResult(new Error("Absent", "nothing there"));

        Check.Equal("Absent", converted.Error.Code);
    }
}
