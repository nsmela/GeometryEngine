import sys
from pathlib import Path

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

sys.path.insert(0, str(Path(__file__).parent))
from stlview import load_stl, render

OUT = Path(sys.argv[1] if len(sys.argv) > 1 else "out")
FIG = OUT / "figures"
FIG.mkdir(parents=True, exist_ok=True)

INK = "#1c1b19"
PAPER = "#f4f1ea"
STEEL = (0.62, 0.66, 0.72)
BRASS = (0.86, 0.62, 0.26)
SLATE = (0.45, 0.58, 0.68)


def panel(ax, stl, title, subtitle, colour, azimuth=38, elevation=26, edges=False):
    tris = load_stl(OUT / stl)
    drawn, _ = render(ax, tris, azimuth=azimuth, elevation=elevation, base_rgb=colour, edges=edges)
    ax.set_title(title, fontsize=13, color=INK, pad=6, fontweight="bold")
    ax.text(0.5, -0.015, subtitle, transform=ax.transAxes, ha="center", va="top",
            fontsize=9.5, color="#6a655c", family="monospace")
    return drawn


# ---------------------------------------------------------------- figure one
fig, axes = plt.subplots(1, 4, figsize=(16, 4.8), facecolor=PAPER)
fig.suptitle("GeometryEngine  \u00b7  boolean operations on two overlapping spheres (r = 1, centres 0.9 apart)",
             fontsize=15, color=INK, fontweight="bold", y=0.98)

panel(axes[0], "03-union-spheres.stl", "A \u222a B  union", "15 954 tris  \u00b7  vol 6.7647", BRASS)
panel(axes[1], "04-intersect-spheres.stl", "A \u2229 B  intersection", "1 096 tris  \u00b7  vol 1.5271", BRASS)
panel(axes[2], "05-subtract-spheres.stl", "A \u2212 B  difference", "10 948 tris  \u00b7  vol 2.6188", BRASS, azimuth=32, elevation=24)
panel(axes[3], "05-subtract-spheres.stl", "A \u2212 B  looking into the cut", "concave crater, still watertight", BRASS, azimuth=0, elevation=2)

for ax in axes:
    ax.set_facecolor(PAPER)
fig.tight_layout(rect=[0, 0.06, 1, 0.94])
fig.savefig(FIG / "booleans-spheres.png", dpi=150, facecolor=PAPER)
plt.close(fig)
print("wrote booleans-spheres.png")

# ---------------------------------------------------------------- figure two
fig, axes = plt.subplots(1, 4, figsize=(17, 5.0), facecolor=PAPER)
fig.suptitle("GeometryEngine  \u00b7  chained operations", fontsize=15, color=INK, fontweight="bold", y=0.98)

panel(axes[0], "01-operand-cube.stl", "box", "12 tris  \u00b7  vol 8.0000", SLATE)
panel(axes[1], "06-rounded-cube.stl", "box \u2229 sphere", "1 880 tris  \u00b7  vol 7.2729", SLATE)
panel(axes[2], "07-csg-showcase.stl", "\u2212 three bores", "58 898 tris  \u00b7  vol 3.3533", BRASS)
panel(axes[3], "07-csg-showcase.stl", "same, wireframe", "every result edge-paired", BRASS, azimuth=125, edges=True)

for ax in axes:
    ax.set_facecolor(PAPER)
fig.tight_layout(rect=[0, 0.06, 1, 0.94])
fig.savefig(FIG / "booleans-showcase.png", dpi=150, facecolor=PAPER)
plt.close(fig)
print("wrote booleans-showcase.png")

# -------------------------------------------------------------- figure three
fig, axes = plt.subplots(1, 3, figsize=(14, 5.0), facecolor=PAPER)
fig.suptitle("GeometryEngine  \u00b7  a part built the way a CAD user would: add material, then remove it",
             fontsize=14, color=INK, fontweight="bold", y=0.97)

panel(axes[0], "08-flanged-plate.stl", "flanged plate", "plate \u222a boss \u2212 bore \u2212 4 holes", STEEL, azimuth=42, elevation=30)
panel(axes[1], "08-flanged-plate.stl", "from below", "through-holes are open both ends", STEEL, azimuth=42, elevation=-32)
panel(axes[2], "08-flanged-plate.stl", "edge on", "27 992 tris  \u00b7  vol 8.5175", STEEL, azimuth=10, elevation=4)

for ax in axes:
    ax.set_facecolor(PAPER)
fig.tight_layout(rect=[0, 0.06, 1, 0.93])
fig.savefig(FIG / "flanged-plate.png", dpi=150, facecolor=PAPER)
plt.close(fig)
print("wrote flanged-plate.png")
