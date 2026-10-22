#include "geometryengine_native.h"
#include "manifold_dynamic.hpp"

#include <igl/AABB.h>
#include <igl/Hit.h>
#include <igl/default_num_threads.h>
#include <igl/fast_winding_number.h>

#include <Eigen/Core>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <exception>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <thread>
#include <vector>

/*
 * Row-major, so a vertex's three coordinates and a face's three corners each sit together: every
 * query fetches them together. Eigen's default is column-major, which spreads one vertex across
 * three cache lines; measured, an offset's sampling ran 11-18% faster this way, same results.
 */
using VertexMatrix = Eigen::Matrix<double, Eigen::Dynamic, 3, Eigen::RowMajor>;
using FaceMatrix = Eigen::Matrix<int, Eigen::Dynamic, 3, Eigen::RowMajor>;

/// Squared distance from `p` to the triangle whose corners are the nine doubles at `t`
/// (Ericson, Real-Time Collision Detection, 5.1.5).
inline double PointTriangleSquared(const double* p, const double* t)
{
    const double* a = t;
    const double* b = t + 3;
    const double* c = t + 6;
    auto dot = [](const double* u, const double* v) { return u[0] * v[0] + u[1] * v[1] + u[2] * v[2]; };
    auto squared = [p](const double* q) {
        const double x = p[0] - q[0], y = p[1] - q[1], z = p[2] - q[2];
        return x * x + y * y + z * z;
    };

    double ab[3], ac[3], ap[3], bp[3], cp[3];
    for (int i = 0; i < 3; ++i) {
        ab[i] = b[i] - a[i];
        ac[i] = c[i] - a[i];
        ap[i] = p[i] - a[i];
        bp[i] = p[i] - b[i];
        cp[i] = p[i] - c[i];
    }

    const double d1 = dot(ab, ap), d2 = dot(ac, ap);
    if (d1 <= 0 && d2 <= 0) return squared(a);

    const double d3 = dot(ab, bp), d4 = dot(ac, bp);
    if (d3 >= 0 && d4 <= d3) return squared(b);

    const double vc = d1 * d4 - d3 * d2;
    if (vc <= 0 && d1 >= 0 && d3 <= 0) {
        const double v = d1 / (d1 - d3);
        const double q[3] = {a[0] + v * ab[0], a[1] + v * ab[1], a[2] + v * ab[2]};
        return squared(q);
    }

    const double d5 = dot(ab, cp), d6 = dot(ac, cp);
    if (d6 >= 0 && d5 <= d6) return squared(c);

    const double vb = d5 * d2 - d1 * d6;
    if (vb <= 0 && d2 >= 0 && d6 <= 0) {
        const double w = d2 / (d2 - d6);
        const double q[3] = {a[0] + w * ac[0], a[1] + w * ac[1], a[2] + w * ac[2]};
        return squared(q);
    }

    const double va = d3 * d6 - d5 * d4;
    if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) {
        const double w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
        const double q[3] = {b[0] + w * (c[0] - b[0]), b[1] + w * (c[1] - b[1]), b[2] + w * (c[2] - b[2])};
        return squared(q);
    }

    const double denominator = 1.0 / (va + vb + vc);
    const double v = vb * denominator, w = vc * denominator;
    const double q[3] = {a[0] + ab[0] * v + ac[0] * w, a[1] + ab[1] * v + ac[1] * w, a[2] + ab[2] * v + ac[2] * w};
    return squared(q);
}

/*
 * The nearest triangle within a bound, and nothing else - all an offset ever asks of the surface.
 *
 * libigl's tree answers the same question, but each of its nodes is a separate allocation and it
 * reaches a triangle's corners through the face and vertex matrices. Here the nodes are one array
 * and each triangle's nine coordinates are copied out in leaf order, so a query walks memory
 * forwards. Measured on the bench meshes: built in a quarter of the time, an offset's sampling
 * 23-31% faster, and the meshes it produced identical to the bit.
 */
