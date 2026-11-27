"""Draws two looping animations (CSS only, so they also play inside a GitHub README):

- docs/assets/architecture-animated.svg (36 s): the lifecycle, from an engineer's change through the gate to production.
- docs/assets/pilotgate-explainer.svg (55 s): the same diagram between an opening (how a pilot gets approved by hand
  today) and a closing comparison (today versus with PilotGate). launch/render_video.py turns it into the video.

The "today" side stays qualitative on purpose; every PilotGate number comes from a measured run in docs/evidence.
Run: python docs/assets/make_architecture.py
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
W = H = 1080
FONT = "system-ui, -apple-system, 'Segoe UI', sans-serif"
C = dict(bg="#121212", node="#1f1f1d", stroke="#383835", grid="#2c2c2a", ink="#f0efec", ink2="#c3c2b7", muted="#898781",
         blue="#3987e5", orange="#d95926", good="#0ca30c", warning="#fab219", critical="#d03b3b", dark="#0b0b0b")

# Build state: the loop length, and the running scene's start (absolute) and end (relative to its start).
S = dict(dur=28.0, shift=0.0, end=27.4, n=0, css=[], body=[])


def pct(t):
    return f"{max(0.0, min(100.0, t / S['dur'] * 100)):.3f}%"


def anim(stops, prop=None):
    """stops: [(t, value)] in seconds from the scene's start; value a string for `prop`, or a dict of properties. One
    keyframes rule per element: two animation classes on one element would override each other."""
    S["n"] += 1
    name = f"k{S['n']}"
    absolute = [(t + S["shift"], v) for t, v in stops]
    frames = [(0.0, absolute[0][1])] + absolute + [(S["dur"], absolute[-1][1])]
    seen, lines = set(), []
    for t, v in frames:
        p = pct(t)
        if p in seen:
            continue
        seen.add(p)
        decl = ";".join(f"{k}:{x}" for k, x in v.items()) if isinstance(v, dict) else f"{prop}:{v}"
        lines.append(f"{p}{{{decl}}}")
    S["css"].append(f"@keyframes {name}{{{''.join(lines)}}}.{name}{{animation:{name} {S['dur']}s linear infinite both}}")
    return name


def _out(t1):
    return t1 + (0.6 if t1 == S["end"] else 0.35)


def window(t0, t1=None, fade=0.35):
    t1 = S["end"] if t1 is None else t1
    return anim([(t0, "0"), (t0 + fade, "1"), (t1, "1"), (_out(t1) if t1 == S["end"] else t1 + fade, "0")], "opacity")


def pop(t0, t1=None):
    """Fades in with a small scale-up; transform-origin is the element's own centre."""
    t1 = S["end"] if t1 is None else t1
    k = anim([(t0, {"opacity": 0, "transform": "scale(.82)"}), (t0 + .35, {"opacity": 1, "transform": "scale(1.06)"}),
              (t0 + .5, {"opacity": 1, "transform": "scale(1)"}), (t1, {"opacity": 1, "transform": "scale(1)"}),
              (_out(t1), {"opacity": 0, "transform": "scale(1)"})])
    return f"{k} pop"


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def text(x, y, s, size, color="ink", weight=400, anchor="start", cls="", spacing=None):
    c = f' class="{cls}"' if cls else ""
    ls = f' letter-spacing="{spacing}"' if spacing else ""
    return (f'<text{c} x="{x}" y="{y}" font-size="{size}" font-weight="{weight}" fill="{C[color]}" text-anchor="{anchor}"{ls}>'
            f'{esc(s)}</text>')


def add(s):
    S["body"].append(s)


def node(x, y, w, h, title, cls):
    return (f'<g class="{cls}"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="14" fill="{C["node"]}" stroke="{C["stroke"]}" stroke-width="1.5"/>'
            + text(x + 24, y + 38, title, 29, "ink", 600) + "</g>")


