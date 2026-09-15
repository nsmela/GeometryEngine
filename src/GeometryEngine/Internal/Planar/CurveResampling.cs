namespace GeometryEngine.Internal.Planar;

/// <summary>
/// Polyline resampling and smoothing, ported from geometry3Sharp's CurveResampler and
/// InPlaceIterativeCurveSmooth (Boost Software License 1.0; see THIRD-PARTY-NOTICES.md), which
/// is what Fabolus used before these operations moved here. Ported rather than rewritten so
/// outlines and painted paths keep the exact shape they had: both algorithms are simple, and
/// their small quirks are part of that shape.
/// </summary>
internal static class CurveResampling
{
    /// <summary>
    /// Splits edges longer than <paramref name="maxEdge"/> and collapses runs of edges shorter
    /// than <paramref name="minEdge"/>. Returns null when no edge needs either, as the original
    /// does. On a closed curve the first point is repeated at the end, again as the original does.
    /// </summary>
    public static List<Vec3>? SplitCollapse(IReadOnlyList<Vec3> curve, bool closed, double maxEdge, double minEdge)
    {
        var maxSquared = maxEdge * maxEdge;
        var minSquared = minEdge * minEdge;

        var count = curve.Count;
        var stop = closed ? count + 1 : count;
        var lengths = new double[stop];
        var split = false;
        var collapse = false;

        for (var i = 0; i < stop - 1; i++)
        {
            lengths[i] = (curve[i] - curve[(i + 1) % count]).LengthSquared;
            if (lengths[i] > maxSquared)
            {
                split = true;
            }
            else if (lengths[i] < minSquared)
            {
                collapse = true;
            }
        }

        if (!split && !collapse)
        {
            return null;
        }

        var result = new List<Vec3> { curve[0] };
        var previous = curve[0];
        var accumulated = 0.0;

        for (var i = 0; i < stop - 1; i++)
        {
            var next = curve[(i + 1) % count];

            // Accumulate collapsed edges; past the minimum length, a vertex is dropped in.
            if (lengths[i] < minSquared)
            {
                accumulated += Math.Sqrt(lengths[i]);
                if (accumulated > minEdge)
                {
                    accumulated = 0;
                    result.Add(next);
                }

                previous = next;
                continue;
            }

            if (accumulated > 0)
            {
                result.Add(previous);
                accumulated = 0;
            }

            if (lengths[i] > maxSquared)
            {
                var steps = (int)(Math.Sqrt(lengths[i]) / maxEdge) + 1;
                for (var k = 1; k < steps; k++)
                {
                    result.Add(previous.LerpTo(next, (double)k / steps));
                }
            }

            result.Add(next);
            previous = next;
        }

        return result;
    }

    /// <summary>
    /// Moves each vertex <paramref name="alpha"/> of the way towards the midpoint of its
    /// neighbours, in place and in order, <paramref name="iterations"/> times. An open curve keeps
    /// its end points.
    /// </summary>
    public static void Smooth(List<Vec3> curve, bool closed, double alpha, int iterations)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        var count = curve.Count;

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if (closed)
            {
                for (var i = 0; i < count; i++)
                {
                    var previous = curve[i == 0 ? count - 1 : i - 1];
                    var next = curve[(i + 1) % count];
                    curve[i] = (curve[i] * (1 - alpha)) + ((previous + next) * 0.5 * alpha);
                }
            }
            else
            {
                for (var i = 1; i < count - 1; i++)
                {
                    curve[i] = (curve[i] * (1 - alpha)) + ((curve[i - 1] + curve[i + 1]) * 0.5 * alpha);
                }
            }
        }
    }
}
