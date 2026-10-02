using System.Collections.Immutable;

namespace GeometryEngine.Core.Geometry;

/// <summary>
/// A local high point of a surface, seen along the direction the peaks were found for.
/// </summary>
/// <param name="Id">Identifies the peak to the <see cref="ISurfacePeaks"/> it came from.</param>
/// <param name="Point">The highest vertex of the peak.</param>
/// <param name="Normal">The surface normal there, averaged over the faces around it.</param>
/// <param name="Prominence">
/// How far the surface has to drop below the peak before it joins somewhere higher - the depth of
/// the basin a fluid rising from below would trap under it. Infinite for the highest peak of each
/// connected piece, which joins nothing higher.
/// </param>
public readonly record struct SurfacePeak(int Id, Vec3 Point, Vec3 Normal, double Prominence);

/// <summary>
/// The peaks of one surface, from <see cref="IGeometryEvaluators.FindPeaks"/>, and the questions
/// that follow from them. Finding the peaks walks the whole surface; everything asked here
/// afterwards is local to one peak, so keep this rather than finding the peaks again.
/// Safe to query from several threads.
/// </summary>
public interface ISurfacePeaks
{
    /// <summary>Every local peak, highest first; ties go to the vertex that came first in the mesh.</summary>
    ImmutableArray<SurfacePeak> Peaks { get; }

    /// <summary>
    /// For each point, whether the surface nearest it is joined to <paramref name="peak"/> through
    /// surface no lower than <paramref name="depth"/> below the peak - whether it stands in the cap
    /// of the peak's basin, rather than on some other surface at that height. Nearest is judged
    /// by vertex, so on a coarse mesh a point can count as in the cap a little below its rim.
    /// </summary>
    ImmutableArray<bool> WithinReach(SurfacePeak peak, double depth, ImmutableArray<Vec3> points);

    /// <summary>
    /// The middle of the peak's top: the surface within <paramref name="tolerance"/> of its
    /// height, centred and dropped back onto the surface, with the normal there. A flat top is
    /// all summit, and its middle is a better place to mark than whichever corner happened to be
    /// highest. Where the middle is not on that top - a ring-shaped rim, say - the peak itself.
    /// </summary>
    (Vec3 Point, Vec3 Normal) Summit(SurfacePeak peak, double tolerance);
}