def check(cx, cy, cls, color="good", glyph="check", r=15):
    mark = (f'<path d="M{cx - 6.5},{cy} l4.5,4.5 l8.5,-9" stroke="#fff" stroke-width="3" fill="none" stroke-linecap="round" stroke-linejoin="round"/>'
            if glyph == "check" else
            f'<path d="M{cx - 5},{cy - 5} l10,10 M{cx + 5},{cy - 5} l-10,10" stroke="#fff" stroke-width="3" stroke-linecap="round"/>')
    return f'<g class="{cls}"><circle cx="{cx}" cy="{cy}" r="{r}" fill="{C[color]}"/>{mark}</g>'


def clock(cx, cy, cls, r=20):
    """A small clock: the cost of doing it by hand is time."""
    return (f'<g class="{cls}"><circle cx="{cx}" cy="{cy}" r="{r}" fill="none" stroke="{C["warning"]}" stroke-width="3"/>'
            f'<path d="M{cx},{cy - r + 7} V{cy} l7,5" stroke="{C["warning"]}" stroke-width="3" fill="none" stroke-linecap="round"/></g>')


def stat_chips(t0, y=828):
    stats = [("1.4 min", "per gate run"), ("$0.11", "per gate run"), ("6", "defects stopped"), ("0", "keys for Azure")]
    for i, (value, label) in enumerate(stats):
        x = 60 + i * 245
        add(f'<g class="{pop(t0 + i * 0.35)}"><rect x="{x}" y="{y}" width="225" height="112" rx="14" fill="{C["node"]}"/>'
            + text(x + 20, y + 58, value, 42, "ink", 700) + text(x + 20, y + 92, label, 21, "ink2") + "</g>")


def narration(lines_by_window, y=862):
    for t0, t1, l1, l2 in lines_by_window:
        cls = window(t0, t1)
        add(text(60, y, l1, 34, "ink", 600, cls=cls))
        add(text(60, y + 46, l2, 34, "ink", 600, cls=cls))


# ---- Scene: today, by hand ------------------------------------------------------------------------------------------
def intro():
    add(text(60, 96, "THE PROBLEM", 22, "muted", 600, cls=window(0.2), spacing="3"))
    t = window(0.4)
    add(text(60, 160, "Getting an AI pilot approved,", 50, "ink", 700, cls=t))
    add(text(60, 220, "the manual way", 50, "ink", 700, cls=t))
    rows = ["Manual security reviews", "GDPR and AI Act checklists in spreadsheets", "One-off red-team tests",
            "Screenshots as the evidence", "Weeks waiting for four sign-offs"]
    for i, row in enumerate(rows):
        y, t0 = 320 + i * 92, 1.0 + i * 0.7
        # Visible, then dimmed once PilotGate replaces it.
        dim = anim([(t0, "0"), (t0 + .35, "1"), (7.9, "1"), (8.3, ".35"), (S["end"], ".35"), (S["end"] + .6, "0")], "opacity")
        add(clock(84, y - 10, dim))
        add(text(130, y, row, 30, "ink2", cls=dim))
        length = 12 + len(row) * 13.5   # about 13.5 px per character at 30 px in the system sans
        strike = anim([(7.6 + i * .12, "0"), (8.0 + i * .12, "1"), (S["end"], "1"), (S["end"] + .6, "0")], "opacity")
        add(f'<line class="{strike}" x1="124" y1="{y - 10}" x2="{124 + length:.0f}" y2="{y - 10}" stroke="{C["muted"]}" stroke-width="3"/>')
    add(f'<g class="{pop(4.9, 7.8)}"><rect x="60" y="770" width="600" height="58" rx="29" fill="{C["warning"]}"/>'
        + text(360, 808, "Out of date after the next change", 27, "dark", 700, "middle") + "</g>")
    add(f'<g class="{pop(8.1)}"><rect x="60" y="770" width="660" height="58" rx="29" fill="{C["blue"]}"/>'
        + text(390, 808, "PilotGate: one automated gate, every change", 27, "ink", 700, "middle") + "</g>")
    narration([
        (0.6, 4.9, "Without tooling, teams prove an AI pilot is", "safe by hand: reviews, checklists, screenshots."),
        (4.9, 8.0, "It takes weeks, and 88% of AI pilots never", "reach production (IDC with Lenovo, 2025)."),
        (8.0, S["end"], "PilotGate turns that into one automated", "gate that runs on every change. Here is how."),
    ], y=900)


