"""Draws the benefit charts in docs/assets (light and dark SVG) from the evidence packs in docs/evidence.

Every value below is copied from a run's report.json (or, for run 7, its README), and the run is named next to it.
The business case is the one projection, and the chart says so. Run: python docs/assets/make_charts.py
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
FONT = "system-ui, -apple-system, 'Segoe UI', sans-serif"

THEMES = {
    "light": dict(surface="#fcfcfb", ink="#0b0b0b", ink2="#52514e", muted="#898781", grid="#e1e0d9", axis="#c3c2b7",
                  blue="#2a78d6", good="#0ca30c", critical="#d03b3b", ring="#fcfcfb", tile="#f4f3f0"),
    "dark": dict(surface="#1a1a19", ink="#f0efec", ink2="#c3c2b7", muted="#898781", grid="#2c2c2a", axis="#383835",
                 blue="#3987e5", good="#0ca30c", critical="#d03b3b", ring="#1a1a19", tile="#222220"),
}

# Gate runs (docs/evidence/<n>-*/report.json; run 7 from its README; run 10 = docs/evidence/10-p0-fixes-*).
RUNS = [  # run, verdict, short label (two lines), p95 latency in seconds (None: not a full run)
    (1, "NO-GO", ("Planted note:", "“pre-approved”"), 10.5),
    (2, "NO-GO", ("Run 1 re-scored:", "phishing link found"), None),   # run 1 re-scored by gate v2, no model calls
    (3, "GO", ("Hardened:", "0 of 4 attacks work"), 8.5),
    (4, "NO-GO", ("Planted demo patch:", "leak caught"), None),  # demo/planted-leak.patch, 6 scenarios
    (5, "NO-GO", ("Bug in the agent’s", "citation check"), 7.4),
    (6, "GO", ("Version 4:", "all blockers pass"), 9.4),
    (7, "GO", ("Live in Azure:", "16 of 16 checks"), 3.4),
    (8, "NO-GO", ("One request hung:", "p95 26.9 s"), 26.9),
    (9, "GO", ("20 s timeout:", "p95 2.4 s"), 2.4),
    (10, "GO", ("P0 fixes:", "approvals in Redis"), 2.5),
]


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def svg(w, h, t, title, desc, body):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" role="img" '
            f'aria-labelledby="t d" font-family="{FONT}">\n<title id="t">{esc(title)}</title>\n<desc id="d">{esc(desc)}</desc>\n'
            f'<rect width="{w}" height="{h}" rx="12" fill="{t["surface"]}"/>\n{body}\n</svg>\n')


def text(x, y, s, t, size=14, color="ink", weight=400, anchor="start"):
    return (f'<text x="{x}" y="{y}" font-size="{size}" font-weight="{weight}" fill="{t[color]}" '
            f'text-anchor="{anchor}">{esc(s)}</text>')


def header(t, title, subtitle):
    return text(32, 44, title, t, 20, "ink", 600) + "\n" + text(32, 70, subtitle, t, 14, "ink2")


def hbar(x0, y, x1, h, color, r=4):
    """Horizontal bar: square at the baseline (x0), 4px rounded data end (x1)."""
    r = min(r, (x1 - x0) / 2, h / 2)
    return (f'<path d="M{x0},{y} H{x1 - r} A{r},{r} 0 0 1 {x1},{y + r} V{y + h - r} A{r},{r} 0 0 1 {x1 - r},{y + h} '
            f'H{x0} Z" fill="{color}"/>')


def status_mark(cx, cy, verdict, t, r=11):
    """Status never by colour alone: a check or a cross inside, and NO-GO is a diamond."""
    if verdict == "GO":
        return (f'<circle cx="{cx}" cy="{cy}" r="{r + 2}" fill="{t["ring"]}"/><circle cx="{cx}" cy="{cy}" r="{r}" fill="{t["good"]}"/>'
                f'<path d="M{cx - 5},{cy} l3.5,3.5 l6.5,-7" stroke="#ffffff" stroke-width="2.4" fill="none" stroke-linecap="round" stroke-linejoin="round"/>')
    d = r + 2
    return (f'<path d="M{cx},{cy - d - 2} L{cx + d + 2},{cy} L{cx},{cy + d + 2} L{cx - d - 2},{cy} Z" fill="{t["ring"]}"/>'
            f'<path d="M{cx},{cy - d} L{cx + d},{cy} L{cx},{cy + d} L{cx - d},{cy} Z" fill="{t["critical"]}"/>'
            f'<path d="M{cx - 4},{cy - 4} l8,8 M{cx + 4},{cy - 4} l-8,8" stroke="#ffffff" stroke-width="2.4" stroke-linecap="round"/>')


def kpis(t):
    tiles = [
        ("Time for a full evidence run", "1.4 min", "latest; 1.2 to 4.1 min over 8 full runs"),
        ("Cost of one evidence run", "$0.11", "28 scenarios, 7 checks, an AI judge"),
        ("Defects stopped before users", "6", "plus 1 planted demo the gate caught"),
        ("Invented figures caught by code", "93%", "195 of 210 in a mutation test"),
        ("Assistant cost per conversation", "$0.0011", "about $6.64 a month at 6,000 lookups"),
    ]
    w, h, gap, x = 1200, 220, 16, 32
    tw = (w - 2 * x - gap * (len(tiles) - 1)) / len(tiles)
    body = [header(t, "What one PilotGate run gives you", "Measured in the gate runs in docs/evidence (runs 1 to 10)")]
    for i, (label, value, note) in enumerate(tiles):
        tx = x + i * (tw + gap)
        body.append(f'<rect x="{tx:.1f}" y="92" width="{tw:.1f}" height="104" rx="10" fill="{t["tile"]}"/>')
        body.append(text(tx + 16, 118, label, t, 13, "ink2"))
        body.append(text(tx + 16, 160, value, t, 36, "ink", 600))
        body.append(text(tx + 16, 184, note, t, 11.5, "muted"))
    desc = "; ".join(f"{a}: {b} ({c})" for a, b, c in tiles)
    return svg(w, h, t, "What one PilotGate run gives you", desc, "\n".join(body))


def timeline(t):
    w, h = 1200, 330
    x0, x1, y = 90, 1140, 190
    step = (x1 - x0) / (len(RUNS) - 1)
    body = [header(t, "Every NO-GO stopped a defect before a user saw it",
                   "PilotGate verdicts for runs 1 to 10 against Azure OpenAI (run 2 re-scores run 1; run 4 is a planted demo); a NO-GO fails the build")]
    body.append(f'<line x1="{x0}" y1="{y}" x2="{x1}" y2="{y}" stroke="{t["axis"]}" stroke-width="1"/>')
    for i, (run, verdict, (l1, l2), _) in enumerate(RUNS):
        cx = x0 + i * step
        up = i % 2 == 0
        ly = y - 58 if up else y + 50
        body.append(f'<line x1="{cx:.1f}" y1="{y}" x2="{cx:.1f}" y2="{y - 30 if up else y + 30}" stroke="{t["grid"]}" stroke-width="1"/>')
        body.append(text(cx, ly, l1, t, 12.5, "ink", 600, "middle"))
        body.append(text(cx, ly + 16, l2, t, 12.5, "ink2", 400, "middle"))
        body.append(status_mark(round(cx, 1), y, verdict, t))
        body.append(text(cx, y + (-18 if not up else 28), f"Run {run}", t, 11, "muted", 400, "middle"))
    # Legend: shape + glyph + words, so status is never colour alone.
    body.append(status_mark(44, 302, "GO", t, 8))
    body.append(text(60, 306, "GO WITH CONDITIONS", t, 12, "ink2"))
    body.append(status_mark(230, 302, "NO-GO", t, 8))
    body.append(text(248, 306, "NO-GO (release blocked)", t, 12, "ink2"))
    desc = "; ".join(f"Run {r}: {v}, {a} {b}" for r, v, (a, b), _ in RUNS)
    return svg(w, h, t, "Every NO-GO stopped a defect before a user saw it", desc, "\n".join(body))


def latency(t):
    pts = [(r, p, v) for r, v, _, p in RUNS if p is not None]
    w, h = 1200, 400
    left, right, top, bottom = 80, 1120, 110, 340
    ymax = 30
    xs = {r: left + i * (right - left) / (len(pts) - 1) for i, (r, _, _) in enumerate(pts)}
    Y = lambda v: bottom - v / ymax * (bottom - top)
    body = [header(t, "p95 answer time per gate run, against the 20-second budget",
                   "Run 8 broke the budget (one request hung for the SDK's 100 s default) and was stopped; a 20 s timeout with retries fixed it")]
    for g in (0, 10, 20, 30):
        body.append(f'<line x1="{left}" y1="{Y(g):.1f}" x2="{right}" y2="{Y(g):.1f}" stroke="{t["grid" if g else "axis"]}" stroke-width="1"/>')
        body.append(text(left - 12, Y(g) + 4, f"{g} s", t, 12, "muted", 400, "end"))
    body.append(f'<line x1="{left}" y1="{Y(20):.1f}" x2="{right}" y2="{Y(20):.1f}" stroke="{t["critical"]}" stroke-width="1.5"/>')
    body.append(text(right, Y(20) - 8, "Budget: 20 s (p95)", t, 12, "ink2", 600, "end"))
    path = " ".join(("M" if i == 0 else "L") + f"{xs[r]:.1f},{Y(p):.1f}" for i, (r, p, _) in enumerate(pts))
    body.append(f'<path d="{path}" fill="none" stroke="{t["blue"]}" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>')
    for r, p, v in pts:
        body.append(f'<circle cx="{xs[r]:.1f}" cy="{Y(p):.1f}" r="6" fill="{t["ring"]}"/><circle cx="{xs[r]:.1f}" cy="{Y(p):.1f}" r="4" fill="{t["blue"]}"/>')
        body.append(text(xs[r], bottom + 24, f"Run {r}", t, 12, "muted", 400, "middle"))
    body.append(status_mark(xs[8] + 34, Y(26.9), "NO-GO", t, 8))
    body.append(text(xs[8] + 52, Y(26.9) + 5, "26.9 s: NO-GO", t, 13, "ink", 600))
    body.append(text(xs[10], Y(2.5) - 14, "2.5 s", t, 13, "ink", 600, "middle"))
    desc = "; ".join(f"Run {r}: p95 {p} s ({v})" for r, p, v in pts) + ". Budget 20 s."
    return svg(w, h, t, "p95 answer time per gate run, against the 20-second budget", desc, "\n".join(body))


def business(t):
    # Run 10 report.json, businessCase: expected 700 h (EUR 40,600), conservative 243.5 h (EUR 14,124), model $6.64.
    rows = [("Expected", 700, "700 h a month, worth EUR 40,600"),
            ("Conservative (lower bound of a 95% interval)", 243.5, "244 h a month, worth EUR 14,124")]
    w, h = 1200, 330
    left, right, top = 360, 1000, 108
    X = lambda v: left + v / 800 * (right - left)
    body = [header(t, "Handler time saved per month: a projection, not a measurement",
                   "120 claims handlers, 6,000 policy lookups a month, EUR 58 an hour; model cost about $6.64 a month")]
    for g in (0, 200, 400, 600, 800):
        body.append(f'<line x1="{X(g):.1f}" y1="{top - 8}" x2="{X(g):.1f}" y2="{top + 112}" stroke="{t["grid" if g else "axis"]}" stroke-width="1"/>')
        body.append(text(X(g), top + 132, f"{g} h", t, 12, "muted", 400, "middle"))
    for i, (label, v, note) in enumerate(rows):
        y = top + 14 + i * 52
        body.append(text(left - 14, y + 17, label, t, 13, "ink2", 400, "end"))
        body.append(hbar(X(0), y, X(v), 24, t["blue"]))
        body.append(text(X(v) + 10, y + 17, note, t, 13, "ink", 600))
    body.append(text(32, h - 46, "Assumes a lookup takes 9 minutes today and 2 with the assistant: the sponsor's estimate. The conservative row counts only", t, 12, "muted"))
    body.append(text(32, h - 28, "the lower bound of task success (81% at 95% confidence, from 16 of 16 correct). Replace with a two-week time study first.", t, 12, "muted"))
    desc = "; ".join(f"{a}: {n}" for a, _, n in rows) + ". Projection based on a sponsor estimate."
    return svg(w, h, t, "Handler time saved per month: a projection, not a measurement", desc, "\n".join(body))


if __name__ == "__main__":
    for mode, t in THEMES.items():
        for name, fn in (("kpis", kpis), ("timeline", timeline), ("latency", latency), ("business-case", business)):
            path = os.path.join(HERE, f"benefits-{name}-{mode}.svg")
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(fn(t))
            print("wrote", os.path.relpath(path))