class DistanceTree {
public:
    bool Empty() const { return nodes_.empty(); }

    template <typename DV, typename DF>
    void Build(const DV& V, const DF& F)
    {
        const int count = static_cast<int>(F.rows());
        std::vector<int> order(count);
        std::vector<double> centroid(static_cast<size_t>(count) * 3);
        for (int t = 0; t < count; ++t) {
            order[t] = t;
            for (int a = 0; a < 3; ++a) {
                centroid[static_cast<size_t>(t) * 3 + a] = (V(F(t, 0), a) + V(F(t, 1), a) + V(F(t, 2), a)) / 3.0;
            }
        }

        nodes_.clear();
        nodes_.reserve(static_cast<size_t>(count) / 2 + 8);
        nodes_.emplace_back();
        BuildNode(0, 0, count, order, centroid, V, F);

        corners_.resize(static_cast<size_t>(count) * 9);
        for (int s = 0; s < count; ++s) {
            const int t = order[s];
            for (int c = 0; c < 3; ++c) {
                for (int a = 0; a < 3; ++a) corners_[static_cast<size_t>(s) * 9 + c * 3 + a] = V(F(t, c), a);
            }
        }
    }

    /// Squared distance to the nearest triangle closer than sqrt(bound2), or bound2 when none is;
    /// `found` says which.
    double Query(const double* p, double bound2, bool& found) const
    {
        found = false;
        double best = bound2;
        int stack[128];
        int top = 0;
        if (BoxSquared(nodes_[0], p) < best) stack[top++] = 0;

        while (top > 0) {
            const Node& node = nodes_[stack[--top]];
            if (BoxSquared(node, p) >= best) continue;

            if (node.count > 0) {
                for (int s = node.first; s < node.first + node.count; ++s) {
                    const double d = PointTriangleSquared(p, &corners_[static_cast<size_t>(s) * 9]);
                    if (d < best) {
                        best = d;
                        found = true;
                    }
                }
                continue;
            }

            // Nearer child last, so it is searched first and tightens the bound for the other.
            const int left = node.first, right = node.first + 1;
            const double dl = BoxSquared(nodes_[left], p), dr = BoxSquared(nodes_[right], p);
            const int nearer = dl <= dr ? left : right, farther = dl <= dr ? right : left;
            if (std::max(dl, dr) < best) stack[top++] = farther;
            if (std::min(dl, dr) < best) stack[top++] = nearer;
        }
        return best;
    }

private:
    static constexpr int LeafSize = 4;

    /// A leaf holds `count` triangles from `first`; an inner node has count 0 and its children
    /// at `first` and `first + 1`.
    struct Node {
        double lo[3], hi[3];
        int first = 0;
        int count = 0;
    };

    static double BoxSquared(const Node& node, const double* p)
    {
        double d = 0;
        for (int a = 0; a < 3; ++a) {
            const double e = p[a] < node.lo[a] ? node.lo[a] - p[a] : (p[a] > node.hi[a] ? p[a] - node.hi[a] : 0.0);
            d += e * e;
        }
        return d;
    }