# ---- Scene: the lifecycle, from a change to production and back ------------------------------------------------
def node2(x, y, w, h, title, cls, size=26):
    return (f'<g class="{cls}"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="14" fill="{C["node"]}" stroke="{C["stroke"]}" stroke-width="1.5"/>'
            + text(x + 22, y + 30, title, size, "ink", 600) + "</g>")


def ring(x, y, w, h, color, t0, t1=None):
    add(f'<rect class="{window(t0, t1)}" x="{x}" y="{y}" width="{w}" height="{h}" rx="14" fill="none" stroke="{C[color]}" stroke-width="3"/>')


def dot_on(path, t0, dur, color, r=7):
    pos = anim([(t0, "0%"), (t0 + dur, "100%")], "offset-distance")
    add(f'<g class="{window(t0, t0 + dur, 0.1)}"><circle class="{pos}" r="{r}" fill="{C[color]}" style="offset-path:path(\'{path}\')"/></g>')


def arrowhead(x, y, direction, cls, color="stroke"):
    d = {"left": f"M{x},{y} l12,-7 v14 z", "down": f"M{x},{y} l-7,-12 h14 z"}[direction]
    add(f'<path class="{cls}" d="{d}" fill="{C[color]}"/>')


def diagram(with_results):
    add(f'<g class="{window(0.2)}">' + text(60, 86, "PilotGate", 52, "ink", 700)
        + text(60, 124, "From a change to production, with evidence at every step", 24, "ink2") + "</g>")

    # Right column: the change's path. FDE, PilotGate in CI, its checks and verdict, Production Watch.
    RX, RW = 592, 428
    FY, PY, CY0, STEP, VY, WY = 172, 276, 370, 36, 604, 670
    add(node2(RX, FY, RW, 64, "Forward-deployed engineer", pop(0.6)))
    add(text(RX + 22, FY + 52, "changes a prompt, a model or code", 19, "ink2", cls=window(0.6, 9.45)))
    add(text(RX + 22, FY + 52, "fixes the agent, not the threshold", 19, "warning", 600, cls=window(9.8, 11.85)))
    add(text(RX + 22, FY + 52, "changes a prompt, a model or code", 19, "ink2", cls=window(12.2, 30.25)))
    add(text(RX + 22, FY + 52, "gets the alert: knows what to fix next", 19, "orange", 600, cls=window(30.6)))
    pr = f"M806,{FY + 64} L806,{PY - 6}"
    add(f'<path class="{window(1.0)}" d="{pr}" stroke="{C["stroke"]}" stroke-width="3" fill="none"/>')
    arrowhead(806, PY - 2, "down", window(1.0))
    add(text(818, FY + 88, "pull request", 17, "muted", cls=window(1.0)))
    add(node2(RX, PY, RW, 64, "PilotGate in CI", pop(0.9)))
    add(text(RX + 22, PY + 52, "28 scenarios on every pull request", 19, "ink2", cls=window(0.9)))
    checks = ["Permission trimming", "Prompt injection", "Human oversight", "PII protection", "Grounded answers", "Cost and latency", "Transparency"]
    for i, name in enumerate(checks):
        cy = CY0 + i * STEP
        appear = window(1.1 + i * 0.06)
        add(f'<circle class="{appear}" cx="{RX + 40}" cy="{cy}" r="13" fill="none" stroke="{C["stroke"]}" stroke-width="2"/>')
        add(text(RX + 64, cy + 8, name, 22, "ink2", cls=appear))
        if i == 0:   # the planted leak fails first; after the fix it passes
            add(check(RX + 40, cy, pop(7.0, 12.8), "critical", "cross", r=13))
            add(check(RX + 40, cy, pop(12.8), r=13))
        else:
            add(check(RX + 40, cy, pop(7.4 + (i - 1) * 0.3), r=13))
    add(f'<g class="{pop(9.2, 13.0)}"><rect x="{RX + 20}" y="{VY}" width="{RW - 40}" height="46" rx="23" fill="{C["critical"]}"/>'
        + text(RX + RW / 2, VY + 31, "✕  NO-GO: merge blocked", 22, "ink", 700, "middle") + "</g>")
    add(f'<g class="{pop(13.2)}"><rect x="{RX + 20}" y="{VY}" width="{RW - 40}" height="46" rx="23" fill="{C["good"]}"/>'
        + text(RX + RW / 2, VY + 31, "✓  GO WITH CONDITIONS", 22, "ink", 700, "middle") + "</g>")
    add(node2(RX, WY, RW, 64, "Production Watch", pop(1.4)))
    add(text(RX + 22, WY + 52, "drift alerts, every hour", 19, "ink2", cls=window(1.4, 28.85)))
    add(text(RX + 22, WY + 52, "drift alert sent to the FDE", 19, "orange", 600, cls=window(29.2)))
    loop = f"M{RX + RW},{WY + 32} C1046,{WY + 32} 1052,{WY + 20} 1052,{WY} L1052,{FY + 50} C1052,{FY + 38} 1040,{FY + 32} {RX + RW + 2},{FY + 32}"
    add(f'<path class="{window(2.0)}" d="{loop}" stroke="{C["stroke"]}" stroke-width="2" fill="none"/>')
    arrowhead(RX + RW, FY + 32, "left", window(2.0))
    fix = f"M{RX + RW - 20},{VY + 23} C1040,{VY + 23} 1052,{VY + 12} 1052,{VY - 6} L1052,{FY + 50} C1052,{FY + 38} 1040,{FY + 32} {RX + RW + 2},{FY + 32}"
    add(f'<path class="{window(9.6, 11.6)}" d="{fix}" stroke="{C["critical"]}" stroke-width="3" fill="none"/>')

    # Left column: production. Lit only after the release.
    LX, LW, NH, GAP, TOP = 112, 420, 70, 24, 172
    ys = [TOP + i * (NH + GAP) for i in range(5)]
    centres = [y + NH / 2 for y in ys]
    RAIL = 78
    names = [("Claims handler", "asks questions, approves payouts"), ("DIAL Core", "signs the user in, routes the request"),
             ("DIAL Guard", "checks the request and every token back"), ("ClaimsAssist agent", "RAG, tools, figure grounding"),
             ("Azure OpenAI", "Entra ID only, no keys")]
    add(f'<line class="{window(1.2)}" x1="{RAIL}" y1="{centres[0]}" x2="{RAIL}" y2="{centres[-1]}" stroke="{C["stroke"]}" stroke-width="3" stroke-linecap="round"/>')
    for i, ((title, sub), y) in enumerate(zip(names, ys)):
        cls = pop(1.2 + i * 0.2)
        add(f'<line class="{cls}" x1="{RAIL}" y1="{centres[i]}" x2="{LX}" y2="{centres[i]}" stroke="{C["stroke"]}" stroke-width="3"/>')
        add(node2(LX, y, LW, NH, title, cls))
        if i != 3:
            add(text(LX + 22, y + 55, sub, 19, "ink2", cls=window(1.2 + i * 0.2)))
    add(text(LX + 22, ys[3] + 55, names[3][1], 19, "ink2", cls=window(1.8, 23.45)))
    add(text(LX + 22, ys[3] + 55, "Waiting for the handler’s approval…", 19, "warning", 600, cls=window(23.8, 25.05)))
    add(text(LX + 22, ys[3] + 55, "Approved: guards re-run, QUEUED once", 19, "good", 600, cls=window(25.4, 26.85)))
    add(text(LX + 22, ys[3] + 55, names[3][1], 19, "ink2", cls=window(27.2)))
    boundary = pop(15.0)
    add(f'<rect class="{boundary}" x="48" y="152" width="500" height="486" rx="18" fill="none" stroke="{C["blue"]}" stroke-width="2" stroke-dasharray="9 7"/>')
    add(text(62, 664, "In production on Azure Container Apps", 18, "ink2", 600, cls=window(15.0)))

    # Between the columns: scenarios into the build under test, the release, the logs.
    scen = f"M{RX},{PY + 54} C566,{PY + 76} 566,{centres[3] - 20} {LX + LW + 8},{centres[3]}"
    add(f'<path class="{window(2.0)}" d="{scen}" stroke="{C["stroke"]}" stroke-width="3" fill="none"/>')
    arrowhead(LX + LW + 6, centres[3], "left", window(2.0))
    release = f"M{RX + 20},{VY + 23} L552,{VY + 23}"
    add(f'<path class="{window(14.4)}" d="{release}" stroke="{C["good"]}" stroke-width="3" fill="none"/>')
    arrowhead(550, VY + 23, "left", window(14.4), "good")
    add(text(580, VY + 8, "release", 17, "good", 600, "middle", cls=window(14.4)))
    logs = f"M{LX + LW},{centres[3] + 16} C574,{centres[3] + 30} 562,{WY + 30} {RX},{WY + 32}"
    add(f'<path class="{window(2.0)}" d="{logs}" stroke="{C["stroke"]}" stroke-width="2" fill="none"/>')

    # 1. The change.
    ring(RX, FY, RW, 64, "blue", 2.4, 5.2)
    dot_on(pr, 3.4, 0.8, "blue", 8)
    # 2. PilotGate runs the scenarios against the build; a leak means NO-GO and the FDE fixes it.
    ring(RX, PY, RW, 64, "blue", 5.0, 13.6)
    ring(LX, ys[3], LW, NH, "blue", 5.6, 13.6)
    for k in range(4):
        dot_on(scen, 5.4 + k * 0.28, 0.9, "blue")
    ring(RX, FY, RW, 64, "warning", 9.8, 12.0)
    dot_on(fix, 9.8, 1.0, "critical", 8)
    # 3. The re-run passes.
    for k in range(4):
        dot_on(scen, 11.8 + k * 0.2, 0.8, "blue")
    # 4. Release, then 5. a claims handler asks: down the chain and back, a check at each step.
    dot_on(release, 14.6, 0.7, "good", 8)
    down = [17.0, 18.0, 19.0, 20.0, 21.0]
    pk = anim([(t, f"translate({RAIL}px,{c}px)") for t, c in zip(down, centres)], "transform")
    add(f'<g class="{window(16.8, 21.2, 0.2)}"><g class="{pk}"><circle r="20" fill="{C["blue"]}" opacity=".25"/><circle r="10" fill="{C["blue"]}"/></g></g>')
    for i in range(1, 5):
        add(check(LX + LW - 30, centres[i], pop(down[i]), r=14))
    up = [21.4, 21.8, 22.2, 22.6, 23.0]
    pk2 = anim([(t, f"translate({RAIL}px,{c}px)") for t, c in zip(up, reversed(centres))], "transform")
    add(f'<g class="{window(21.3, 23.2, 0.2)}"><g class="{pk2}"><circle r="20" fill="{C["orange"]}" opacity=".25"/><circle r="10" fill="{C["orange"]}"/></g></g>')
    add(check(LX + LW - 30, centres[0], pop(23.0), r=14))
    # 6. A payout waits at the agent until the handler approves it.
    add(f'<g class="{window(23.8, 25.4, 0.2)}"><circle cx="{RAIL}" cy="{centres[3]}" r="22" fill="{C["warning"]}" opacity=".25"/><circle cx="{RAIL}" cy="{centres[3]}" r="10" fill="{C["warning"]}"/></g>')
    ap = anim([(24.6, f"translate({RAIL}px,{centres[0]}px)"), (25.3, f"translate({RAIL}px,{centres[3]}px)")], "transform")
    add(f'<g class="{window(24.5, 25.4, 0.1)}"><g class="{ap}"><circle r="9" fill="{C["good"]}"/></g></g>')
    # 7. The logs feed Production Watch; its alert goes back to the FDE.
    ring(RX, WY, RW, 64, "orange", 27.2)
    for k in range(5):
        dot_on(logs, 27.2 + k * 0.45, 1.1, "orange", 6)
    for k in range(3):
        dot_on(loop, 29.4 + k * 0.4, 1.0, "orange", 7)
    ring(RX, FY, RW, 64, "orange", 30.4)

    end9 = 31.4 if with_results else S["end"]
    narration([
        (2.4, 5.0, "1. The FDE changes the agent: a prompt,", "a model or code, as a pull request."),
        (5.0, 9.2, "2. In CI, PilotGate sends 28 scenarios to", "the agent and checks every answer."),
        (9.2, 11.6, "A data leak: NO-GO, the merge is blocked.", "The FDE fixes the agent, not the threshold."),
        (11.6, 14.4, "3. Re-run: every check passes. GO, with", "evidence for the CISO, DPO and works council."),
        (14.4, 16.6, "4. Only now is it released to production", "on Azure Container Apps."),
        (16.6, 20.6, "5. A claims handler asks a question: identity", "proven, planted text cut out, allowed sources only."),
        (20.6, 23.6, "The answer: every figure checked against its", "source, personal data masked, sources cited."),
        (23.6, 27.0, "6. A payout waits for the handler’s approval,", "and the guards check it again before it runs."),
        (27.0, end9, "7. Production Watch turns logs into drift", "alerts for the FDE, and the loop starts again."),
    ])
    if with_results:
        stat_chips(31.6)
        add(text(60, 1012, "Open source: github.com/CodeMitVishal/PilotGate", 26, "ink2", 600, cls=window(33.0)))


