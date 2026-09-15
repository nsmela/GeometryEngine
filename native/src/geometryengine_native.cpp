#include "geometryengine_native.h"
#include "manifold_dynamic.hpp"

#include <igl/AABB.h>
#include <igl/Hit.h>
#include <igl/fast_winding_number.h>

#include <Eigen/Core>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <exception>
#include <memory>
#include <new>
#include <thread>
#include <vector>

/*
 * A mesh with everything needed to answer distance, closest-point and ray queries against it.
 *
 * The precomputation lives here rather than being rebuilt per query, and every member is
 * read-only once built - which is what makes the queries safe to call from the several threads
 * Manifold meshes on.
 */
struct GeField {
    Eigen::MatrixXd V;
    Eigen::MatrixXi F;
    igl::AABB<Eigen::MatrixXd, 3> tree;
    igl::FastWindingNumberBVH windingBvh;

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
        int face = -1;
        Eigen::RowVector3d closest;
        const double squared = tree.squared_distance(V, F, point, band * band, face, closest);
        const double distance = face < 0 ? band : std::min(std::sqrt(squared), band);

        // Beyond the band the winding number sits near 0 or 1, nowhere near the 0.5 that decides
        // the sign, so a coarser Barnes-Hut approximation answers it just as well for less work.
        const float winding = igl::fast_winding_number(
            windingBvh, face < 0 ? FarWindingAccuracy : WindingAccuracy, point.cast<float>().eval());

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
    if (vertices == nullptr || triangles == nullptr) return nullptr;
    if (vertex_count == 0 || triangle_count == 0) return nullptr;

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
        // several hundred thousand queries, so anything rebuilt per query would dominate.
        field->tree.init(field->V, field->F);
        igl::fast_winding_number(
            field->V, field->F, static_cast<int>(GeField::WindingAccuracy), field->windingBvh);

        return field.release();
    } catch (...) {
        return nullptr;
    }
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
        ge_field_create(vertices, vertex_count, triangles, triangle_count),
        ge_field_destroy);

    if (!field) return GE_ERROR_EMPTY_MESH;

    void* box = nullptr;
    void* solid = nullptr;
    void* meshGl = nullptr;

    int32_t status = Guarded([&] {
        box = manifold.alloc_box();
        manifold.box(box, bounds[0], bounds[1], bounds[2], bounds[3], bounds[4], bounds[5]);

        // Manifold keeps the region where the field is above the level, so the field is handed
        // over negated - positive inside the solid - and the surface `offset_distance` outside
        // sits at the level of the same magnitude, negated to match.
        auto sample = [](double x, double y, double z, void* context) -> double {
            const auto* banded = static_cast<const BandedField*>(context);
            return -banded->field->BandedSignedDistance(Eigen::RowVector3d(x, y, z), banded->band);
        };

        // Wide enough that every grid edge crossing the level has both ends' values exact: the
        // offset itself, plus a few cells of margin for the mesher's interpolation and refinement.
        BandedField context{field.get(), std::abs(offset_distance) + 4.0 * edge_length};

        solid = manifold.alloc_manifold();
        manifold.level_set(solid, sample, box, edge_length, -offset_distance, 0, &context);

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