    template <typename DV, typename DF>
    void BuildNode(int index, int begin, int end, std::vector<int>& order, const std::vector<double>& centroid,
                   const DV& V, const DF& F)
    {
        Node node;
        double clo[3], chi[3];
        for (int a = 0; a < 3; ++a) {
            node.lo[a] = clo[a] = std::numeric_limits<double>::infinity();
            node.hi[a] = chi[a] = -std::numeric_limits<double>::infinity();
        }
        for (int s = begin; s < end; ++s) {
            const int t = order[s];
            for (int c = 0; c < 3; ++c) {
                for (int a = 0; a < 3; ++a) {
                    node.lo[a] = std::min(node.lo[a], V(F(t, c), a));
                    node.hi[a] = std::max(node.hi[a], V(F(t, c), a));
                }
            }
            for (int a = 0; a < 3; ++a) {
                clo[a] = std::min(clo[a], centroid[static_cast<size_t>(t) * 3 + a]);
                chi[a] = std::max(chi[a], centroid[static_cast<size_t>(t) * 3 + a]);
            }
        }

        if (end - begin <= LeafSize) {
            node.first = begin;
            node.count = end - begin;
            nodes_[index] = node;
            return;
        }

        // Halve at the median centroid along the widest spread; children go in as a pair.
        int axis = 0;
        for (int a = 1; a < 3; ++a) {
            if (chi[a] - clo[a] > chi[axis] - clo[axis]) axis = a;
        }
        const int middle = begin + (end - begin) / 2;
        std::nth_element(order.begin() + begin, order.begin() + middle, order.begin() + end, [&](int x, int y) {
            return centroid[static_cast<size_t>(x) * 3 + axis] < centroid[static_cast<size_t>(y) * 3 + axis];
        });

        node.first = static_cast<int>(nodes_.size());
        nodes_.emplace_back();
        nodes_.emplace_back();
        nodes_[index] = node;
        BuildNode(node.first, begin, middle, order, centroid, V, F);
        BuildNode(node.first + 1, middle, end, order, centroid, V, F);
    }

    std::vector<Node> nodes_;
    std::vector<double> corners_;
};

/*
 * A mesh with everything needed to answer distance, closest-point and ray queries against it.
 *
 * The precomputation lives here rather than being rebuilt per query, and every member is
 * read-only once built - which is what makes the queries safe to call from the several threads
 * Manifold meshes on. A field for batch queries and rays carries libigl's tree; a field for an
 * offset carries the flat DistanceTree instead, since that is the only question an offset asks.
 */
struct GeField {
    VertexMatrix V;
    FaceMatrix F;
    igl::AABB<VertexMatrix, 3> tree;
    DistanceTree distanceTree;
    igl::FastWindingNumberBVH windingBvh;

    /// The winding number's multipole expansion order. Until this had a name of its own the
    /// accuracy below was passed in its place, cast to int - the same 2, so nothing changed.
    static constexpr int WindingExpansionOrder = 2;

    /// Barnes-Hut accuracy for the winding number. libigl recommends 2; the sign only has to be
    /// right, and it is not close to the 0.5 threshold anywhere the magnitude matters.
    static constexpr float WindingAccuracy = 2.0f;

    /// The same, for samples beyond an offset's band, where only a clear inside or outside is asked.
    static constexpr float FarWindingAccuracy = 1.0f;

    double ClosestPoint(const Eigen::RowVector3d& point, int& face, Eigen::RowVector3d& closest) const
    {
        return std::sqrt(tree.squared_distance(V, F, point, face, closest));
    }

    /*
     * Signed distance to the surface, negative inside.
     *
     * The magnitude always comes from the AABB tree, which gives the exact closest-point
     * distance; only the sign is asked of the winding number. Neither of libigl's own
     * signed_distance helpers can stand in for that split:
     *
     *   signed_distance_fast_winding_number returns sqrt(d^2) * (1 - 2|w|) - the distance
     *   *scaled* by the winding number rather than merely signed by it. Away from 0 and 1 the
     *   magnitude is pulled towards zero, so an isosurface asked for at 1mm lands short of it:
     *   measured on a bolus, up to 0.1mm short, a tenth of the offset.
     *
     *   signed_distance_pseudonormal returns a true distance, but its sign disagreed with the
     *   winding number on roughly one sample in three hundred of the same bolus - enough
     *   scattered flips to grow an erode-dilate by 2mm.
     */
    double SignedDistance(const Eigen::RowVector3d& point) const
    {
        int face = -1;
        Eigen::RowVector3d closest;
        const double distance = ClosestPoint(point, face, closest);

        // 0.5 is the surface: below it the point is outside, above it enclosed.
        const float winding = igl::fast_winding_number(
            windingBvh, WindingAccuracy, point.cast<float>().eval());

        return winding > 0.5f ? -distance : distance;
    }