# ---- Scene: what it costs, by hand versus with PilotGate (sourced) ----------------------------------------------
# By hand: typical vendor prices and durations for one AI security test (BD Emerson, Repello, Schellman price guides,
# 2025-26) and IBM's Cost of a Data Breach 2025. With PilotGate: measured in gate runs 1 to 10 (docs/evidence).
def outro():
    add(text(60, 96, "WHAT IT COSTS", 22, "muted", 600, cls=window(0.2), spacing="3"))
    add(text(60, 158, "Time, money and effort: by hand vs PilotGate", 40, "ink", 700, cls=window(0.4)))
    head = window(0.7)
    add(text(236, 232, "Today, by hand", 24, "muted", 600, cls=head))
    add(text(640, 232, "With PilotGate", 24, "ink", 700, cls=head))
    add(f'<rect class="{head}" x="640" y="243" width="178" height="4" rx="2" fill="{C["blue"]}"/>')
    rows = [
        ("TIME", ("2–4 weeks", "per AI security test"), ("1.4 minutes", "per full run: 28 scenarios, 7 checks")),
        ("COST", ("$10,000–40,000", "per AI security test"), ("$0.11", "per run, at Azure list price")),
        ("EFFORT", ("4 sign-offs, by hand", "CISO, DPO, sponsor, works council"), ("One evidence pack", "with a page for each reviewer")),
        ("COVERAGE", ("Done once", "out of date after the next change"), ("Every pull request", "a NO-GO blocks the merge")),
        ("QUALITY", ("Depends on the reviewer", "an AI judge passed a phishing answer"), ("Expert rules in code", "OWASP, GDPR and EU AI Act checks")),
        ("RISK", ("$4.44M", "average cost of a data breach"), ("6 real defects stopped", "before any user saw them")),
    ]
    for i, (aspect, (b1, b2), (a1, a2)) in enumerate(rows):
        y, t0 = 268 + i * 74, 1.0 + i * 0.9
        add(f'<line class="{window(t0)}" x1="60" y1="{y + 60}" x2="1020" y2="{y + 60}" stroke="{C["grid"]}" stroke-width="1"/>')
        add(text(60, y + 26, aspect, 17, "muted", 700, cls=window(t0), spacing="2"))
        add(text(236, y + 26, b1, 25, "ink2", 600, cls=window(t0)))
        add(text(236, y + 50, b2, 18, "muted", cls=window(t0)))
        add(text(606, y + 26, "→", 26, "muted", 400, "middle", cls=window(t0 + .2)))
        add(check(652, y + 18, pop(t0 + .35), r=12))
        add(text(676, y + 26, a1, 25, "ink", 700, cls=window(t0 + .35)))
        add(text(676, y + 50, a2, 18, "ink2", cls=window(t0 + .35)))
    src = window(6.6)
    add(text(60, 728, "Sources: typical vendor prices for an AI security test, 2025–26; IBM Cost of a Data Breach 2025.", 17, "muted", cls=src))
    add(text(60, 752, "PilotGate figures are measured in its 10 gate runs (docs/evidence); the phishing answer is run 2.", 17, "muted", cls=src))
    t = window(7.6)
    add(text(60, 900, "Free and open source under the MIT licence. Gate your own agent:", 26, "ink2", cls=t))
    add(text(60, 950, "github.com/CodeMitVishal/PilotGate", 38, "ink", 700, cls=t))


