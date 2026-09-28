"""Render binary STL files produced by GeometryEngine into shaded PNG views.

Deliberately simple: orthographic projection, back-face culling, painter's
algorithm, Lambert shading with a fill light. No external 3D dependencies.
"""
import struct
import sys
from pathlib import Path

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import PolyCollection


def load_stl(path):
    data = Path(path).read_bytes()
    count = struct.unpack_from("<I", data, 80)[0]
    facets = np.frombuffer(data, dtype=np.uint8, count=count * 50, offset=84)
    facets = facets.reshape(count, 50)
    coords = facets[:, 12:48].copy().view(np.float32).reshape(count, 3, 3)
    return coords.astype(np.float64)


def view_matrix(azimuth_deg, elevation_deg):
    """Rows are screen x, screen y, and the direction from the scene to the camera,
    so a larger third component means nearer the viewer."""
    a = np.radians(azimuth_deg)
    e = np.radians(elevation_deg)
    to_camera = np.array([np.cos(e) * np.cos(a), np.cos(e) * np.sin(a), np.sin(e)])
    up_hint = np.array([0.0, 0.0, 1.0])
    rightv = np.cross(up_hint, to_camera)
    rightv /= np.linalg.norm(rightv)
    upv = np.cross(to_camera, rightv)
    return np.stack([rightv, upv, to_camera])


def shade(normals_cam, base_rgb):
    key = np.array([0.35, 0.45, 0.82])
    key /= np.linalg.norm(key)
    fill = np.array([-0.6, -0.2, 0.4])
    fill /= np.linalg.norm(fill)

    lambert = np.clip(normals_cam @ key, 0, 1)
    bounce = np.clip(normals_cam @ fill, 0, 1)
    intensity = 0.30 + 0.62 * lambert + 0.18 * bounce
    intensity = np.clip(intensity, 0, 1.25)[:, None]

    rgb = np.clip(np.array(base_rgb)[None, :] * intensity, 0, 1)
    return rgb


def shade_each(normals_cam, face_rgb, relief=1.0):
    """shade(), but with one base colour per facet rather than one for the whole mesh.

    relief scales how far the lighting is allowed to move the base colour, blending the
    shaded result back towards the flat one. A heatmap wants some relief to read as a
    surface, but not so much that shadow is mistaken for a lower value: relief below one
    keeps the colour near the one the legend promises.
    """
    key = np.array([0.35, 0.45, 0.82])
    key /= np.linalg.norm(key)
    fill = np.array([-0.6, -0.2, 0.4])
    fill /= np.linalg.norm(fill)

    lambert = np.clip(normals_cam @ key, 0, 1)
    bounce = np.clip(normals_cam @ fill, 0, 1)
    intensity = 0.30 + 0.62 * lambert + 0.18 * bounce
    intensity = np.clip(intensity, 0, 1.25)
    intensity = 1.0 + relief * (intensity - 1.0)
    intensity = intensity[:, None]

    return np.clip(np.asarray(face_rgb, dtype=float) * intensity, 0, 1)


def render(ax, triangles, azimuth=38, elevation=26, base_rgb=(0.82, 0.55, 0.32), edges=False,
           face_rgb=None, limits=None, relief=1.0):
    """Draw a facet soup.

    face_rgb, when given, is one (r, g, b) per input facet and replaces base_rgb, so a
    per-facet quantity can be carried into the shading - a deviation heatmap, say. It is
    filtered and reordered alongside the facets it belongs to.

    limits, when given, is the (cx, cy, half) framing to use instead of fitting this mesh,
    so several panels can share one camera and be compared pixel for pixel. render returns
    the framing it used, ready to pass to the next panel.

    relief is passed to shade_each and applies only alongside face_rgb.
    """
    m = view_matrix(azimuth, elevation)
    cam = triangles @ m.T                      # (n, 3, 3) in camera space

    tint = None if face_rgb is None else np.asarray(face_rgb, dtype=float)

    e1 = cam[:, 1] - cam[:, 0]
    e2 = cam[:, 2] - cam[:, 0]
    normals = np.cross(e1, e2)
    lengths = np.linalg.norm(normals, axis=1)
    keep = lengths > 1e-14
    cam, normals, lengths = cam[keep], normals[keep], lengths[keep]
    normals /= lengths[:, None]
    if tint is not None:
        tint = tint[keep]

    # A face is visible when its outward normal leans towards the camera.
    facing = normals[:, 2] > 0
    cam, normals = cam[facing], normals[facing]
    if tint is not None:
        tint = tint[facing]

    # Painter's algorithm: the furthest facets go down first.
    order = np.argsort(cam[:, :, 2].mean(axis=1))
    cam, normals = cam[order], normals[order]
    if tint is not None:
        tint = tint[order]

    colours = shade(normals, base_rgb) if tint is None else shade_each(normals, tint, relief)
    polys = cam[:, :, :2]

    # Drawing each facet with its own colour as the edge closes the hairline gaps
    # that antialiasing leaves between neighbouring triangles.
    collection = PolyCollection(
        polys,
        facecolors=colours,
        edgecolors=(0.12, 0.12, 0.14, 0.30) if edges else colours,
        linewidths=0.20 if edges else 0.35,
        antialiased=True,
    )
    ax.add_collection(collection)

    if limits is None:
        xs, ys = polys[:, :, 0], polys[:, :, 1]
        pad = 0.06 * max(np.ptp(xs), np.ptp(ys))
        cx, cy = (xs.min() + xs.max()) / 2, (ys.min() + ys.max()) / 2
        half = max(np.ptp(xs), np.ptp(ys)) / 2 + pad
    else:
        cx, cy, half = limits

    ax.set_xlim(cx - half, cx + half)
    ax.set_ylim(cy - half, cy + half)
    ax.set_aspect("equal")
    ax.axis("off")
    return len(cam), (cx, cy, half)