    /*
     * Signed distance clamped to +/- `band`: exact inside the band, and only the sign outside it.
     *
     * A level set asks for the field on a whole grid, and nearly all of it lies far from the
     * surface being meshed, where the mesher only needs to know which side of the level a sample
     * is on. Bounding the closest-point search by the band lets the tree discard everything
     * beyond it without descending - the search, not the sign, is most of a query's cost - so
     * the far samples come almost for free. Inside the band, which is all the mesher interpolates
     * across, the value is identical to SignedDistance.
     */
    double BandedSignedDistance(const Eigen::RowVector3d& point, double band) const
    {
        bool inBand = false;
        double squared;
        if (!distanceTree.Empty()) {
            const double p[3] = {point(0), point(1), point(2)};
            squared = distanceTree.Query(p, band * band, inBand);
        } else {
            int face = -1;
            Eigen::RowVector3d closest;
            squared = tree.squared_distance(V, F, point, band * band, face, closest);
            inBand = face >= 0;
        }
        const double distance = inBand ? std::min(std::sqrt(squared), band) : band;

        // Beyond the band the winding number sits near 0 or 1, nowhere near the 0.5 that decides
        // the sign, so a coarser Barnes-Hut approximation answers it just as well for less work.
        const float winding = igl::fast_winding_number(
            windingBvh, inBand ? WindingAccuracy : FarWindingAccuracy, point.cast<float>().eval());

        return winding > 0.5f ? -distance : distance;
    }
};

/// Context handed through the level-set callback.
struct BandedField {
    const GeField* field;
    double band;
};

