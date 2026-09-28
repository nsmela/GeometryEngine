"""Build the smoothing comparison PDF: a summary page, then one page per model.

Each model page carries shaded renders of both smoothed meshes on one camera, three deviation
heatmaps against a shared per-model scale with a legend, and the metrics for that case.

    python3 tools/smoothing-comparison/make_report.py <run-dir> <out.pdf>

<run-dir> holds what the harnesses and the comparison wrote:

    input/<case>.stl                  the centred, unsmoothed mesh
    main/<case>.stl                   Fabolus main's smoothed mesh
    engine/<case>.stl                 GeometryEngine's smoothed mesh
    fields/<case>__<pair>.csv         one signed distance per facet, from deviation-field
    results/head_to_head.json         meshcompare batch output
    results/main-run.json             the reference harness's per-case record
    results/engine-run.json           the candidate harness's per-case record

Colours follow tools/render/make_smoothing_figures.py: red is surface lying inside the mesh it
is measured against, blue outside, near-white barely moved.
"""
import json
import sys
import textwrap
from pathlib import Path

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.backends.backend_pdf import PdfPages
from matplotlib.colors import LinearSegmentedColormap, Normalize
from matplotlib.cm import ScalarMappable
from matplotlib.font_manager import FontProperties

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "render"))
from stlview import load_stl, render

RUN = Path(sys.argv[1] if len(sys.argv) > 1 else "out")
OUT = Path(sys.argv[2] if len(sys.argv) > 2 else "smoothing-comparison.pdf")

PAGE = (11.69, 8.27)          # A4 landscape, in inches
DPI = 200                     # the rasterized mesh panels; everything else stays vector
PAPER = "#f4f1ea"
INK = "#1c1b19"
MUTED = "#6a655c"
RULE = "#c9c2b4"
GOOD = "#0b6b0b"
BAD = "#8c1116"
COL0, COL1 = 0.050, 0.950       # the page's left and right text margins

MAIN_RGB = (0.62, 0.64, 0.68)   # reference meshes, neutral grey
ENGINE_RGB = (0.82, 0.55, 0.32) # candidate meshes, the repo's usual amber

# Red inside, blue outside, near-white in the middle so "did not move" reads as absence of
# colour. Same ramp as make_smoothing_figures.py, so the two sets of figures can be read
# together; its poles clear every gate in the data-viz validator on this paper.
HEAT = LinearSegmentedColormap.from_list(
    "deviation",
    ["#8c1116", "#c62026", "#e8918f", "#f6f4f2", "#8fa8e0", "#224ec6", "#12307e"],
)

PAIRS = [
    ("main_vs_orig", "main vs original", "main"),
    ("engine_vs_orig", "engine vs original", "engine"),
    ("engine_vs_main", "engine vs main", "engine"),
]

CASES = ["chin_bolus", "ear_bolus", "eye_bolus", "larynx small", "larynx_bolus", "nose_bolus",
         "scalp_bolus", "small test", "sphere", "test bolus 107mL", "test bolus 7mm"]


def load_results():
    batch = json.loads((RUN / "results" / "head_to_head.json").read_text())
    summaries = {c["name"]: c for c in batch["cases"]}
    main = {c["Case"]: c for c in json.loads((RUN / "results" / "main-run.json").read_text())["cases"]}
    engine = {c["Case"]: c for c in json.loads((RUN / "results" / "engine-run.json").read_text())["cases"]}
    return batch, summaries, main, engine


def field(case, pair):
    path = RUN / "fields" / f"{case}__{pair}.csv"
    return np.loadtxt(path, dtype=float, skiprows=1, ndmin=1)


def clamp_for(fields):
    """One scale for all three panels of a model, so they can be read against each other.

    Set from the 99th percentile of |deviation| rather than the maximum: a handful of facets
    at a grid-resolution spike would otherwise wash the whole surface pale. Rounded up to a
    readable step, and never below 0.1 mm - below that the two pipelines' agreement is the
    result and a tighter scale would manufacture colour out of nothing.
    """
    spread = max(np.percentile(np.abs(f), 99) for f in fields)
    for step in (0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0):
        if spread <= step:
            return step
    return float(np.ceil(spread))


def text(fig, x, y, s, size=9.5, colour=INK, weight="normal", family=None, ha="left", va="top",
         linespacing=1.5):
    return fig.text(x, y, s, fontsize=size, color=colour, fontweight=weight, ha=ha, va=va,
                    family=family, linespacing=linespacing)


