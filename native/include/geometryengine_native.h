/*
 * geometryengine_native - native distance-field support for GeometryEngine.
 *
 * Exists for one reason: Manifold's level-set mesher asks for a signed distance one point at a
 * time, and answering those from managed code costs a P/Invoke transition per sample. A bolus
 * offset is a few hundred thousand samples, so the transitions dominate - the field itself is
 * cheap by comparison. Everything here is shaped so the per-sample work never leaves native
 * code: ge_offset runs the whole offset, callback included, behind a single call, and the batch
 * queries answer every point of a request in one transition, in parallel.
 *
 * The distance magnitude comes from libigl's AABB tree and the sign from libigl's fast winding
 * number, which gives a sensible inside on meshes with holes and self-intersections - where a
 * pseudonormal test cannot - and imported patient scans are routinely neither closed nor clean.
 */
#ifndef GEOMETRYENGINE_NATIVE_H
#define GEOMETRYENGINE_NATIVE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  if defined(GEOMETRYENGINE_NATIVE_BUILD)
#    define GE_API __declspec(dllexport)
#  else
#    define GE_API __declspec(dllimport)
#  endif
#else
#  define GE_API __attribute__((visibility("default")))
#endif

typedef enum GeStatus {
    GE_OK = 0,
    GE_ERROR_INVALID_ARGUMENT = -1,
    GE_ERROR_EMPTY_MESH = -2,
    /* manifoldc could not be loaded, or lacks an entry point this needs. */
    GE_ERROR_MANIFOLD_UNAVAILABLE = -3,
    /* The level set ran but produced nothing, or Manifold rejected the result. */
    GE_ERROR_LEVEL_SET_FAILED = -4,
    GE_ERROR_EXCEPTION = -5
} GeStatus;

/* Bumped whenever the shape of anything below changes. The managed binding checks it on load,
   so a stale binary beside a new assembly is reported rather than crashed through. */
#define GEOMETRYENGINE_NATIVE_ABI_VERSION 1

GE_API int32_t ge_abi_version(void);

/* An owned triangle mesh. Free with ge_mesh_free. */
typedef struct GeMesh {
    double*  vertices;        /* xyz interleaved, 3 * vertex_count doubles */
    size_t   vertex_count;
    int32_t* triangles;       /* 3 * triangle_count indices */
    size_t   triangle_count;
} GeMesh;

GE_API void ge_mesh_free(GeMesh* mesh);

/* A mesh's distance structures, built once and queried many times. Immutable once built, so
   one field may be queried from several threads at once. */
typedef struct GeField GeField;

/*
 * Builds the acceleration structures for a mesh. Returns NULL on bad input.
 * The arrays are copied, so the caller may free them immediately.
 */
GE_API GeField* ge_field_create(
    const double*  vertices,
    size_t         vertex_count,
    const int32_t* triangles,
    size_t         triangle_count);

GE_API void ge_field_destroy(GeField* field);

/*
 * Signed distance for each of `count` points, negative inside the solid. Queries run in
 * parallel; `points` is xyz interleaved and `out` holds `count` results.
 */
GE_API int32_t ge_field_signed_distance(
    const GeField* field,
    const double*  points,
    size_t         count,
    double*        out);

/*
 * Closest surface point for each of `count` points: `out_points` receives xyz interleaved,
 * `out_triangles` the index of the triangle carrying it, `out_distances` the unsigned distance.
 * Any of the three outputs may be NULL when the caller has no use for it.
 */
GE_API int32_t ge_field_closest_point(
    const GeField* field,
    const double*  points,
    size_t         count,
    double*        out_points,
    int32_t*       out_triangles,
    double*        out_distances);

/*
 * Nearest hit along each of `count` rays: origins and unit directions are xyz interleaved.
 * `out_distances` receives the distance along the ray, or a negative value on a miss;
 * `out_triangles` the hit triangle, or -1.
 */
GE_API int32_t ge_field_raycast(
    const GeField* field,
    const double*  origins,
    const double*  directions,
    size_t         count,
    double*        out_distances,
    int32_t*       out_triangles);

/*
 * Re-meshes the surface at `offset_distance` from the input - positive outwards - by sampling a
 * distance field and handing it to Manifold's level-set mesher, whose output is a closed solid
 * by construction.
 *
 * `bounds` is the volume to sample, as min x/y/z then max x/y/z; it must already contain the
 * offset surface, since nothing outside it is meshed. `edge_length` is Manifold's target output
 * edge length, which drives both fidelity and cost.
 */
GE_API int32_t ge_offset(
    const double*  vertices,
    size_t         vertex_count,
    const int32_t* triangles,
    size_t         triangle_count,
    double         offset_distance,
    const double*  bounds,
    double         edge_length,
    GeMesh*        out);

#ifdef __cplusplus
}
#endif

#endif /* GEOMETRYENGINE_NATIVE_H */