namespace {

/// Runs `body` over [0, count) across the hardware's threads. The shape here is only ever a
/// flat range, so a work-stealing scheduler would buy nothing over chunked atomics.
template <typename Body>
void ParallelFor(size_t count, const Body& body)
{
    unsigned int threads = std::thread::hardware_concurrency();
    if (threads == 0) threads = 1;

    // Below this the thread handoff costs more than the work.
    if (count < 1024 || threads == 1) {
        for (size_t i = 0; i < count; ++i) body(i);
        return;
    }

    std::atomic<size_t> next{0};
    const size_t chunk = 256;

    std::vector<std::thread> workers;
    workers.reserve(threads);

    for (unsigned int t = 0; t < threads; ++t) {
        workers.emplace_back([&] {
            for (;;) {
                size_t start = next.fetch_add(chunk);
                if (start >= count) return;

                size_t end = start + chunk;
                if (end > count) end = count;
                for (size_t i = start; i < end; ++i) body(i);
            }
        });
    }

    for (auto& worker : workers) worker.join();
}

/// Runs a body that may throw, turning any C++ exception into a status. Nothing may cross the
/// C boundary as an exception: the managed caller cannot catch it, and the runtime aborts.
template <typename Body>
int32_t Guarded(const Body& body)
{
    try {
        return body();
    } catch (const std::bad_alloc&) {
        return GE_ERROR_EXCEPTION;
    } catch (const std::exception&) {
        return GE_ERROR_EXCEPTION;
    } catch (...) {
        return GE_ERROR_EXCEPTION;
    }
}

/*
 * libigl starts a thread per hardware thread for every parallel loop in the winding number's
 * build, and its recursive build nests them. Measured on a 20-thread machine, building it for an
 * 81k-triangle mesh took 58 ms with all of them and 20 ms with four (26-28 ms with 1, 2 or 8).
 * libigl fixes its count on its first parallel loop, so this runs before any.
 */
void LimitLibiglThreads()
{
    static std::once_flag once;
    std::call_once(once, [] {
        const unsigned int hardware = std::thread::hardware_concurrency();
        igl::default_num_threads(std::max(1u, std::min(4u, hardware)));
    });
}

/// What a field is built to answer: batch queries and rays, which need libigl's tree, or an
/// offset, which needs only distances and is served by the flat DistanceTree.
enum class FieldUse { Queries, Offset };

GeField* CreateField(
    const double*  vertices,
    size_t         vertex_count,
    const int32_t* triangles,
    size_t         triangle_count,
    FieldUse       use)
{
    if (vertices == nullptr || triangles == nullptr) return nullptr;
    if (vertex_count == 0 || triangle_count == 0) return nullptr;

    LimitLibiglThreads();

    try {
        auto field = std::make_unique<GeField>();

        field->V.resize(static_cast<Eigen::Index>(vertex_count), 3);
        for (size_t i = 0; i < vertex_count; ++i) {
            field->V(static_cast<Eigen::Index>(i), 0) = vertices[i * 3];
            field->V(static_cast<Eigen::Index>(i), 1) = vertices[i * 3 + 1];
            field->V(static_cast<Eigen::Index>(i), 2) = vertices[i * 3 + 2];
        }

        field->F.resize(static_cast<Eigen::Index>(triangle_count), 3);
        for (size_t i = 0; i < triangle_count; ++i) {
            for (int c = 0; c < 3; ++c) {
                int32_t index = triangles[i * 3 + c];
                if (index < 0 || static_cast<size_t>(index) >= vertex_count) return nullptr;
                field->F(static_cast<Eigen::Index>(i), c) = index;
            }
        }

        // Both structures are built up front and read-only afterwards: the level set asks for
        // several hundred thousand queries, so anything rebuilt per query would dominate. They are
        // independent of each other, so the winding number's is built on a second thread.
        std::exception_ptr windingFailure;
        std::thread winding([&] {
            try {
                igl::fast_winding_number(field->V, field->F, GeField::WindingExpansionOrder, field->windingBvh);
            } catch (...) {
                windingFailure = std::current_exception();
            }
        });

        try {
            if (use == FieldUse::Offset) {
                field->distanceTree.Build(field->V, field->F);
            } else {
                field->tree.init(field->V, field->F);
            }
        } catch (...) {
            winding.join();
            throw;
        }

        winding.join();
        if (windingFailure) std::rethrow_exception(windingFailure);

        return field.release();
    } catch (...) {
        return nullptr;
    }
}

} // namespace