def build(name, dur, scenes, title, desc, still):
    S.update(dur=dur, n=0, css=[], body=[f'<rect width="{W}" height="{H}" fill="{C["bg"]}"/>'])
    for fn, shift, end in scenes:
        S.update(shift=shift, end=end)
        fn()
    svg = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" '
           f'aria-labelledby="title desc" font-family="{FONT}">\n<title id="title">{esc(title)}</title>\n<desc id="desc">{esc(desc)}</desc>\n'
           f'<style>.pop{{transform-box:fill-box;transform-origin:center}}{"".join(S["css"])}'
           # Reduced motion: hold one frame that tells the whole story instead of moving.
           f'@media (prefers-reduced-motion: reduce){{*{{animation-delay:-{still}s!important;animation-play-state:paused!important}}}}</style>\n'
           + "\n".join(S["body"]) + "\n</svg>\n")
    path = os.path.join(HERE, name)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(svg)
    print("wrote", os.path.relpath(path), f"({len(svg) // 1024} KB, {S['n']} animations, {dur:.0f} s loop)")


DIAGRAM_DESC = ("1. A forward-deployed engineer changes the agent in a pull request. 2. In CI, PilotGate sends 28 scenarios to "
                "the agent and decides seven checks: a planted data leak gives NO-GO and blocks the merge, and the engineer fixes "
                "the agent. 3. The re-run gives GO WITH CONDITIONS, with evidence for each reviewer. 4. Only then is it released "
                "to Azure Container Apps. 5. A claims handler's question passes DIAL Core (sign-in), DIAL Guard (every streamed "
                "token checked), the ClaimsAssist agent and Azure OpenAI (Entra ID, no keys), each step checked. 6. A payout waits "
                "for the handler's approval and the guards run again. 7. Production Watch turns the logs into drift alerts for "
                "the engineer, and the loop starts again.")
