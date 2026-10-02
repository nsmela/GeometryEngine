namespace GeometryEngine.Tests.Primitives;

[Suite("Primitives / Rotation")]
public sealed class RotationTests
{
    private static void Near(Vec3 expected, Vec3 actual) => Check.Less(actual.DistanceTo(expected), 1e-12);

    [Fact]
    public void A_quarter_turn_about_z_takes_x_to_y()
    {
        Near(Vec3.UnitY, Rotation.FromAxisAngle(Direction.Z, Math.PI / 2).Apply(Vec3.UnitX));
    }

    [Fact]
    public void It_agrees_with_the_axis_angle_mesh_rotation()
    {
        var axis = Direction.From(new Vec3(1, -2, 0.5)).Value;
        var box = Fixtures.Box(new Vec3(1, 2, 3), new Vec3(4, 3, 5));

        var byAxis = Fixtures.Engine.Transforms.Rotate(box, axis, 1.1).Value;
        var byRotation = Fixtures.Engine.Transforms.Rotate(box, Rotation.FromAxisAngle(axis, 1.1)).Value;

        for (var i = 0; i < box.VertexCount; i++)
        {
            Near(byAxis.Vertices[i], byRotation.Vertices[i]);
        }
    }

    [Fact]
    public void Between_takes_one_direction_onto_another()
    {
        var from = Direction.From(new Vec3(1, 2, 3)).Value;
        var to = Direction.From(new Vec3(-2, 0.5, 1)).Value;

        Near(to.Vector, Rotation.Between(from, to).Apply(from.Vector));
    }

    [Fact]
    public void Between_opposite_directions_is_a_half_turn()
    {
        var turn = Rotation.Between(Direction.Z, Direction.Z.Flipped());

        Near(-Vec3.UnitZ, turn.Apply(Vec3.UnitZ));
        Check.Close(1, turn.Apply(Vec3.UnitX).Length, 1e-12);
    }

    [Fact]
    public void Then_applies_this_rotation_first()
    {
        var first = Rotation.FromAxisAngle(Direction.Z, Math.PI / 2);
        var second = Rotation.FromAxisAngle(Direction.X, Math.PI / 2);

        // x -> y under the first, then y -> z under the second.
        Near(Vec3.UnitZ, first.Then(second).Apply(Vec3.UnitX));
    }

    [Fact]
    public void Inverse_undoes_it()
    {
        var turn = Rotation.FromAxisAngle(Direction.From(new Vec3(3, 1, -1)).Value, 2.3);
        var point = new Vec3(4, -5, 6);

        Near(point, turn.Inverse().Apply(turn.Apply(point)));
    }

    [Fact]
    public void A_quaternion_is_normalised_and_the_zero_one_refused()
    {
        var halfTurn = Rotation.FromQuaternion(0, 0, 0, 5).Value;

        Check.Close(1, halfTurn.Z, 1e-15);
        Near(-Vec3.UnitX, halfTurn.Apply(Vec3.UnitX));
        Check.False(Rotation.FromQuaternion(0, 0, 0, 0).HasValue);
        Check.False(Rotation.FromQuaternion(double.NaN, 0, 0, 1).HasValue);
    }

    [Fact]
    public void A_rotated_mesh_carries_its_measurements()
    {
        var sphere = Fixtures.Sphere(Vec3.Zero, 3, 16);
        var topology = Fixtures.Engine.Evaluators.ValidateTopology(sphere).Value;

        var turned = Fixtures.Engine.Transforms.Rotate(sphere, Rotation.FromAxisAngle(Direction.Y, 0.4)).Value;

        Check.True(ReferenceEquals(topology, Fixtures.Engine.Evaluators.ValidateTopology(turned).Value));
    }
}