extern "C" {

int32_t ge_abi_version(void)
{
    return GEOMETRYENGINE_NATIVE_ABI_VERSION;
}

void ge_mesh_free(GeMesh* mesh)
{
    if (mesh == nullptr) return;

    delete[] mesh->vertices;
    delete[] mesh->triangles;
    mesh->vertices = nullptr;
    mesh->triangles = nullptr;
    mesh->vertex_count = 0;
    mesh->triangle_count = 0;
}

GeField* ge_field_create(
    const double*  vertices,
    size_t         vertex_count,
    const int32_t* triangles,
    size_t         triangle_count)
{
    return CreateField(vertices, vertex_count, triangles, triangle_count, FieldUse::Queries);
}

void ge_field_destroy(GeField* field)
{
    delete field;
}

int32_t ge_field_signed_distance(
    const GeField* field,
    const double*  points,
    size_t         count,
    double*        out)
{
    if (field == nullptr || points == nullptr || out == nullptr) return GE_ERROR_INVALID_ARGUMENT;
    if (count == 0) return GE_OK;

    return Guarded([&] {
        ParallelFor(count, [&](size_t i) {
            Eigen::RowVector3d point(points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
            out[i] = field->SignedDistance(point);
        });
        return static_cast<int32_t>(GE_OK);
    });
}

int32_t ge_field_closest_point(
    const GeField* field,
    const double*  points,
    size_t         count,
    double*        out_points,
    int32_t*       out_triangles,
    double*        out_distances)
{
    if (field == nullptr || points == nullptr) return GE_ERROR_INVALID_ARGUMENT;
    if (count == 0) return GE_OK;

    return Guarded([&] {
        ParallelFor(count, [&](size_t i) {
            Eigen::RowVector3d point(points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
            Eigen::RowVector3d closest;
            int face = -1;
            const double distance = field->ClosestPoint(point, face, closest);

            if (out_points != nullptr) {
                out_points[i * 3] = closest(0);
                out_points[i * 3 + 1] = closest(1);
                out_points[i * 3 + 2] = closest(2);
            }
            if (out_triangles != nullptr) out_triangles[i] = face;
            if (out_distances != nullptr) out_distances[i] = distance;
        });
        return static_cast<int32_t>(GE_OK);
    });
}

int32_t ge_field_raycast(
    const GeField* field,
    const double*  origins,
    const double*  directions,
    size_t         count,
    double*        out_distances,
    int32_t*       out_triangles)
{
    if (field == nullptr || origins == nullptr || directions == nullptr ||
        out_distances == nullptr || out_triangles == nullptr) {
        return GE_ERROR_INVALID_ARGUMENT;
    }
    if (count == 0) return GE_OK;

    return Guarded([&] {
        ParallelFor(count, [&](size_t i) {
            Eigen::RowVector3d origin(origins[i * 3], origins[i * 3 + 1], origins[i * 3 + 2]);
            Eigen::RowVector3d direction(directions[i * 3], directions[i * 3 + 1], directions[i * 3 + 2]);

            igl::Hit hit;
            if (field->tree.intersect_ray(field->V, field->F, origin, direction, hit) && hit.t > 0) {
                out_distances[i] = static_cast<double>(hit.t);
                out_triangles[i] = hit.id;
            } else {
                out_distances[i] = -1.0;
                out_triangles[i] = -1;
            }
        });
        return static_cast<int32_t>(GE_OK);
    });
}

int32_t ge_offset(
    const double*  vertices,
    size_t         vertex_count,
    const int32_t* triangles,
    size_t         triangle_count,
    double         offset_distance,
    const double*  bounds,
    double         edge_length,
    GeMesh*        out)
{
    if (out == nullptr || bounds == nullptr) return GE_ERROR_INVALID_ARGUMENT;
    if (!(edge_length > 0)) return GE_ERROR_INVALID_ARGUMENT;

    std::memset(out, 0, sizeof(*out));

    const auto& manifold = geometryengine::Manifold();
    if (!manifold.loaded) return GE_ERROR_MANIFOLD_UNAVAILABLE;

    std::unique_ptr<GeField, void (*)(GeField*)> field(
        CreateField(vertices, vertex_count, triangles, triangle_count, FieldUse::Offset),
        ge_field_destroy);

    if (!field) return GE_ERROR_EMPTY_MESH;

    void* box = nullptr;
    void* solid = nullptr;
    void* meshGl = nullptr;

    int32_t status = Guarded([&] {
        box = manifold.alloc_box();
        manifold.box(box, bounds[0], bounds[1], bounds[2], bounds[3], bounds[4], bounds[5]);

        // Wide enough that every grid edge crossing the level has both ends' values exact: the
        // offset itself, plus a few cells of margin for the mesher's interpolation and refinement.
        BandedField banded{field.get(), std::abs(offset_distance) + 4.0 * edge_length};

        // Manifold keeps the region where the field is above the level, so the field is handed
        // over negated - positive inside the solid - and the surface `offset_distance` outside
        // sits at the level of the same magnitude, negated to match.
        auto sample = [](double x, double y, double z, void* context) -> double {
            const auto* banded = static_cast<const BandedField*>(context);
            return -banded->field->BandedSignedDistance(Eigen::RowVector3d(x, y, z), banded->band);
        };

        // The parallel mesher. Upstream it is not repeatable - it numbers vertices in whatever
        // order its threads reach them, and its topology cleanup then resolves some offsets into
        // different geometry - but the manifold.dll this library ships with is built with a patch
        // that renumbers its output before cleanup (runtimes/README.md), so the same field gives the
        // same mesh on every call, the same as the sequential mesher does.
        solid = manifold.alloc_manifold();
        manifold.level_set(solid, sample, box, edge_length, -offset_distance, 0, &banded);

        if (manifold.status(solid) != 0 || manifold.num_tri(solid) == 0) {
            return static_cast<int32_t>(GE_ERROR_LEVEL_SET_FAILED);
        }

        meshGl = manifold.alloc_meshgl64();
        manifold.get_meshgl64(meshGl, solid);

        const size_t vertexCount = manifold.meshgl64_num_vert(meshGl);
        const size_t triCount = manifold.meshgl64_num_tri(meshGl);
        const size_t stride = manifold.meshgl64_num_prop(meshGl);
        const size_t propsLength = manifold.meshgl64_vert_properties_length(meshGl);
        const size_t triLength = manifold.meshgl64_tri_length(meshGl);

        // The position is only the first three of however many properties a vertex carries,
        // and the two lengths are cross-checked so a disagreement is reported rather than read
        // past the end of the buffer.
        if (vertexCount == 0 || triCount == 0 || stride < 3 ||
            propsLength != vertexCount * stride || triLength != triCount * 3) {
            return static_cast<int32_t>(GE_ERROR_LEVEL_SET_FAILED);
        }

        std::vector<double> props(propsLength);
        std::vector<uint64_t> tris(triLength);
        manifold.meshgl64_vert_properties(props.data(), meshGl);
        manifold.meshgl64_tri_verts(tris.data(), meshGl);

        // A position Manifold split into several vertices is stitched back through the merge
        // vectors, then vertices left unreferenced are dropped - otherwise the seam reads as a
        // ring of boundary edges to anything that pairs edges by index.
        const size_t mergeLength = manifold.meshgl64_merge_length(meshGl);
        std::vector<size_t> remap(vertexCount);
        for (size_t i = 0; i < vertexCount; ++i) remap[i] = i;
        if (mergeLength > 0) {
            std::vector<uint64_t> from(mergeLength), to(mergeLength);
            manifold.meshgl64_merge_from_vert(from.data(), meshGl);
            manifold.meshgl64_merge_to_vert(to.data(), meshGl);
            for (size_t i = 0; i < mergeLength; ++i) {
                if (from[i] >= vertexCount || to[i] >= vertexCount) {
                    return static_cast<int32_t>(GE_ERROR_LEVEL_SET_FAILED);
                }
                remap[from[i]] = static_cast<size_t>(to[i]);
            }
        }

        std::vector<int64_t> compact(vertexCount, -1);
        std::vector<size_t> kept;
        kept.reserve(vertexCount);
        std::vector<int32_t> indices(triLength);
        for (size_t i = 0; i < triLength; ++i) {
            const size_t v = remap[tris[i]];
            if (compact[v] < 0) {
                compact[v] = static_cast<int64_t>(kept.size());
                kept.push_back(v);
            }
            indices[i] = static_cast<int32_t>(compact[v]);
        }

        out->vertices = new double[kept.size() * 3];
        out->triangles = new int32_t[triLength];
        out->vertex_count = kept.size();
        out->triangle_count = triCount;

        for (size_t i = 0; i < kept.size(); ++i) {
            out->vertices[i * 3] = props[kept[i] * stride];
            out->vertices[i * 3 + 1] = props[kept[i] * stride + 1];
            out->vertices[i * 3 + 2] = props[kept[i] * stride + 2];
        }
        std::copy(indices.begin(), indices.end(), out->triangles);

        return static_cast<int32_t>(GE_OK);
    });

    if (status != GE_OK) ge_mesh_free(out);

    if (meshGl != nullptr) manifold.delete_meshgl64(meshGl);
    if (solid != nullptr) manifold.delete_manifold(solid);
    if (box != nullptr) manifold.delete_box(box);

    return status;
}

} // extern "C"
