namespace GeometryEngine.Internal.Spatial;

/// <summary>Exact triangle-triangle crossing, the narrow phase of self-intersection counting.</summary>
internal static class TriangleIntersection
{
    /// <summary>
    /// Vertices this close to a plane, relative to the size of the triangle defining it, count as
    /// on it. Loose enough that two triangles meeting along a shared edge are not reported as
    /// crossing it.
    /// </summary>
    private const double PlaneTolerance = 1e-7;

    /// <summary>
    /// True when the two triangles pass through each other. Triangles that merely touch - sharing
    /// an edge or a corner, or lying in the same plane - are not intersections.
    ///
    /// Moller's interval test: project both triangles onto the line where their planes meet and
    /// see whether the resulting intervals overlap. Coplanar pairs are deliberately ignored; the
    /// callers only care about surfaces that actually pierce one another.
    /// </summary>
    public static bool Intersects(Vec3 a0, Vec3 a1, Vec3 a2, Vec3 b0, Vec3 b1, Vec3 b2)
    {
        var normalA = (a1 - a0).Cross(a2 - a0);
        var scaleA = normalA.Length;
        if (scaleA < 1e-300)
        {
            return false; // Degenerate: nothing to pierce.
        }

        var offsetA = -normalA.Dot(a0);
        var toleranceA = PlaneTolerance * scaleA * Size(a0, a1, a2);
        var db0 = Snap(normalA.Dot(b0) + offsetA, toleranceA);
        var db1 = Snap(normalA.Dot(b1) + offsetA, toleranceA);
        var db2 = Snap(normalA.Dot(b2) + offsetA, toleranceA);

        if (SameSide(db0, db1, db2))
        {
            return false;
        }

        var normalB = (b1 - b0).Cross(b2 - b0);
        var scaleB = normalB.Length;
        if (scaleB < 1e-300)
        {
            return false;
        }

        var offsetB = -normalB.Dot(b0);
        var toleranceB = PlaneTolerance * scaleB * Size(b0, b1, b2);
        var da0 = Snap(normalB.Dot(a0) + offsetB, toleranceB);
        var da1 = Snap(normalB.Dot(a1) + offsetB, toleranceB);
        var da2 = Snap(normalB.Dot(a2) + offsetB, toleranceB);

        if (SameSide(da0, da1, da2))
        {
            return false;
        }

        // Project onto the dominant axis of the line where the two planes meet.
        var direction = normalA.Cross(normalB);
        var x = Math.Abs(direction.X);
        var y = Math.Abs(direction.Y);
        var z = Math.Abs(direction.Z);
        var axis = x > y ? (x > z ? 0 : 2) : (y > z ? 1 : 2);

        if (!Interval(Axis(a0, axis), Axis(a1, axis), Axis(a2, axis), da0, da1, da2, out var aMin, out var aMax) ||
            !Interval(Axis(b0, axis), Axis(b1, axis), Axis(b2, axis), db0, db1, db2, out var bMin, out var bMax))
        {
            return false;
        }

        // Touching endpoints are a shared boundary, not a crossing.
        var touch = PlaneTolerance * Math.Max(Size(a0, a1, a2), Size(b0, b1, b2));
        return aMax > bMin + touch && bMax > aMin + touch;
    }

    private static double Size(Vec3 a, Vec3 b, Vec3 c) =>
        Math.Max((b - a).Length, Math.Max((c - b).Length, (a - c).Length));

    private static double Snap(double value, double tolerance) => Math.Abs(value) < tolerance ? 0 : value;

    /// <summary>
    /// True when no vertex lies strictly on each side of the plane. Non-strict on purpose: a
    /// triangle that only touches a plane - a corner or an edge on it, or lying in it - does not
    /// pass through it. Unwelded solids meeting along an edge would otherwise count as crossing.
    /// </summary>
    private static bool SameSide(double d0, double d1, double d2) =>
        (d0 >= 0 && d1 >= 0 && d2 >= 0) || (d0 <= 0 && d1 <= 0 && d2 <= 0);

    private static double Axis(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>
    /// The span of a triangle's projection between the two vertices that straddle the other
    /// plane. False when the triangle only touches that plane.
    /// </summary>
    private static bool Interval(double p0, double p1, double p2, double d0, double d1, double d2, out double min, out double max)
    {
        min = 0;
        max = 0;

        // Reorder so the vertex alone on its side comes first.
        if (d0 * d1 > 0)
        {
            (p0, p2) = (p2, p0);
            (d0, d2) = (d2, d0);
        }
        else if (d0 * d2 > 0)
        {
            (p0, p1) = (p1, p0);
            (d0, d1) = (d1, d0);
        }
        else if (d1 * d2 > 0 || d0 != 0)
        {
            // d0 is already the odd one out.
        }
        else if (d1 != 0)
        {
            (p0, p1) = (p1, p0);
            (d0, d1) = (d1, d0);
        }
        else if (d2 != 0)
        {
            (p0, p2) = (p2, p0);
            (d0, d2) = (d2, d0);
        }
        else
        {
            return false;
        }

        var denominator1 = d0 - d1;
        var denominator2 = d0 - d2;
        if (Math.Abs(denominator1) < 1e-300 || Math.Abs(denominator2) < 1e-300)
        {
            return false;
        }

        var t1 = p0 + ((p1 - p0) * d0 / denominator1);
        var t2 = p0 + ((p2 - p0) * d0 / denominator2);

        min = Math.Min(t1, t2);
        max = Math.Max(t1, t2);
        return true;
    }
}
