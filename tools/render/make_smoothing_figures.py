"""Compare the two smoothing modifiers, with the surface coloured by how far it moved.

Reads what `dotnet run -c Release --project bench/GeometryEngine.Benchmarks -- smoothfigures <dir>`
writes into <dir>: for each mesh an `-original.stl`, a `-fairing.stl` and a `-closing.stl`, each
smoothed variant beside a `.dev` file holding one signed deviation per facet, in millimetres,
measured against the original surface.

Red is surface that moved *inside* the original, blue is surface that moved *outside*, and the
scale saturates at one millimetre either way. Near-white is surface that barely moved.

    python3 tools/render/make_smoothing_figures.py <dir> [<out-dir>] [<clamp-mm>]

The clamp defaults to 1.0 mm. Both modifiers, at the settings used here, keep well inside that,
so the 1 mm figures come out pale on purpose - that is the result. Pass something like 0.25 to
rescale and see where the movement actually is.
"""
import sys
from pathlib import Path

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.colors import LinearSegmentedColormap, Normalize
from matplotlib.cm import ScalarMappable

sys.path.insert(0, str(Path(__file__).parent))
from stlview import load_stl, render

DATA = Path(sys.argv[1] if len(sys.argv) > 1 else "out")
OUT = Path(sys.argv[2] if len(sys.argv) > 2 else DATA)
OUT.mkdir(parents=True, exist_ok=True)

INK = "#1c1b19"
PAPER = "#f4f1ea"
NEUTRAL = (0.70, 0.71, 0.74)
CLAMP = float(sys.argv[3]) if len(sys.argv) > 3 else 1.0  # mm at which the scale saturates
SUFFIX = "" if CLAMP == 1.0 else f"-{CLAMP:g}mm"

# Red inside, blue outside, and a near-white middle so "did not move" reads as absence of
# colour rather than as a third hue competing with the two that matter.
HEAT = LinearSegmentedColormap.from_list(
    "deviation",
    ["#8c1116", "#c62026", "#e8918f", "#f6f4f2", "#8fa8e0", "#224ec6", "#12307e"],
)
NORM = Normalize(vmin=-CLAMP, vmax=CLAMP)


def deviation_colours(path, facets):
    dev = np.loadtxt(path, dtype=float, ndmin=1)
    if dev.shape[0] != facets:
        raise SystemExit(f"{path}: {dev.shape[0]} deviations for {facets} facets")

    return HEAT(NORM(dev))[:, :3], dev


def summarise(dev):
    """One line, for the console."""
    return (f"mean |dev| {np.abs(dev).mean():.3f} mm   "
            f"worst in {dev.min():+.2f}   worst out {dev.max():+.2f}   "
            f"{100.0 * (dev < 0).mean():.0f}% inside")


def caption_lines(facets, dev):
    """Short lines, because a panel caption wider than its panel runs into its neighbour."""
    return (f"{facets} facets\n"
            f"mean |dev| {np.abs(dev).mean():.3f} mm\n"
            f"worst {dev.min():+.2f} / {dev.max():+.2f} mm\n"
            f"{100.0 * (dev < 0).mean():.0f}% inside the original")


def figure(name):
    original = load_stl(DATA / f"{name}-original.stl")
    fairing = load_stl(DATA / f"{name}-fairing.stl")
    closing = load_stl(DATA / f"{name}-closing.stl")

    edges = load_stl(DATA / f"{name}-edges.stl")

    fairing_rgb, fairing_dev = deviation_colours(DATA / f"{name}-fairing.dev", len(fairing))
    closing_rgb, closing_dev = deviation_colours(DATA / f"{name}-closing.dev", len(closing))
    edges_rgb, edges_dev = deviation_colours(DATA / f"{name}-edges.dev", len(edges))

    fig, axes = plt.subplots(1, 4, figsize=(19.2, 6.0), facecolor=PAPER)
    fig.subplots_adjust(top=0.70, bottom=0.15, left=0.02, right=0.98, wspace=0.04)
    fig.suptitle(
        f"{name}  ·  LaplacianSmooth (10 λ|μ pairs)  ·  OffsetSmooth (2 mm closing)  ·  "
        "SmoothEdges (30°)",
        fontsize=14, color=INK, fontweight="bold", y=0.975)

    # Captions go above the meshes: the bottom of the figure belongs to the colour bar, and a
    # two-line caption underneath a panel collides with it.
    def caption(ax, title, detail):
        ax.set_title(title, fontsize=12, color=INK, pad=60, fontweight="bold")
        ax.text(0.5, 1.012, detail, transform=ax.transAxes, ha="center", va="bottom",
                fontsize=8.4, color="#6a655c", family="monospace", linespacing=1.45)

    # One camera for all three panels: fitted to the original and handed on, so a feature
    # lands on the same pixel in each and the panels can be read against each other.
    _, limits = render(axes[0], original, base_rgb=NEUTRAL)
    caption(axes[0], "original", f"{len(original)} facets\nunsmoothed reference")

    for ax, tris, rgb, dev, title in (
        (axes[1], fairing, fairing_rgb, fairing_dev, "LaplacianSmooth"),
        (axes[2], closing, closing_rgb, closing_dev, "OffsetSmooth"),
        (axes[3], edges, edges_rgb, edges_dev, "SmoothEdges"),
    ):
        render(ax, tris, face_rgb=rgb, limits=limits)
        caption(ax, title, caption_lines(len(tris), dev))

    bar = fig.colorbar(
        ScalarMappable(norm=NORM, cmap=HEAT), ax=axes, orientation="horizontal",
        fraction=0.05, pad=0.04, aspect=48)
    bar.set_label(
        f"displacement from the original surface (mm), saturating at ±{CLAMP:g}"
        "   —   red: inside   blue: outside",
        fontsize=10, color=INK)
    bar.set_ticks([-CLAMP, -CLAMP / 2, 0, CLAMP / 2, CLAMP])
    bar.set_ticklabels([f"≤ −{CLAMP:g}", f"−{CLAMP / 2:g}", "0",
                        f"+{CLAMP / 2:g}", f"≥ +{CLAMP:g}"])
    bar.outline.set_visible(False)

    path = OUT / f"{name}-smoothing{SUFFIX}.png"
    fig.savefig(path, dpi=140, facecolor=PAPER)
    plt.close(fig)
    print(f"  {path}")
    print(f"    fairing: {summarise(fairing_dev)}")
    print(f"    closing: {summarise(closing_dev)}")
    print(f"    edges  : {summarise(edges_dev)}")


names = sorted({p.name[: -len("-original.stl")] for p in DATA.glob("*-original.stl")})
if not names:
    raise SystemExit(f"no *-original.stl in {DATA}; run the smoothfigures bench mode first")

for name in names:
    print(name)
    figure(name)