def wrap(fig, body, width_frac, size):
    """Wrap prose to a column width measured on this figure rather than guessed.

    Hand-wrapped paragraphs go wrong the moment the font or the column changes, and one that
    overruns its column silently lands on top of whatever is beside it. So the character
    budget is searched for: widen it while every wrapped line still measures inside the column
    at the size it will be drawn. Blank lines are kept, since they separate paragraphs.
    """
    renderer = fig.canvas.get_renderer()
    font = FontProperties(size=size)

    def fits(budget):
        lines = []
        for paragraph in body.split("\n"):
            lines.extend(textwrap.wrap(paragraph, budget) if paragraph.strip() else [""])
        widest = max((renderer.get_text_width_height_descent(line, font, False)[0]
                      for line in lines if line), default=0.0)
        return widest / fig.bbox.width <= width_frac, "\n".join(lines)

    # Scan rather than stop at the first budget that fails. textwrap counts characters and this
    # measures rendered width, and the two do not agree monotonically: one budget can break a
    # paragraph onto a line of unusually wide glyphs while a larger budget breaks it better. A
    # loop that broke on the first failure settled a third short of the column every time.
    best, best_budget = None, 0
    for budget in range(8, 400, 2):
        ok, candidate = fits(budget)
        if ok and budget > best_budget:
            best, best_budget = candidate, budget

    return best if best is not None else fits(8)[1]


def column_height(body, size, linespacing=1.45):
    """How much of the page a wrapped column will occupy, as a fraction of page height."""
    return (body.count("\n") + 1) * size * linespacing / 72.0 / PAGE[1]


def fitted(fig, body, width_frac, size, top, floor, label):
    """Wrap a column and refuse to draw one that would overrun into what sits below it."""
    wrapped = wrap(fig, body, width_frac, size)
    height = column_height(wrapped, size)
    if top - height < floor:
        raise SystemExit(f"{label}: the column runs {top - height - floor:+.3f} past its slot "
                         f"({wrapped.count(chr(10)) + 1} lines). Shorten it or widen the column.")
    return wrapped


def rule(fig, y, x0=COL0, x1=COL1, width=0.9):
    fig.add_artist(plt.Line2D([x0, x1], [y, y], color=RULE, linewidth=width,
                              transform=fig.transFigure))


def new_page():
    return plt.figure(figsize=PAGE, facecolor=PAPER)


def rasterize(ax):
    """Bake a mesh panel to pixels inside the PDF.

    A page carries five panels and a panel can be fifty thousand shaded facets; left as vector
    paths that is a twenty-megabyte file that a viewer takes seconds to turn. At DPI the mesh
    is a photograph of a surface either way, and the text and rules around it stay vector.
    """
    for artist in ax.collections:
        artist.set_rasterized(True)


# ----------------------------------------------------------------------------- summary page