RESULTS = "Results: 1.4 minutes and $0.11 per gate run, 6 real defects stopped, no keys for Azure (Microsoft Entra ID only)."

if __name__ == "__main__":
    build("architecture-animated.svg", 36.4, [(lambda: diagram(True), 0.0, 35.8)],
          "PilotGate architecture, animated", DIAGRAM_DESC + " " + RESULTS, 34.8)
    build("pilotgate-explainer.svg", 58.8, [(intro, 0.0, 10.5), (lambda: diagram(False), 11.0, 31.4), (outro, 42.8, 15.4)],
          "PilotGate: AI pilot approval by hand versus with PilotGate",
          "Today, a pilot is approved by hand: manual security reviews, GDPR and AI Act checklists, one-off red-team tests, "
          "screenshots as evidence and weeks of waiting, and it goes out of date with the next change. " + DIAGRAM_DESC
          + " Compared (typical vendor prices, IBM 2025, and PilotGate's measured runs): an AI security test takes 2 to 4 weeks and "
          "costs $10,000 to $40,000, a PilotGate run 1.4 minutes and $0.11; four sign-offs by hand versus one evidence pack; done "
          "once versus every pull request; quality that depends on the reviewer versus expert rules in code; a data breach costs "
          "$4.44M on average, and PilotGate stopped 6 real defects before any user saw them. 88% of AI pilots never reach "
          "production (IDC with Lenovo, 2025). " + RESULTS, 57.0)
