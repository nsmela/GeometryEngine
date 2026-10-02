namespace GeometryEngine.Tests.Decals;

[Suite("Decals / surface frames")]
public sealed class SurfaceFrameTests
{
    [Fact]
    public void A_frame_facing_up_lays_its_baseline_along_x()
    {
        var frame = SurfaceFrame.FromNormal(new Vec3(10, 20, 30), Vec3.UnitZ);

        Check.Equal(new Vec3(10, 20, 30), frame.Origin);
        Check.Equal(Vec3.UnitX, frame.U);
        Check.Equal(Vec3.UnitY, frame.V);
        Check.Equal(Vec3.UnitZ, frame.N);
    }

    [Fact]
    public void A_frame_on_a_wall_keeps_its_baseline_level_and_reads_upwards()
    {
        var frame = SurfaceFrame.FromNormal(Vec3.Zero, -Vec3.UnitY);

        Check.Equal(Vec3.UnitX, frame.U);
        Check.Equal(Vec3.UnitZ, frame.V);
    }

    [Fact]
    public void Any_normal_gives_a_right_handed_orthonormal_frame()
    {
        foreach (var normal in new[] { new Vec3(1, 2, 3), new Vec3(-0.2, 0.1, -5), new Vec3(3, -4, 0.5) })
        {
            var frame = SurfaceFrame.FromNormal(Vec3.Zero, normal, 0.7);

            Check.Less(frame.N.DistanceTo(normal.Normalize()), 1e-12);
            Check.Close(1, frame.U.Length, 1e-12);
            Check.Close(1, frame.V.Length, 1e-12);
            Check.Close(0, frame.U.Dot(frame.V), 1e-12);
            Check.Close(0, frame.U.Dot(frame.N), 1e-12);
            Check.Less(frame.U.Cross(frame.V).DistanceTo(frame.N), 1e-12);
        }
    }

    [Fact]
    public void Rotation_turns_the_baseline_counter_clockwise_about_the_normal()
    {
        var turned = SurfaceFrame.FromNormal(Vec3.Zero, Vec3.UnitZ, Math.PI / 2);

        Check.Less(turned.U.DistanceTo(Vec3.UnitY), 1e-12);
        Check.Less(turned.V.DistanceTo(-Vec3.UnitX), 1e-12);
    }

    [Fact]
    public void A_degenerate_normal_is_taken_to_face_up()
    {
        Check.Equal(Vec3.UnitZ, SurfaceFrame.FromNormal(Vec3.Zero, Vec3.Zero).N);
        Check.Equal(Vec3.UnitZ, SurfaceFrame.FromNormal(Vec3.Zero, new Vec3(double.NaN, 0, 1)).N);
    }
}