def summary_page(pdf, batch, summaries, main, engine):
    fig = new_page()

    text(fig, COL0, 0.962, "Fabolus smoothing: main (MeshLib) vs GeometryEngine", size=21,
         weight="bold")
    text(fig, COL0, 0.916, "Head-to-head over eleven clinical and synthetic inputs at the UI's "
                           "standard preset  \u00b7  27 September 2026", size=10.5, colour=MUTED)
    rule(fig, 0.898, width=1.3)

    intro = (
        "Fabolus on main smooths a bolus with MeshLib voxel offsets and a geometry3Sharp "
        "Reducer. The geometry-engine branch smooths it with GeometryEngine's own DoubleOffset, "
        "Offset and Decimate. The two had never been run on the same input and compared, so "
        "this report does that: the same eleven meshes, the same settings, the same frame.\n"
        "\n"
        "The question is not which is prettier, but whether the engine can replace main without "
        "changing the bolus a clinic prints \u2014 and, where it does change it, by how much "
        "and why."
    )

    method = (
        "Two headless harnesses generate the meshes, one per pipeline, each centring its input "
        "on the bounding box exactly as its own app does on import. The reference harness "
        "carries verbatim copies of main's smoothing code, since Fabolus.Core on main will not "
        "build off Windows.\n"
        "\n"
        "Both run the UI's standard preset: a 1.0 mm offset cycle, 0.1 mm inflation, one "
        "iteration, 1.0 mm cell, decimated to twice the input triangle count. Component "
        "separation, which only the engine branch does, is off so both sides see the same "
        "geometry.\n"
        "\n"
        "Measured with Fabolus.MeshCompare on linux-x64, against libmanifoldc built at upstream "
        "7c86359 \u2014 the commit the shipped win-x64 binaries come from. ICP between the "
        "outputs returned under 0.04 mm and 0.18\u00b0, so the harnesses agree about where the "
        "model is."
    )

    verdict = (
        "The geometry matches closely: mean Dice 0.992, HD95 between 0.03 and 0.23 mm, mean "
        "surface separation under 0.07 mm. The engine sits a touch outside main \u2014 bias "
        "+0.010 mm on average, never past +0.030 mm \u2014 which the code predicts: main's "
        "inflation offset runs on a finer grid, and the engine's decimation has no projection "
        "target.\n"
        "\n"
        "Against the unsmoothed anatomy the engine is the closer of the two, halving main's "
        "HD95 on nine of eleven cases.\n"
        "\n"
        "Topology is the gap: three engine outputs are not watertight against none on main, and "
        "a fourth gains two self-intersections. All four are Decimate's doing \u2014 asked "
        "directly, the level-set offset is closed and manifold on every case, and what it hands "
        "on is a few coincident vertex pairs that Decimate welds into an edge with four faces.\n"
        "\n"
        "Runtime is 2.1x main's: the offsets cost more, the decimation costs less."
    )

    for x, w, heading, body in (
            (COL0, 0.205, "Introduction", intro),
            (0.285, 0.265, "How this was tested", method),
            (0.580, 0.370, "Summary", verdict)):
        text(fig, x, 0.868, heading, size=12.5, weight="bold")
        text(fig, x, 0.840, fitted(fig, body, w, 8.3, 0.840, 0.434, heading), size=8.3,
             linespacing=1.40)

    print("   summary column heights: " + ", ".join(
        f"{name} {column_height(wrap(fig, body, w, 8.3), 8.3, 1.40):.3f}"
        for name, w, body in (("intro", 0.205, intro), ("method", 0.265, method),
                              ("summary", 0.370, verdict))))

    rule(fig, 0.420, width=1.3)
    text(fig, COL0, 0.402, "Per-case result", size=12.5, weight="bold")
    text(fig, 0.215, 0.400,
         "distances in mm; volume and area are the engine's difference from main; "
         "W / NM / SI = watertight, non-manifold edges, self-intersections",
         size=8.5, colour=MUTED)

    # Two-line headers on the wide ones: a right-aligned header longer than its column runs
    # backwards into its neighbour, and these labels are all longer than the numbers under them.
    columns = ["case", "in\ntris", "out\ntris", "Dice", "HD95", "ASSD", "bias", "vol\n%",
               "area\n%", "main\nW / NM / SI", "engine\nW / NM / SI", "main\ns", "engine\ns",
               "checks"]
    widths = [0.105, 0.045, 0.047, 0.050, 0.044, 0.044, 0.048, 0.042, 0.045, 0.086, 0.090,
              0.040, 0.046, 0.148]
    xs = np.cumsum([0] + widths)[:-1] + COL0

    header_y = 0.366
    for label, x, w in zip(columns, xs, widths):
        right = label != "case" and label != "checks"
        text(fig, x + (w - 0.006 if right else 0), header_y, label, size=8.5, colour=MUTED,
             weight="bold", ha="right" if right else "left", linespacing=1.25)
    rule(fig, header_y - 0.032)

    row_y = header_y - 0.053
    for case in CASES:
        s = summaries[case]["summary"]
        passed = summaries[case]["passed"]
        failed = summaries[case].get("failed_checks") or []

        def topology(scope):
            nm = int(s[f"{scope}.nonmanifold_edges"])
            si = int(s[f"{scope}.self_intersections"])
            return f"{'yes' if s[f'{scope}.watertight'] else 'NO'} / {nm} / {si}"

        if passed:
            note, colour = "all pass", GOOD
        else:
            kinds = []
            if any(k in f for f in failed for k in ("watertight", "nonmanifold", "genus")):
                kinds.append("topology")
            if any("self_intersections" in f for f in failed):
                kinds.append("self-intersections")
            if any("volume" in f for f in failed):
                kinds.append("volume")
            if any("icp" in f for f in failed):
                kinds.append("ICP noise")
            note, colour = ", ".join(kinds), BAD

        cells = [
            (case, "left", INK),
            (f"{main[case]['InputTriangles']:,}", "right", INK),
            (f"{int(s['cand.triangles']):,}", "right", INK),
            (f"{s['cand_ref.dice']:.4f}", "right", INK),
            (f"{s['cand_ref.hd95_mm']:.3f}", "right", INK),
            (f"{s['cand_ref.assd_mm']:.3f}", "right", INK),
            (f"{s['cand_ref.signed_bias_mm']:+.3f}", "right", INK),
            (f"{s['cand_ref.volume_diff_pct']:+.2f}", "right", INK),
            (f"{s['cand_ref.area_diff_pct']:+.2f}", "right", INK),
            (topology("ref"), "right", INK),
            (topology("cand"), "right", INK if passed else colour),
            (f"{main[case]['TotalMs'] / 1000:.2f}", "right", INK),
            (f"{engine[case]['TotalMs'] / 1000:.2f}", "right", INK),
            (note, "left", colour),
        ]
        for (value, align, ink), x, w in zip(cells, xs, widths):
            text(fig, x + (w - 0.006 if align == "right" else 0), row_y, value, size=8.6,
                 colour=ink, ha=align)
        row_y -= 0.0240

    rule(fig, row_y + 0.007)
    text(fig, COL0, row_y - 0.011,
         f"{batch['totals']['passed']} of {batch['totals']['cases']} cases pass every default "
         f"meshcompare check. Every failure is topology, one volume margin, or ICP fitting "
         f"noise \u2014 none is a distance or overlap metric.",
         size=8.8, colour=MUTED)
    text(fig, COL1, 0.026, "1", size=9, colour=MUTED, ha="right", va="bottom")

    pdf.savefig(fig, facecolor=PAPER, dpi=DPI)
    plt.close(fig)


# --------------------------------------------------------------------------- per-model pages

def model_page(pdf, page_number, case, summaries, main, engine):
    s = summaries[case]["summary"]
    fig = new_page()

    meshes = {
        "input": load_stl(RUN / "input" / f"{case}.stl"),
        "main": load_stl(RUN / "main" / f"{case}.stl"),
        "engine": load_stl(RUN / "engine" / f"{case}.stl"),
    }
    fields = {key: field(case, key) for key, _, _ in PAIRS}
    for (key, _, subject) in PAIRS:
        if len(fields[key]) != len(meshes[subject]):
            raise SystemExit(f"{case}/{key}: {len(fields[key])} values for "
                             f"{len(meshes[subject])} facets")

    # Two scales, not one. The two "vs original" panels share theirs, because the whole point
    # is to read them against each other. The head-to-head gets its own: the pipelines differ
    # from each other by far less than either differs from the input, so on the shared scale
    # that panel is blank paper - true, but it shows nothing about where they differ.
    fidelity_clamp = clamp_for([fields["main_vs_orig"], fields["engine_vs_orig"]])
    head_clamp = clamp_for([fields["engine_vs_main"]])
    norms = {
        "main_vs_orig": Normalize(vmin=-fidelity_clamp, vmax=fidelity_clamp),
        "engine_vs_orig": Normalize(vmin=-fidelity_clamp, vmax=fidelity_clamp),
        "engine_vs_main": Normalize(vmin=-head_clamp, vmax=head_clamp),
    }

    text(fig, COL0, 0.952, case, size=18, weight="bold")
    text(fig, COL0, 0.908,
         f"{main[case]['InputTriangles']:,} input facets  \u00b7  "
         f"{int(s['cand.triangles']):,} facets out of both pipelines  \u00b7  "
         f"input surface {s['orig.area_mm2']:,.0f} mm\u00b2, "
         f"volume {s['orig.volume_mm3']:,.0f} mm\u00b3",
         size=9.6, colour=MUTED)
    rule(fig, 0.888, width=1.3)

    # One camera for every panel on the page, fitted to the unsmoothed input and handed on, so
    # a feature lands on the same pixel in all five and they can be read against each other.
    probe = fig.add_axes([0, 0, 0.001, 0.001])
    _, limits = render(probe, meshes["input"], base_rgb=MAIN_RGB)
    probe.remove()

    # ---- the two smoothed meshes
    for (x, label, key, colour) in ((COL0, "main (MeshLib)", "main", MAIN_RGB),
                                    (0.268, "GeometryEngine", "engine", ENGINE_RGB)):
        ax = fig.add_axes([x, 0.548, 0.212, 0.295])
        ax.set_facecolor(PAPER)
        render(ax, meshes[key], base_rgb=colour, limits=limits)
        rasterize(ax)
        ax.set_title(label, fontsize=10.5, color=INK, fontweight="bold", pad=5)

    text(fig, COL0, 0.533,
         "The smoothed mesh from each pipeline. Every panel on this page shares one camera, "
         "fitted to the unsmoothed input.", size=8.7, colour=MUTED)

    # ---- the three deviation fields
    heat_boxes = {
        "main_vs_orig": (0.520, 0.548),
        "engine_vs_orig": (0.725, 0.548),
        "engine_vs_main": (0.520, 0.195),
    }
    for (key, label, subject) in PAIRS:
        x, y = heat_boxes[key]
        ax = fig.add_axes([x, y, 0.185, 0.295])
        ax.set_facecolor(PAPER)
        render(ax, meshes[subject], face_rgb=HEAT(norms[key](fields[key]))[:, :3], limits=limits,
               relief=0.45)
        rasterize(ax)
        ax.set_title(label, fontsize=9.8, color=INK, fontweight="bold", pad=4)
        dev = fields[key]
        ax.text(0.5, -0.035,
                f"mean |dev| {np.abs(dev).mean():.3f} mm   {100.0 * (dev < 0).mean():.0f}% inside",
                transform=ax.transAxes, ha="center", va="top", fontsize=8.2, color=MUTED)

    def legend(x, y, width, clamp, title, subtitle):
        bar = fig.add_axes([x, y, width, 0.019])
        mappable = ScalarMappable(norm=Normalize(vmin=-clamp, vmax=clamp), cmap=HEAT)
        mappable.set_array([])
        fig.colorbar(mappable, cax=bar, orientation="horizontal",
                     ticks=[-clamp, -clamp / 2, 0, clamp / 2, clamp])
        bar.tick_params(labelsize=8.2, colors=MUTED, length=2.5, pad=2)
        for spine in bar.spines.values():
            spine.set_edgecolor(RULE)
        centre = x + width / 2
        text(fig, centre, y + 0.037, title, size=9.6, colour=INK, weight="bold", ha="center",
             va="bottom")
        text(fig, centre, y + 0.023, subtitle, size=8.3, colour=MUTED, ha="center", va="bottom")

    legend(0.735, 0.400, 0.170, fidelity_clamp, "Signed deviation",
           f"both panels above  \u00b7  \u00b1{fidelity_clamp:g} mm")
    legend(0.545, 0.108, 0.135, head_clamp, "Signed deviation",
           f"panel above  \u00b7  \u00b1{head_clamp:g} mm")

    text(fig, 0.725, 0.352, wrap(fig,
         "Each panel colours the mesh named first in its title by how far it lies from the mesh "
         "named second. Red: the coloured surface lies inside the one it is measured against "
         "\u2014 material the smoothing took away. Blue: outside it \u2014 material the "
         "smoothing added. Near-white: the two surfaces coincide.\n"
         "\n"
         "The two fidelity panels share one scale so they can be read against each other. The "
         "head-to-head below has its own, because the pipelines differ from each other by far "
         "less than either differs from the input.", 0.200, 8.3),
         size=8.3, colour=MUTED, linespacing=1.4)

    # ---- metrics, under the renders
    rule(fig, 0.497, x1=0.480)
    text(fig, COL0, 0.479, "Metrics", size=12.5, weight="bold")

    def rows(x, y, title, entries, width=0.200):
        text(fig, x, y, title, size=9.8, weight="bold", colour=INK)
        y -= 0.030
        for label, value, ink in entries:
            text(fig, x, y, label, size=8.8, colour=MUTED)
            text(fig, x + width, y, value, size=8.8, colour=ink, ha="right")
            y -= 0.0245
        return y

    rows(COL0, 0.444, "engine vs main", [
        ("Dice", f"{s['cand_ref.dice']:.4f}", INK),
        ("HD95 / Hausdorff", f"{s['cand_ref.hd95_mm']:.3f} / {s['cand_ref.hausdorff_mm']:.3f} mm", INK),
        ("mean separation (ASSD)", f"{s['cand_ref.assd_mm']:.4f} mm", INK),
        ("signed bias", f"{s['cand_ref.signed_bias_mm']:+.4f} mm", INK),
        ("surface Dice @0.5 mm", f"{s['cand_ref.surface_dice@0.5']:.4f}", INK),
        ("volume / area", f"{s['cand_ref.volume_diff_pct']:+.2f} % / "
                          f"{s['cand_ref.area_diff_pct']:+.2f} %", INK),
        ("curvature ratio", f"{s['cand_ref.curvature_ratio']:.3f}", INK),
    ])

    rows(0.268, 0.444, "fidelity to the input      main \u2192 engine", [
        ("Dice", f"{s['ref_orig.dice']:.4f} \u2192 {s['cand_orig.dice']:.4f}", INK),
        ("HD95", f"{s['ref_orig.hd95_mm']:.3f} \u2192 {s['cand_orig.hd95_mm']:.3f} mm", INK),
        ("mean separation", f"{s['ref_orig.assd_mm']:.3f} \u2192 {s['cand_orig.assd_mm']:.3f} mm", INK),
        ("signed bias", f"{s['ref_orig.signed_bias_mm']:+.3f} \u2192 "
                        f"{s['cand_orig.signed_bias_mm']:+.3f} mm", INK),
        ("volume", f"{s['ref_orig.volume_diff_pct']:+.2f} \u2192 "
                   f"{s['cand_orig.volume_diff_pct']:+.2f} %", INK),
        ("runtime", f"{main[case]['TotalMs'] / 1000:.2f} \u2192 "
                    f"{engine[case]['TotalMs'] / 1000:.2f} s", INK),
        ("ICP residual", f"{s['icp.translation_mm']:.3f} mm, {s['icp.rotation_deg']:.3f}\u00b0",
         MUTED),
    ])

    # One topology table with a column per pipeline rather than two tables: the rows are the
    # same either side, and side by side is how the difference is actually read.
    def topology_value(scope, key):
        if key == "watertight":
            ok = bool(s[f"{scope}.watertight"])
            return ("yes" if ok else "NO"), (INK if ok else BAD)
        if key == "genus":
            genus = s[f"{scope}.genus"]
            return ("n/a" if genus is None else f"{int(genus)}"), (MUTED if genus is None else INK)
        count = int(s[f"{scope}.{key}"])
        bad = count and key in ("nonmanifold_edges", "self_intersections", "degenerate_triangles")
        return f"{count}", (BAD if bad else INK)

    text(fig, COL0, 0.235, "topology", size=9.8, weight="bold", colour=INK)
    text(fig, COL0 + 0.135, 0.235, "main", size=8.8, weight="bold", colour=MUTED, ha="right")
    text(fig, COL0 + 0.205, 0.235, "engine", size=8.8, weight="bold", colour=MUTED, ha="right")
    y = 0.208
    for label, key in (("watertight", "watertight"), ("non-manifold edges", "nonmanifold_edges"),
                       ("self-intersections", "self_intersections"), ("components", "components"),
                       ("genus", "genus"),
                       ("degenerate facets", "degenerate_triangles")):
        text(fig, COL0, y, label, size=8.8, colour=MUTED)
        for scope, offset in (("ref", 0.135), ("cand", 0.205)):
            value, ink = topology_value(scope, key)
            text(fig, COL0 + offset, y, value, size=8.8, colour=ink, ha="right")
        y -= 0.0245

    if s["ref.genus"] is None or s["cand.genus"] is None:
        text(fig, COL0, y + 0.004,
             "n/a: genus is undefined for a surface this open or this non-manifold.",
             size=8.2, colour=MUTED)

    failed = summaries[case].get("failed_checks") or []
    text(fig, 0.268, 0.235, "meshcompare default checks", size=9.8, weight="bold")
    if not failed:
        text(fig, 0.268, 0.208, "all ten pass", size=8.8, colour=GOOD)
    else:
        y = 0.208
        for line in failed:
            body = wrap(fig, "\u2022 " + line.replace("(Value is null (not computable for these "
                                                       "meshes).)", "(not computable)"), 0.205, 8.5)
            text(fig, 0.268, y, body, size=8.5, colour=BAD, linespacing=1.35)
            y -= 0.020 + 0.0165 * body.count("\n")

    text(fig, COL1, 0.030, f"{page_number}", size=9, colour=MUTED, ha="right", va="bottom")

    pdf.savefig(fig, facecolor=PAPER, dpi=DPI)
    plt.close(fig)


def main_entry():
    batch, summaries, main, engine = load_results()
    with PdfPages(OUT) as pdf:
        summary_page(pdf, batch, summaries, main, engine)
        for i, case in enumerate(CASES, start=2):
            model_page(pdf, i, case, summaries, main, engine)
            print(f"page {i}: {case}")
        info = pdf.infodict()
        info["Title"] = "Fabolus smoothing: main (MeshLib) vs GeometryEngine"
        info["Subject"] = "Head-to-head comparison of the two smoothing pipelines"
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main_entry()
