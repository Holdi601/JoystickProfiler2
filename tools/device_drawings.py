"""JoyPro's own vector drawings of devices, authored from specs (docs/device-reference.md, "JoyPro's drawings").

A spec (tools/device_drawings/<model>.<view>.json) describes the device as clean vector shapes: the silhouette, the
body parts that make it recognizable, and every control as a glyph of the shared library (a hat with its four or eight
directions, a button, a ministick, a toggle, a rocker, a wheel, an encoder, a slider ...). Nothing is traced from a
photo: the maker's photos and button maps are only the reference for proportions and positions. Because each control is
drawn at its marker, the positions are exact by construction.

The tool writes the drawing (EngineOnly_Runtime/DB/DeviceDrawings/<model>.<view>.json, format joypro-device-drawing,
in its own frame) and its position set (DB/DeviceLayouts/<model>.drawing-<view>.json, photo kind "lineart": the set
names the drawing's key) with the callout sheet the engine arranges for it.

    python tools/device_drawings.py build [spec ...] [--all] [--preview <dir>] [--no-sheet]
    python tools/device_drawings.py preview <model> [--view v] [--out dir]     the drawing and its sheet as PNG
    python tools/device_drawings.py glyphs [--out dir]                         the glyph library on one sheet
    python tools/device_drawings.py list                                       the specs and what they place

Spec format (all coordinates in the drawing's own pixels):
    { "model": "<catalog id>", "view": "main", "class": "stick", "width": 1600, "height": 1200,
      "source": "...", "mappingSource": "...", "confidence": "medium", "note": "...",
      "colors": { "body": "#2B2B2B" },                      named colours (else the palette names below)
      "shapes":   [ shape, ... ],                           drawn in order (fills under all strokes)
      "controls": [ { "part": "stick.hat.tms", "glyph": "hat4", "at": [x, y], "size": 40, "angle": 0,
                      "marker": [x, y], "fill": "cap", "press": true }, ... ],
      "unplaced": { "<part>": "why it is not drawn" },
      "replaces": [ "<old drawing file name>" ] }
    shape: { "type": "path", "pts": [[x, y], [x, y, "c"] ...], "closed": true }   smooth curve, "c" = a corner
           { "type": "poly" | "line", "pts": [...] }   { "type": "circle", "c": [x, y], "r": r }
           { "type": "ellipse", "c": [x, y], "rx": a, "ry": b, "angle": deg }
           { "type": "rrect", "c": [x, y], "w": w, "h": h, "r": radius, "angle": deg }
           { "type": "arc", "c": [x, y], "r": r, "from": deg, "to": deg }
           { "type": "group", "dx": 0, "dy": 0, "scale": 1, "mirror": false, "items": [shape | control ...] }
      every shape: "fill": colour name or #RRGGBB (optional), "role": "outline" | "detail" | "minor" | "none"
"""
import argparse
import glob
import hashlib
import json
import math
import os
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DB = os.path.join(REPO, "EngineOnly_Runtime", "DB")
DRAWINGS = os.path.join(DB, "DeviceDrawings")
LAYOUTS = os.path.join(DB, "DeviceLayouts")
SPECS = os.path.join(REPO, "tools", "device_drawings")
FORMAT = "joypro-device-drawing"

# The cel palette (the same small fixed palette as the other drawings).
PALETTE = {
    "black": "#141414", "body": "#2B2B2B", "bezel": "#444444", "dark": "#444444", "mid": "#666666", "grey": "#8C8C8C",
    "cap": "#B4B4B4", "light": "#D9D9D9", "white": "#F2F2F2", "red": "#D32F2F", "orange": "#EF7D22", "yellow": "#F2C230",
    "green": "#3BAA4A", "blue": "#2F6FD0", "sky": "#6FB7E9", "teal": "#29B6C8", "brown": "#A0703C",
}


def fail(message):
    print("error: " + message, file=sys.stderr)
    sys.exit(2)


def fmt(v):
    s = "%.1f" % v
    return s[:-2] if s.endswith(".0") else ("0" if s == "-0" else s)


def pt(p):
    return fmt(p[0]) + "," + fmt(p[1])


# --------------------------------------------------------------------------------------------------- geometry

class Frame:
    """A placement: offset, uniform scale and an optional mirror about x = 0 (applied before the offset)."""

    def __init__(self, dx=0.0, dy=0.0, scale=1.0, mirror=False, rotate=0.0, parent=None):
        self.dx, self.dy, self.scale, self.mirror, self.rotate, self.parent = dx, dy, scale, mirror, rotate, parent

    def apply(self, p):
        x, y = p[0], p[1]
        if self.mirror:
            x = -x
        if self.rotate:
            a = math.radians(self.rotate)
            x, y = x * math.cos(a) - y * math.sin(a), x * math.sin(a) + y * math.cos(a)
        x, y = x * self.scale + self.dx, y * self.scale + self.dy
        return self.parent.apply((x, y)) if self.parent else (x, y)

    def length(self, v):
        return v * self.scale * (self.parent.length(1.0) if self.parent else 1.0)

    def angle(self, a):
        a = (180 - a) if self.mirror else a
        a += self.rotate
        return self.parent.angle(a) if self.parent else a

    def mirrored(self):
        return self.mirror != (self.parent.mirrored() if self.parent else False)


IDENTITY = Frame()


def catmull_rom(points, closed):
    """Absolute path data (M C ... [Z]) of a smooth curve through the points; a point marked "c" is a corner."""
    pts = [(float(p[0]), float(p[1])) for p in points]
    corner = [len(p) > 2 and p[2] == "c" for p in points]
    n = len(pts)
    if n < 2:
        return ""
    out = ["M" + pt(pts[0])]
    count = n if closed else n - 1
    for i in range(count):
        p0 = pts[(i - 1) % n] if (closed or i > 0) else pts[i]
        p1 = pts[i]
        p2 = pts[(i + 1) % n]
        p3 = pts[(i + 2) % n] if (closed or i + 2 < n) else p2
        t = 1.0 / 6.0
        c1 = (p1[0] + (p2[0] - p0[0]) * t, p1[1] + (p2[1] - p0[1]) * t)
        c2 = (p2[0] - (p3[0] - p1[0]) * t, p2[1] - (p3[1] - p1[1]) * t)
        if corner[i]:
            c1 = (p1[0] + (p2[0] - p1[0]) / 3.0, p1[1] + (p2[1] - p1[1]) / 3.0)
        if corner[(i + 1) % n]:
            c2 = (p2[0] - (p2[0] - p1[0]) / 3.0, p2[1] - (p2[1] - p1[1]) / 3.0)
        if corner[i] and corner[(i + 1) % n]:
            out.append("L" + pt(p2))
        else:
            out.append("C" + pt(c1) + " " + pt(c2) + " " + pt(p2))
    if closed:
        out.append("Z")
    return "".join(out)


def ellipse_d(cx, cy, rx, ry, angle=0.0):
    k = 0.5522847498
    a = math.radians(angle)
    c, s = math.cos(a), math.sin(a)

    def P(x, y):
        return (cx + x * c - y * s, cy + x * s + y * c)
    q = [P(rx, 0), P(rx, k * ry), P(k * rx, ry), P(0, ry), P(-k * rx, ry), P(-rx, k * ry), P(-rx, 0), P(-rx, -k * ry),
         P(-k * rx, -ry), P(0, -ry), P(k * rx, -ry), P(rx, -k * ry)]
    d = "M" + pt(q[0])
    for i in range(4):
        d += "C" + pt(q[3 * i + 1]) + " " + pt(q[3 * i + 2]) + " " + pt(q[(3 * i + 3) % 12])
    return d + "Z"


def rrect_pts(cx, cy, w, h, r, angle):
    """The corners of a rounded rectangle as a closed list of (point, kind) for the path builder."""
    r = max(0.0, min(r, w / 2.0, h / 2.0))
    a = math.radians(angle)
    c, s = math.cos(a), math.sin(a)

    def P(x, y):
        return (cx + x * c - y * s, cy + x * s + y * c)
    hw, hh = w / 2.0, h / 2.0
    return P, hw, hh, r


def rrect_d(cx, cy, w, h, r, angle=0.0):
    P, hw, hh, r = rrect_pts(cx, cy, w, h, r, angle)
    if r <= 0.01:
        q = [P(-hw, -hh), P(hw, -hh), P(hw, hh), P(-hw, hh)]
        return "M" + pt(q[0]) + "".join("L" + pt(x) for x in q[1:]) + "Z"
    k = 0.5522847498 * r
    d = "M" + pt(P(-hw + r, -hh))
    d += "L" + pt(P(hw - r, -hh)) + "C" + pt(P(hw - r + k, -hh)) + " " + pt(P(hw, -hh + r - k)) + " " + pt(P(hw, -hh + r))
    d += "L" + pt(P(hw, hh - r)) + "C" + pt(P(hw, hh - r + k)) + " " + pt(P(hw - r + k, hh)) + " " + pt(P(hw - r, hh))
    d += "L" + pt(P(-hw + r, hh)) + "C" + pt(P(-hw + r - k, hh)) + " " + pt(P(-hw, hh - r + k)) + " " + pt(P(-hw, hh - r))
    d += "L" + pt(P(-hw, -hh + r)) + "C" + pt(P(-hw, -hh + r - k)) + " " + pt(P(-hw + r - k, -hh)) + " " + pt(P(-hw + r, -hh))
    return d + "Z"


def arc_d(cx, cy, r, a0, a1):
    """An open circular arc from a0 to a1 degrees (0 = right, clockwise on screen), as cubic pieces."""
    steps = max(1, int(math.ceil(abs(a1 - a0) / 90.0)))
    d = ""
    for i in range(steps):
        t0 = math.radians(a0 + (a1 - a0) * i / steps)
        t1 = math.radians(a0 + (a1 - a0) * (i + 1) / steps)
        k = 4.0 / 3.0 * math.tan((t1 - t0) / 4.0)
        p0 = (cx + r * math.cos(t0), cy + r * math.sin(t0))
        p3 = (cx + r * math.cos(t1), cy + r * math.sin(t1))
        c1 = (p0[0] - k * r * math.sin(t0), p0[1] + k * r * math.cos(t0))
        c2 = (p3[0] + k * r * math.sin(t1), p3[1] - k * r * math.cos(t1))
        if i == 0:
            d += "M" + pt(p0)
        d += "C" + pt(c1) + " " + pt(c2) + " " + pt(p3)
    return d


def transform_d(d, frame):
    """Applies a frame to absolute path data (M L Q C Z)."""
    if frame is IDENTITY:
        return d
    out, i, op = [], 0, None
    tokens = d.replace(",", " ").replace("M", " M ").replace("L", " L ").replace("C", " C ").replace("Q", " Q ").replace("Z", " Z ").split()
    nums = []

    def flush():
        pass
    j = 0
    while j < len(tokens):
        t = tokens[j]
        if t in ("M", "L", "C", "Q", "Z"):
            op = t
            if t == "Z":
                out.append("Z")
            j += 1
            continue
        count = {"M": 1, "L": 1, "Q": 2, "C": 3}[op]
        pairs = []
        for _ in range(count):
            x, y = float(tokens[j]), float(tokens[j + 1])
            j += 2
            pairs.append(pt(frame.apply((x, y))))
        out.append(op + " ".join(pairs))
        if op == "M":
            op = "L"
    return "".join(out)


# --------------------------------------------------------------------------------------------------- the glyph library

def col(name, colors):
    if name is None:
        return None
    if isinstance(name, str) and name.startswith("#"):
        return name.upper()
    if name in colors:
        return col(colors[name], {}) if colors[name] != name else None
    if name in PALETTE:
        return PALETTE[name]
    fail("unknown colour '%s'" % name)


def tri(cx, cy, size, angle):
    """A small arrow head pointing at angle (degrees, 0 = right, 90 = down)."""
    a = math.radians(angle)
    tip = (cx + size * math.cos(a), cy + size * math.sin(a))
    b1 = (cx + size * 0.7 * math.cos(a + 2.4), cy + size * 0.7 * math.sin(a + 2.4))
    b2 = (cx + size * 0.7 * math.cos(a - 2.4), cy + size * 0.7 * math.sin(a - 2.4))
    return [list(tip) + ["c"], list(b1) + ["c"], list(b2) + ["c"]]


def glyph_shapes(c):
    """The shapes of a control glyph, centred at 0,0 with its size (the radius it covers) as s, angle 0 = upright."""
    g = c.get("glyph", "button")
    s = float(c.get("size", 20))
    fill = c.get("fill")
    out = []

    def add(shape, **kw):
        shape.update(kw)
        out.append(shape)

    if g in ("button", "round"):
        add({"type": "circle", "c": [0, 0], "r": s}, fill=c.get("bezel", "bezel"), role="detail")
        add({"type": "circle", "c": [0, 0], "r": s * 0.68}, fill=fill or "cap", role="detail")
    elif g == "square":
        add({"type": "rrect", "c": [0, 0], "w": 2 * s, "h": 2 * s, "r": s * 0.25}, fill=c.get("bezel", "bezel"), role="detail")
        add({"type": "rrect", "c": [0, 0], "w": 1.4 * s, "h": 1.4 * s, "r": s * 0.18}, fill=fill or "cap", role="detail")
    elif g == "key":
        w, h = float(c.get("w", 2 * s)), float(c.get("h", 2 * s))
        add({"type": "rrect", "c": [0, 0], "w": w, "h": h, "r": min(w, h) * 0.18}, fill=fill or "grey", role="detail")
    elif g in ("hat4", "hat8", "hat"):
        dirs = 8 if g == "hat8" else 4
        add({"type": "circle", "c": [0, 0], "r": s}, fill=c.get("bezel", "bezel"), role="detail")
        add({"type": "circle", "c": [0, 0], "r": s * 0.58}, fill=fill or "cap", role="detail")
        for k in range(dirs):
            ang = -90 + k * (360.0 / dirs)
            big = k % 2 == 0 or dirs == 4
            r0 = s * 0.79
            size = s * (0.27 if big else 0.17)
            cx, cy = r0 * math.cos(math.radians(ang)), r0 * math.sin(math.radians(ang))
            add({"type": "poly", "pts": tri(cx, cy, size, ang)}, fill="white", role="minor")
        if c.get("press", True):
            add({"type": "circle", "c": [0, 0], "r": s * 0.16}, fill="bezel", role="minor")
    elif g == "ministick":
        add({"type": "circle", "c": [0, 0], "r": s}, fill=c.get("bezel", "bezel"), role="detail")
        add({"type": "circle", "c": [0, 0], "r": s * 0.56}, fill=fill or "grey", role="detail")
        add({"type": "circle", "c": [0, 0], "r": s * 0.36}, fill="cap", role="minor")
        add({"type": "circle", "c": [0, 0], "r": s * 0.14}, fill="grey", role="minor")
        for k in range(8):
            ang = math.radians(-90 + k * 45)
            r0, r1 = s * 0.68, s * (0.9 if k % 2 == 0 else 0.8)
            add({"type": "line", "pts": [[r0 * math.cos(ang), r0 * math.sin(ang)], [r1 * math.cos(ang), r1 * math.sin(ang)]]}, role="minor")
    elif g == "toggle":
        # A toggle seen from above at an angle: the nut, the bat lever leaning to its position.
        lean = float(c.get("lean", 0.0))
        add({"type": "circle", "c": [0, 0], "r": s}, fill=c.get("bezel", "bezel"), role="detail")
        add({"type": "circle", "c": [0, 0], "r": s * 0.55}, fill="grey", role="minor")
        tip = (0, -s * (1.25 if lean == 0 else 1.25 * lean))
        add({"type": "rrect", "c": [0, tip[1] / 2], "w": s * 0.42, "h": abs(tip[1]) + s * 0.1, "r": s * 0.2}, fill=fill or "light", role="detail")
        add({"type": "circle", "c": [0, tip[1]], "r": s * 0.3}, fill=fill or "light", role="detail")
    elif g == "rocker":
        w, h = float(c.get("w", s * 1.1)), float(c.get("h", s * 2.2))
        add({"type": "rrect", "c": [0, 0], "w": w + s * 0.3, "h": h + s * 0.3, "r": w * 0.3}, fill=c.get("bezel", "bezel"), role="detail")
        add({"type": "rrect", "c": [0, 0], "w": w, "h": h, "r": w * 0.25}, fill=fill or "grey", role="detail")
        add({"type": "line", "pts": [[-w * 0.35, 0], [w * 0.35, 0]]}, role="minor")
        add({"type": "poly", "pts": tri(0, -h * 0.28, w * 0.22, -90)}, fill="white", role="minor")
        add({"type": "poly", "pts": tri(0, h * 0.28, w * 0.22, 90)}, fill="white", role="minor")
    elif g == "wheel":
        w, h = float(c.get("w", s * 0.9)), float(c.get("h", s * 2.2))
        add({"type": "rrect", "c": [0, 0], "w": w, "h": h, "r": w * 0.45}, fill=fill or "grey", role="detail")
        ribs = int(c.get("ribs", 5))
        for k in range(ribs):
            y = -h * 0.36 + k * (h * 0.72 / max(1, ribs - 1))
            add({"type": "line", "pts": [[-w * 0.34, y], [w * 0.34, y]]}, role="minor")
    elif g in ("encoder", "knob"):
        add({"type": "circle", "c": [0, 0], "r": s}, fill=fill or "grey", role="detail")
        add({"type": "circle", "c": [0, 0], "r": s * 0.72}, fill="mid", role="minor")
        if g == "encoder":
            for k in range(12):
                a = math.radians(k * 30)
                add({"type": "line", "pts": [[s * 0.78 * math.cos(a), s * 0.78 * math.sin(a)], [s * 0.98 * math.cos(a), s * 0.98 * math.sin(a)]]}, role="minor")
        add({"type": "line", "pts": [[0, -s * 0.15], [0, -s * 0.66]]}, role="detail")
        if c.get("turn", g == "knob"):
            add({"type": "arc", "c": [0, 0], "r": s * 1.3, "from": -150, "to": -30}, role="minor")
            add({"type": "poly", "pts": tri(s * 1.3 * math.cos(math.radians(-30)), s * 1.3 * math.sin(math.radians(-30)), s * 0.22, 60)}, fill="light", role="minor")
    elif g == "slider":
        w, h = float(c.get("w", s * 0.6)), float(c.get("h", s * 3.0))
        add({"type": "rrect", "c": [0, 0], "w": w, "h": h, "r": w * 0.5}, fill="black", role="detail")
        add({"type": "rrect", "c": [0, float(c.get("knob", 0)) * h * 0.35], "w": w * 2.2, "h": w * 1.2, "r": w * 0.3}, fill=fill or "light", role="detail")
    elif g == "lever":
        # An axis lever's travel: a slot with the handle and a double arrow.
        w, h = float(c.get("w", s * 0.7)), float(c.get("h", s * 3.0))
        add({"type": "rrect", "c": [0, 0], "w": w, "h": h, "r": w * 0.5}, fill="black", role="detail")
        add({"type": "line", "pts": [[w * 1.3, -h * 0.4], [w * 1.3, h * 0.4]]}, role="minor")
        add({"type": "poly", "pts": tri(w * 1.3, -h * 0.42, w * 0.45, -90)}, fill="light", role="minor")
        add({"type": "poly", "pts": tri(w * 1.3, h * 0.42, w * 0.45, 90)}, fill="light", role="minor")
    elif g == "gimbal":
        # The base's pitch and roll: four arrows around the column.
        for k in range(4):
            ang = -90 + k * 90
            x, y = s * math.cos(math.radians(ang)), s * math.sin(math.radians(ang))
            add({"type": "line", "pts": [[x * 0.4, y * 0.4], [x * 0.8, y * 0.8]]}, role="outline")
            add({"type": "poly", "pts": tri(x * 0.88, y * 0.88, s * 0.24, ang)}, fill="light", role="outline")
    elif g == "twist":
        add({"type": "arc", "c": [0, 0], "r": s, "from": 205, "to": 335}, role=c.get("role", "outline"))
        add({"type": "poly", "pts": tri(s * math.cos(math.radians(338)), s * math.sin(math.radians(338)), s * 0.2, 70)}, fill="light", role=c.get("role", "outline"))
        add({"type": "poly", "pts": tri(s * math.cos(math.radians(202)), s * math.sin(math.radians(202)), s * 0.2, 110)}, fill="light", role=c.get("role", "outline"))
    elif g == "pedal":
        add({"type": "line", "pts": [[-s * 0.8, 0], [s * 0.8, 0]]}, role=c.get("role", "outline"))
        add({"type": "poly", "pts": tri(-s * 0.9, 0, s * 0.22, 180)}, fill="light", role=c.get("role", "outline"))
        add({"type": "poly", "pts": tri(s * 0.9, 0, s * 0.22, 0)}, fill="light", role=c.get("role", "outline"))
    elif g == "dot":
        add({"type": "circle", "c": [0, 0], "r": s * 0.3}, fill=fill or "light", role="minor")
    elif g == "none":
        pass
    else:
        fail("unknown glyph '%s'" % g)
    return out


def resample(line, step):
    out = [line[0]]
    for a, b in zip(line, line[1:]):
        dist = math.hypot(b[0] - a[0], b[1] - a[1])
        n = max(1, int(dist / step))
        for k in range(1, n + 1):
            out.append((a[0] + (b[0] - a[0]) * k / n, a[1] + (b[1] - a[1]) * k / n))
    return out


def inside(points, poly):
    """Even-odd point in polygon for many points (numpy); a point within 0.6 px of the edge counts as outside."""
    import numpy as np
    x, y = points[:, 0][:, None], points[:, 1][:, None]
    x1, y1 = poly[:, 0][None, :], poly[:, 1][None, :]
    x2, y2 = np.roll(poly[:, 0], -1)[None, :], np.roll(poly[:, 1], -1)[None, :]
    cond = (y1 > y) != (y2 > y)
    with np.errstate(divide="ignore", invalid="ignore"):
        xs = (x2 - x1) * (y - y1) / (y2 - y1) + x1
    crossings = np.sum(cond & (x < xs), axis=1)
    result = crossings % 2 == 1
    # Near the edge: visible (the outline of the front part itself is drawn there).
    dx, dy = x2 - x1, y2 - y1
    length = dx * dx + dy * dy
    with np.errstate(divide="ignore", invalid="ignore"):
        t = np.clip(((x - x1) * dx + (y - y1) * dy) / np.where(length == 0, 1, length), 0, 1)
    dist = np.min((x1 + t * dx - x) ** 2 + (y1 + t * dy - y) ** 2, axis=1)
    return result & (dist > 0.6 * 0.6)


def rdp(points, eps):
    if len(points) < 3:
        return points
    a, b = points[0], points[-1]
    dx, dy = b[0] - a[0], b[1] - a[1]
    norm = math.hypot(dx, dy)
    if norm < 1e-6:
        # A closed run: split at the point farthest from its start.
        far = max(range(len(points)), key=lambda i: (points[i][0] - a[0]) ** 2 + (points[i][1] - a[1]) ** 2)
        if far == 0 or far == len(points) - 1:
            return [a, b]
        return rdp(points[:far + 1], eps)[:-1] + rdp(points[far:], eps)
    best, idx = 0.0, 0
    for i in range(1, len(points) - 1):
        p = points[i]
        dist = abs(dy * (p[0] - a[0]) - dx * (p[1] - a[1])) / norm
        if dist > best:
            best, idx = dist, i
    if best <= eps:
        return [a, b]
    return rdp(points[:idx + 1], eps)[:-1] + rdp(points[idx:], eps)


# --------------------------------------------------------------------------------------------------- spec -> drawing

class Doc:
    def __init__(self, colors):
        self.colors = colors
        self.paths = []
        self.fills = []
        self.markers = {}
        self.controls = []
        self.items = []
        self.mapping = {}
        self.unmapped = []
        self.photo_markers = {}

    def finalize(self):
        """Hidden lines: a stroke is cut where a shape drawn after it covers it (the painter's order of the spec), so a
        part in front of the body hides the body's outline behind it although the format draws every fill first."""
        import numpy as np
        occluders = []
        for kind, order, d in self.items:
            if kind != "fill":
                continue
            for line, closed in flatten_d(d, 16):
                if len(line) > 2:
                    a = np.array(line)
                    occluders.append((order, a, a.min(axis=0), a.max(axis=0)))
        out = []
        for p in self.paths:
            order = p.pop("order")
            lines = flatten_d(p["d"], 24)
            if not lines:
                continue
            allpts = np.array([q for line, _ in lines for q in line])
            lo, hi = allpts.min(axis=0), allpts.max(axis=0)
            later = [o for o in occluders if o[0] > order and not (o[3][0] < lo[0] or o[2][0] > hi[0] or o[3][1] < lo[1] or o[2][1] > hi[1])]
            if not later:
                out.append(p)
                continue
            pieces = []
            for line, closed in lines:
                pts = resample(line, 1.0)
                if len(pts) < 2:
                    continue
                arr = np.array(pts)
                hidden = np.zeros(len(arr), bool)
                for _, poly, mn, mx in later:
                    hidden |= inside(arr, poly)
                run = []
                for q, h in zip(pts, hidden):
                    if h:
                        if len(run) > 1:
                            pieces.append(run)
                        run = []
                    else:
                        run.append(q)
                if len(run) > 1:
                    pieces.append(run)
            if not pieces:
                continue
            d = ""
            for piece in pieces:
                piece = rdp(piece, 0.35)
                d += "M" + pt(piece[0]) + "".join("L" + pt(q) for q in piece[1:])
            out.append({"role": p["role"], "d": d})
        self.paths = out

    def shape(self, sh, frame):
        t = sh.get("type")
        if t == "group":
            sub = Frame(float(sh.get("dx", 0)), float(sh.get("dy", 0)), float(sh.get("scale", 1)), bool(sh.get("mirror", False)),
                        float(sh.get("rotate", 0)), frame if frame is not IDENTITY else None)
            # A group drawn in the frame of a maker's photo (its pixels): its parts also go to that photo's curated set.
            sub.photo = (sh["photoSet"], float(sh["photoSize"][0]), float(sh["photoSize"][1])) if sh.get("photoSet") else None
            for item in sh.get("items", []):
                if "part" in item or "glyph" in item or "key" in item:
                    self.control(item, sub)
                else:
                    self.shape(item, sub)
            return
        d, closed = self.path_of(sh, frame)
        if not d:
            return
        fill = col(sh.get("fill"), self.colors)
        role = sh.get("role", "detail")
        order = len(self.items)
        if fill and closed:
            self.fills.append({"color": fill, "rule": sh.get("rule", "nonzero"), "d": d})
            if sh.get("occludes", True):
                self.items.append(("fill", order, d))
        if role != "none":
            self.paths.append({"role": role, "d": d, "order": order})
        self.items.append(("stroke", order, d))

    def path_of(self, sh, frame):
        t = sh.get("type")
        L = frame.length
        if t == "raw":
            # Path data in the shape's own frame (absolute M L Q C Z), e.g. a game model's render made into a part.
            return transform_d(sh["d"], frame), "Z" in sh["d"]
        if t == "path":
            pts = [list(frame.apply(p)) + list(p[2:]) for p in sh["pts"]]
            closed = bool(sh.get("closed", True))
            return catmull_rom(pts, closed), closed
        if t in ("poly", "line"):
            pts = [frame.apply(p) for p in sh["pts"]]
            closed = t == "poly"
            return "M" + pt(pts[0]) + "".join("L" + pt(p) for p in pts[1:]) + ("Z" if closed else ""), closed
        if t == "circle":
            c = frame.apply(sh["c"])
            return ellipse_d(c[0], c[1], L(sh["r"]), L(sh["r"])), True
        if t == "ellipse":
            c = frame.apply(sh["c"])
            return ellipse_d(c[0], c[1], L(sh["rx"]), L(sh["ry"]), frame.angle(float(sh.get("angle", 0)))), True
        if t == "rrect":
            c = frame.apply(sh["c"])
            return rrect_d(c[0], c[1], L(sh["w"]), L(sh["h"]), L(sh.get("r", 0)), frame.angle(float(sh.get("angle", 0)))), True
        if t == "arc":
            c = frame.apply(sh["c"])
            a0, a1 = float(sh["from"]), float(sh["to"])
            if frame.mirrored():
                a0, a1 = 180 - a1, 180 - a0
            a0 += frame.angle(0) if not frame.mirrored() else frame.angle(0) - 180
            a1 += frame.angle(0) if not frame.mirrored() else frame.angle(0) - 180
            return arc_d(c[0], c[1], L(sh["r"]), a0, a1), False
        fail("unknown shape type '%s'" % t)

    def control(self, c, frame):
        at = c.get("at")
        if at is None:
            fail("control %s has no position" % c.get("part"))
        angle = float(c.get("angle", 0))
        sub = Frame(float(at[0]), float(at[1]), 1.0, False, angle, frame if frame is not IDENTITY else None)
        for sh in glyph_shapes(c):
            self.shape(sh, sub)
        for extra in c.get("shapes", []):
            self.shape(extra, sub)
        part = c.get("part")
        if part is None and c.get("key") is not None:
            # A shared drawing's control: the spec's map names the model's part(s) for this physical control.
            part = self.mapping.get(c["key"])
            if part is None:
                self.unmapped.append(c["key"])
        parts = part if isinstance(part, list) else ([part] if part else [])
        m = c.get("marker")
        base = frame.apply(m) if m is not None else frame.apply(at)
        photo = getattr(frame, "photo", None)
        for k, one in enumerate(parts):
            if photo:
                local = m if m is not None else at
                self.photo_markers.setdefault(photo[0], {})[one] = [round(float(local[0]) / photo[1], 4), round(float(local[1]) / photo[2], 4)]
            if one in self.markers:
                fail("part %s is drawn twice" % one)
            # Several parts of one control (a knob that tilts and turns): one spot, a hair apart.
            self.markers[one] = (base[0] + 2.0 * k, base[1] + 1.5 * k)
            self.controls.append(one)


def opaque_key(spec):
    raw = "joypro-vector-drawing:" + spec["model"] + "." + spec.get("view", "main")
    return hashlib.sha256(raw.encode("ascii")).hexdigest()[:16]


def load_includes(spec):
    """Shared drawings (tools/device_drawings/parts/*.json) a spec builds on: their shapes and keyed controls."""
    shapes = []
    for item in spec.get("include", []):
        entry = item if isinstance(item, dict) else {"file": item}
        path = os.path.join(SPECS, "parts", entry["file"])
        with open(path, encoding="utf-8") as handle:
            part = json.load(handle)
        if "map" in entry:
            # This include's own map: its keyed controls get these parts, the other keys are drawn without a part.
            items = []
            for sh in part.get("shapes", []):
                if "key" in sh:
                    sh = dict(sh)
                    target = entry["map"].get(sh["key"])
                    sh.pop("key")
                    if isinstance(target, dict):
                        # A glyph over the model's own part (a small, dark hat made a clear target) or a nudge.
                        extra = dict(target)
                        target = extra.pop("part", None)
                        off = extra.pop("offset", None)
                        if off:
                            sh["at"] = [sh["at"][0] + off[0], sh["at"][1] + off[1]]
                        sh.update(extra)
                    if target:
                        sh["part"] = target
                    elif sh.get("glyph", "none") == "none" or entry.get("only"):
                        continue
                items.append(sh)
            part = dict(part, shapes=items)
        if any(k in entry for k in ("dx", "dy", "scale", "mirror")):
            shapes.append({"type": "group", "dx": entry.get("dx", 0), "dy": entry.get("dy", 0), "scale": entry.get("scale", 1),
                           "mirror": entry.get("mirror", False), "items": part.get("shapes", [])})
        else:
            shapes.extend(part.get("shapes", []))
    return shapes


def build_doc(spec):
    colors = dict(spec.get("colors", {}))
    doc = Doc(colors)
    doc.mapping = dict(spec.get("map", {}))
    for sh in load_includes(spec) + spec.get("shapes", []):
        if "key" in sh or "part" in sh or "glyph" in sh:
            doc.control(sh, IDENTITY)
        else:
            doc.shape(sh, IDENTITY)
    for c in spec.get("controls", []):
        if c.get("type") == "group" or ("type" in c and "glyph" not in c and "part" not in c):
            doc.shape(c, IDENTITY)
        else:
            doc.control(c, IDENTITY)
    doc.finalize()
    return doc


def drawing_json(spec, doc):
    W, H = float(spec["width"]), float(spec["height"])
    side = max(W, H)
    return {
        "format": FORMAT, "version": 1, "model": spec["model"], "view": spec.get("view", "main"), "key": opaque_key(spec),
        "class": spec.get("class", ""), "title": spec.get("title", "JoyPro drawing"), "width": W, "height": H,
        "outlineWidth": round(float(spec.get("outlineWidth", side * 0.0034)), 2),
        "detailWidth": round(float(spec.get("detailWidth", side * 0.0021)), 2),
        "paths": doc.paths, "fills": doc.fills,
    }


def layout_json(spec, doc, old=None):
    W, H = float(spec["width"]), float(spec["height"])
    markers = {k: [round(v[0] / W, 4), round(v[1] / H, 4)] for k, v in sorted(doc.markers.items())}
    # Merged user corrections (tools/device_corrections.py): positions by part id (0..1), removed parts.
    for k, v in (spec.get("positions") or {}).items():
        markers[k] = [round(float(v[0]), 4), round(float(v[1]), 4)]
    for k, why in (spec.get("unplaced") or {}).items():
        if why.startswith("removed by a user correction"):
            markers.pop(k, None)
    markers = dict(sorted(markers.items()))
    data = {
        "format": "joypro-device-layout", "version": 1, "model": spec["model"],
        "photo": {"kind": "lineart", "sha256": opaque_key(spec), "view": spec.get("view", "main")},
        "markers": markers,
    }
    if spec.get("unplaced"):
        data["unplaced"] = spec["unplaced"]
    for key in ("source", "mappingSource", "confidence", "note"):
        if spec.get(key):
            data[key] = spec[key]
    if old and old.get("callouts") and spec.get("keepPins", True):
        # Pinned tables stay when the drawing keeps its aspect (the sheet is arranged again around them).
        pic = old["callouts"].get("picture") or [0, 0, 0, 0]
        if pic[2] > 0 and pic[3] > 0 and abs(pic[2] / pic[3] - W / H) < 0.005:
            data["callouts"] = old["callouts"]
    return data


def spec_files(names, all_specs):
    if all_specs or not names:
        return sorted(glob.glob(os.path.join(SPECS, "*.json")))
    files = []
    for n in names:
        if os.path.isfile(n):
            files.append(n)
        else:
            found = sorted(glob.glob(os.path.join(SPECS, n + "*.json")))
            if not found:
                fail("no spec " + n)
            files.extend(found)
    return files


def engine_path(arg=None):
    for candidate in (arg, os.environ.get("JOYPRO_ENGINE"), os.path.join(REPO, "engine", "bin", "x64", "Debug", "engine.exe"),
                      os.path.join(REPO, "engine", "bin", "x64", "Release", "engine.exe")):
        if candidate and os.path.isfile(candidate):
            return candidate
    fail("engine.exe not found; build engine/engine.csproj or set JOYPRO_ENGINE")


def run_engine(args, *command):
    env = dict(os.environ)
    env["JOYPRO_DEVICE_KB"] = os.path.join(DB, "Devices")
    result = subprocess.run([engine_path(getattr(args, "engine", None))] + list(command), capture_output=True, text=True, encoding="utf-8", env=env)
    return result


def write_json(path, data):
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(data, handle, ensure_ascii=False, indent=1)
        handle.write("\n")


def set_path(spec):
    return os.path.join(LAYOUTS, spec["model"] + ".drawing-" + spec.get("view", "main") + ".json")


def drawing_path(spec):
    return os.path.join(DRAWINGS, spec["model"] + "." + spec.get("view", "main") + ".json")


def arrange_sheet(args, path):
    """The engine's callout sheet for a set (pinned tables kept), stored in the set; returns the engine's check."""
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    trial = path + ".trial.json"
    copy = json.loads(json.dumps(data))
    for t in (copy.get("callouts") or {}).get("tables", []):
        t.pop("title", None)
    write_json(trial, copy)
    try:
        result = run_engine(args, "layout", "callouts", "--layout", trial, "--arrange")
    finally:
        os.remove(trial)
    if result.returncode != 0:
        print("  engine failed: " + (result.stdout + result.stderr).strip()[:600])
        return None
    payload = json.loads(result.stdout[result.stdout.index("{"):])
    data["callouts"] = payload["Sheet"]
    write_json(path, data)
    return payload["Check"]


def preview(args, model, view, out):
    os.makedirs(out, exist_ok=True)
    files = []
    for style, light in (("cel", False), ("lines", True)):
        png = os.path.join(out, "%s.%s.%s.png" % (model, view, style))
        r = run_engine(args, "devices", "reference", "drawings", "--model", model, "--view", view, "--png", png, "--drawing-style", style,
                       *(["--light"] if light else []))
        if r.returncode == 0:
            files.append(png)
        else:
            print("  preview failed: " + (r.stdout + r.stderr).strip()[:300])
    sheet = os.path.join(SPECS_SET_DIR(model, view))
    png = os.path.join(out, "%s.%s.sheet.png" % (model, view))
    r = run_engine(args, "layout", "callouts", "--layout", sheet, "--sample", "--png", png, "--print-width", "0")
    if r.returncode == 0:
        files.append(png)
    else:
        print("  sheet preview failed: " + (r.stdout + r.stderr).strip()[:300])
    return files


def SPECS_SET_DIR(model, view):
    return os.path.join(LAYOUTS, model + ".drawing-" + view + ".json")


def cmd_build(args):
    for path in spec_files(args.specs, args.all):
        with open(path, encoding="utf-8") as handle:
            spec = json.load(handle)
        doc = build_doc(spec)
        os.makedirs(DRAWINGS, exist_ok=True)
        for old in spec.get("replaces", []):
            old_path = os.path.join(DRAWINGS, old)
            if os.path.isfile(old_path) and os.path.abspath(old_path) != os.path.abspath(drawing_path(spec)):
                os.remove(old_path)
                print("  removed " + old)
        write_json(drawing_path(spec), drawing_json(spec, doc))
        sp = set_path(spec)
        old = None
        if os.path.isfile(sp):
            with open(sp, encoding="utf-8") as handle:
                old = json.load(handle)
        write_json(sp, layout_json(spec, doc, old))
        for set_name, markers in doc.photo_markers.items():
            photo_set = os.path.join(LAYOUTS, set_name)
            with open(photo_set, encoding="utf-8-sig") as handle:
                data = json.load(handle)
            data.setdefault("markers", {}).update(markers)
            for key in ("unplaced", "candidates"):
                for part in markers:
                    (data.get(key) or {}).pop(part, None)
            data.pop("callouts", None)
            write_json(photo_set, data)
            print("  %s: %d positions from the drawing (arrange its sheet: device_positions.py sheet)" % (set_name, len(markers)))
        check = None if args.no_sheet else arrange_sheet(args, sp)
        line = "%s.%s: %d parts placed, %d paths, %d fills" % (spec["model"], spec.get("view", "main"), len(doc.markers), len(doc.paths), len(doc.fills))
        if doc.unmapped:
            line += " | drawn without a part: " + ", ".join(doc.unmapped)
        if check:
            line += " | tables %d, linked %d/%d, other %d, crossings %d, shared %d%s" % (
                check.get("Tables", 0), check.get("Linked", 0), check.get("Positioned", 0), check.get("Other", 0), check.get("Crossings", 0),
                check.get("Shared", 0), "" if check.get("Ok") else " NOT OK " + "; ".join(check.get("Problems", [])[:3]))
        print(line)
        if args.preview:
            for f in preview(args, spec["model"], spec.get("view", "main"), args.preview):
                print("  " + f)


# --------------------------------------------------------------------------------------------------- game 3D models

def nearest_palette(rgb):
    best, bd = None, 1e18
    for name in ("black", "body", "bezel", "mid", "grey", "cap", "light", "white", "red", "orange", "yellow", "green", "blue", "sky", "teal", "brown"):
        h = PALETTE[name]
        c = (int(h[1:3], 16), int(h[3:5], 16), int(h[5:7], 16))
        d = sum((a - b) ** 2 for a, b in zip(rgb, c))
        if d < bd:
            best, bd = name, d
    return best


DARKER = {"white": "light", "light": "cap", "cap": "grey", "grey": "mid", "mid": "bezel", "bezel": "body", "body": "black", "black": "black"}


def contour_d(contours, scale, ox, oy, eps):
    import cv2
    d = ""
    for c in contours:
        if len(c) < 3:
            continue
        import numpy as np
        raw = c.reshape(-1, 2).astype(np.float64)
        if len(raw) >= 9:
            # Pixel steps out: a circular moving average along the contour, then the simplification.
            k = 7
            pad = np.concatenate([raw[-k:], raw, raw[:k]])
            kernel = np.ones(2 * k + 1) / (2 * k + 1)
            raw = np.stack([np.convolve(pad[:, 0], kernel, "same")[k:-k], np.convolve(pad[:, 1], kernel, "same")[k:-k]], axis=1)
        a = cv2.approxPolyDP(raw.astype(np.float32).reshape(-1, 1, 2), eps, True).reshape(-1, 2)
        if len(a) < 3:
            continue
        pts = [((float(x) - ox) * scale, (float(y) - oy) * scale) for x, y in a]
        d += "M" + pt(pts[0]) + "".join("L" + pt(q) for q in pts[1:]) + "Z"
    return d


def cmd_mesh(args):
    """A game's grip render (engine devices reference mesh-render) as a shared drawing part: the silhouette, the outline
    of every separate part of the model, flat colour regions (the part's colour, a shadow tone where the light falls
    off), and each named part of the model as a keyed control at its spot. Specs include it and map the keys."""
    import cv2
    import numpy as np
    from PIL import Image
    folder = args.render
    with open(os.path.join(folder, "meta.json"), encoding="utf-8-sig") as handle:
        meta = json.load(handle)
    W, H = meta["Width"], meta["Height"]
    part = np.fromfile(os.path.join(folder, "part.bin"), dtype=np.int32).reshape(H, W)
    light = np.fromfile(os.path.join(folder, "light.bin"), dtype=np.float32).reshape(H, W)
    albedo = np.array(Image.open(os.path.join(folder, "albedo.png")).convert("RGB"))
    x0, y0, x1, y1 = [float(v) for v in (args.crop or "0,0,1,1").split(",")]
    cx0, cy0, cx1, cy1 = int(x0 * W), int(y0 * H), int(x1 * W), int(y1 * H)
    part = part[cy0:cy1, cx0:cx1].copy()
    light = light[cy0:cy1, cx0:cx1]
    albedo = albedo[cy0:cy1, cx0:cx1]
    mask = (part >= 0).astype(np.uint8)
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        fail("nothing of the grip in the crop")
    bx0, by0, bx1, by1 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
    target = float(args.size)
    scale = target / max(bx1 - bx0, by1 - by0)
    margin = 0.0
    shapes = []
    eps = max(1.0, 0.9 / max(scale, 1e-6))
    ids, counts = np.unique(part[part >= 0], return_counts=True)
    body = int(ids[np.argmax(counts)])
    order = [body] + [int(i) for i in ids if i != body]
    names = {m["Part"]: m["Node"] for m in meta.get("Meshes", [])}
    kernel = np.ones((3, 3), np.uint8)
    for pid in order:
        region = (part == pid).astype(np.uint8)
        if region.sum() < 40:
            continue
        region = cv2.morphologyEx(region, cv2.MORPH_OPEN, kernel)
        colour = nearest_palette(tuple(int(v) for v in np.median(albedo[part == pid], axis=0)))
        for tone, sel in ((colour, light >= args.shadow), (DARKER.get(colour, colour), light < args.shadow)):
            sub = (region & sel.astype(np.uint8))
            sub = cv2.morphologyEx(sub, cv2.MORPH_OPEN, kernel)
            if sub.sum() < 60:
                continue
            contours, hierarchy = cv2.findContours(sub, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_NONE)
            contours = [c for c in contours if cv2.contourArea(c) > 30]
            d = contour_d(contours, scale, bx0, by0, eps)
            if d:
                shapes.append({"type": "raw", "d": d, "fill": tone, "rule": "evenodd", "role": "none", "occludes": False})
        if pid != body:
            contours, _ = cv2.findContours(region, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
            contours = [c for c in contours if cv2.contourArea(c) > 30]
            d = contour_d(contours, scale, bx0, by0, eps)
            if d:
                shapes.append({"type": "raw", "d": d, "role": "detail", "note": names.get(pid, "")})
    contours, _ = cv2.findContours(cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    contours = [c for c in contours if cv2.contourArea(c) > 200]
    shapes.append({"type": "raw", "d": contour_d(contours, scale, bx0, by0, eps), "role": "outline", "closed": True, "fill": None})
    for p in meta.get("Parts", []):
        x, y = (p["X"] - cx0 - bx0) * scale, (p["Y"] - cy0 - by0) * scale
        if 0 <= x <= (bx1 - bx0) * scale and 0 <= y <= (by1 - by0) * scale:
            shapes.append({"key": p["Id"], "glyph": "none", "at": [round(x, 1), round(y, 1)], "seen": p["Seen"]})
    if args.components:
        # Models without named controls (one mesh for all buttons): every separate piece of a part other than the
        # body is a keyed control "c01", "c02" ... (top to bottom, then left to right); a labeled preview shows which.
        comps = []
        for pid in order[1:]:
            region = (part == pid).astype(np.uint8)
            n, labels, stats, cents = cv2.connectedComponentsWithStats(region, 8)
            for k in range(1, n):
                if stats[k, cv2.CC_STAT_AREA] < args.components:
                    continue
                comps.append((cents[k][1], cents[k][0], stats[k, cv2.CC_STAT_AREA]))
        comps.sort(key=lambda c: (round(c[0] / 12), c[1]))
        from PIL import ImageDraw
        prev = Image.fromarray(np.where(mask[..., None] > 0, (np.clip(light, 0, 1)[..., None] * 200 + 40).astype(np.uint8).repeat(3, 2), 0).astype(np.uint8))
        draw = ImageDraw.Draw(prev)
        for i, (cy, cx, area) in enumerate(comps):
            key = "c%02d" % (i + 1)
            shapes.append({"key": key, "glyph": "none", "at": [round((cx - bx0) * scale, 1), round((cy - by0) * scale, 1)], "area": int(area)})
            draw.ellipse([cx - 6, cy - 6, cx + 6, cy + 6], outline=(255, 60, 60), width=2)
            draw.text((cx + 8, cy - 6), key, fill=(255, 255, 0))
        prev.save(os.path.join(folder, "components.png"))
        print("components: %d -> %s" % (len(comps), os.path.join(folder, "components.png")))
    out = {
        "note": "%s %s of %s (%s), rendered from the game's own 3D model (engine devices reference mesh-render), view yaw %s pitch %s" % (
            meta["Game"], meta["Grip"], meta["Aircraft"], meta["Model"], meta["Yaw"], meta["Pitch"]),
        "source": "%s %s cockpit model" % (meta["Game"], meta["Aircraft"]),
        "width": round((bx1 - bx0) * scale, 1), "height": round((by1 - by0) * scale, 1), "shapes": shapes,
        # How to find the drawing's points on the game's model again (the 3D view): the render and the crop.
        "render": {"game": meta["Game"], "aircraft": meta["Aircraft"], "grip": meta["Grip"], "yaw": meta["Yaw"], "pitch": meta["Pitch"],
                   "size": W, "skip": meta.get("Skip", ""), "x0": int(cx0 + bx0), "y0": int(cy0 + by0), "scale": scale}}
    path = os.path.join(SPECS, "parts", args.name + ".json")
    write_json(path, out)
    print("%s: %d shapes, %d keyed parts, %s x %s" % (path, len(shapes), sum(1 for s in shapes if "key" in s), out["width"], out["height"]))


# --------------------------------------------------------------------------------------------------- template drawings

# The glyph of a part type (DeviceVocabulary.ElementType) for the template drawings.
TYPE_GLYPH = {"hat": "hat4", "ministick": "ministick", "button": "button", "release": "button", "switch": "toggle", "toggle": "toggle",
              "rocker": "rocker", "wheel": "wheel", "encoder": "encoder", "knob": "knob", "slider": "slider", "lever": "lever",
              "gimbal": "gimbal", "twist": "twist", "key": "key", "osb": "key", "selector": "knob", "paddle": "rocker", "rudder": "pedal",
              "brake": "lever", "trigger": "none", "detent": "none"}


def smooth_closed(line, window=9, step=None):
    import numpy as np
    pts = np.array(line, dtype=np.float64)
    if len(pts) > 2 and np.allclose(pts[0], pts[-1]):
        pts = pts[:-1]
    if len(pts) < 8:
        return pts.tolist()
    pts = np.array(resample(pts.tolist() + [pts[0].tolist()], step or 2.0)[:-1])
    k = window
    pad = np.concatenate([pts[-k:], pts, pts[:k]])
    kernel = np.ones(2 * k + 1) / (2 * k + 1)
    sm = np.stack([np.convolve(pad[:, 0], kernel, "same")[k:-k], np.convolve(pad[:, 1], kernel, "same")[k:-k]], axis=1)
    return sm.tolist()


def polygon_area(pts):
    a = 0.0
    for i in range(len(pts)):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 1) % len(pts)]
        a += x1 * y2 - x2 * y1
    return abs(a) / 2


def cmd_auto(args):
    """A template drawing for a model with JoyPro's curated positions on a photo: the silhouette of its traced drawing
    smoothed into one clean outline (no traced inner lines, no screw holes), and every placed part drawn as the
    glyph of its type at its curated spot. The drawing keeps the traced drawing's frame and key, so its curated set and
    shipped sheet apply as they are. A model with a spec of its own (tools/device_drawings) is left alone."""
    hand = {json.load(open(f, encoding="utf-8")).get("model") for f in glob.glob(os.path.join(SPECS, "*.json"))}
    sets = {}
    for f in glob.glob(os.path.join(LAYOUTS, "*.json")):
        with open(f, encoding="utf-8-sig") as handle:
            data = json.load(handle)
        sha = (data.get("photo") or {}).get("sha256", "")
        if sha and (data.get("photo") or {}).get("kind") == "vendor" and data.get("markers"):
            key = hashlib.sha256(("joypro-drawing:" + sha.lower()).encode("ascii")).hexdigest()[:16]
            sets[(data["model"], key)] = data
    done = 0
    for path in sorted(glob.glob(os.path.join(DRAWINGS, "*.json"))):
        with open(path, encoding="utf-8") as handle:
            art = json.load(handle)
        model = art.get("model", "")
        if not model or (model in hand and not args.part) or (args.models and not any(q == model for q in args.models)):
            continue
        if art.get("generator") == "template" and not args.force and not args.part:
            continue
        if args.view and art.get("view") != args.view:
            continue
        if getattr(args, "key", None) and art.get("key") != args.key:
            continue
        data = sets.get((model, art.get("key", "")))
        if data is None:
            continue
        r = run_engine(args, "devices", "reference", "show", model)
        if r.returncode != 0:
            print(model + ": engine failed")
            continue
        sch = json.loads(r.stdout[r.stdout.index("{"):])
        types = {m["Id"]: m["Type"] for m in sch["Markers"]}
        W, H = float(art["width"]), float(art["height"])
        side = max(W, H)
        # The silhouette: the traced outline paths' closed loops, the big ones, smoothed.
        loops = []
        for p in art["paths"]:
            if p["role"] != "outline":
                continue
            for line, closed in flatten_d(p["d"], 10):
                if len(line) > 6 and polygon_area(line) > side * side * 0.002:
                    loops.append(line)
        if not loops:
            print(model + ": no silhouette")
            continue
        biggest = max(polygon_area(l) for l in loops)
        loops = [l for l in loops if polygon_area(l) > biggest * 0.04]
        shapes = []
        body_colour = "body"
        if art.get("fills"):
            from collections import Counter
            body_colour = Counter(f["color"] for f in art["fills"]).most_common(1)[0][0]
            if body_colour.upper() in ("#F2F2F2", "#D9D9D9"):
                body_colour = "body"
        for loop in sorted(loops, key=polygon_area, reverse=True):
            sm = smooth_closed(loop, window=int(max(4, side / 220)), step=side / 400)
            sm = rdp(sm + [sm[0]], side / 900)[:-1]
            shapes.append({"type": "path", "closed": True, "pts": [[round(x, 1), round(y, 1)] for x, y in sm], "fill": body_colour, "role": "outline"})
        # The bigger inner contours of the traced drawing (a lever, a panel, a second body part), smoothed; the small
        # ones (screw heads, texture, holes) are left out.
        spots_px = [(v[0] * W, v[1] * H) for v in data["markers"].values()]
        for p in art["paths"]:
            if p["role"] == "outline":
                continue
            for line, closed in flatten_d(p["d"], 10):
                if not closed or len(line) < 8:
                    continue
                area = polygon_area(line)
                if area < side * side * 0.004 or area > biggest * 0.6:
                    continue
                # A small contour around a spot is that control: its glyph draws it.
                xs, ys = [q[0] for q in line], [q[1] for q in line]
                if any(min(xs) <= x <= max(xs) and min(ys) <= y <= max(ys) for x, y in spots_px) and area < side * side * 0.02:
                    continue
                sm = smooth_closed(line, window=int(max(3, side / 300)), step=side / 500)
                sm = rdp(sm + [sm[0]], side / 900)[:-1]
                shapes.append({"type": "path", "closed": True, "pts": [[round(x, 1), round(y, 1)] for x, y in sm], "role": "detail"})
        # Glyph sizes from the spacing of the spots.
        spots = {k: (v[0] * W, v[1] * H) for k, v in data["markers"].items()}
        controls = []
        for k, (x, y) in spots.items():
            others = [math.hypot(x - a, y - b) for kk, (a, b) in spots.items() if kk != k]
            nn = min(others) if others else side * 0.1
            t = types.get(k, "button")
            g = TYPE_GLYPH.get(t, "button")
            size = max(side * 0.012, min(side * 0.04, nn * 0.38))
            c = {"part": k, "glyph": g, "at": [round(x, 1), round(y, 1)], "size": round(size, 1)}
            if g in ("lever", "slider"):
                c.update({"w": size * 0.5, "h": size * 2.6})
            if g in ("gimbal", "twist"):
                c["size"] = round(side * 0.05, 1)
            if g == "key":
                c.update({"w": size * 1.7, "h": size * 1.7})
            if g == "wheel":
                c.update({"w": size * 0.8, "h": size * 2.0})
            if g == "rocker":
                c.update({"w": size * 0.9, "h": size * 1.9})
            if g == "pedal":
                c["size"] = round(side * 0.12, 1)
            controls.append(c)
        if args.part:
            # A shared part for a composite spec (the base of a replica): the shapes and the glyphs, keyed by part id.
            keyed = [dict(c, key=c.pop("part")) for c in controls]
            write_json(os.path.join(SPECS, "parts", args.part + ".json"),
                       {"note": "template drawing of %s (%s view): smoothed silhouette of the traced drawing, glyphs at the curated positions" % (model, art.get("view", "main")),
                        "width": W, "height": H, "shapes": shapes + keyed})
            print("part " + args.part)
            done += 1
            continue
        spec = {"model": model, "view": art.get("view", "main"), "width": W, "height": H, "shapes": shapes, "controls": controls}
        doc = build_doc(spec)
        out = dict(art)
        out["paths"], out["fills"] = doc.paths, doc.fills
        out["generator"] = "template"
        out["outlineWidth"] = round(side * 0.0034, 2)
        out["detailWidth"] = round(side * 0.0021, 2)
        if args.dry:
            prev = os.path.join(args.preview or ".", "%s.%s.template.png" % (model, art.get("view", "main")))
            render_png(out, prev, width=900, markers=dict(doc.markers))
            print(prev)
        else:
            write_json(path, out)
            if args.preview:
                render_png(out, os.path.join(args.preview, "%s.%s.template.png" % (model, art.get("view", "main"))), width=900, markers=dict(doc.markers))
        done += 1
    print("template drawings: %d" % done)


# --------------------------------------------------------------------------------------------------- composed views

def cmd_compose(args):
    """One drawing of a model from several of its curated photo views side by side (each as its template drawing: the
    smoothed silhouette, JoyPro's symbols at the curated spots), plus a mirrored copy of a view's outline for the far side
    ("<view>~"), and the controls no photo shows placed by hand from the maker's button map. The recipe is the spec's
    "compose" block: {"views": ["main", "side", "main~"], "place": [{"part": id, "view": "main~", "uv": [u, v],
    "glyph": optional, "size": optional}], "notPhysical": {part: why}, "unplaced": {part: why}, "source", "mappingSource"}.
    Writes tools/device_drawings/parts/<model>-<view>.json and the spec <model>.main.json, then builds it like build."""
    for model in args.models:
        spec_path = os.path.join(SPECS, model + ".main.json")
        recipe = {}
        if os.path.isfile(spec_path):
            with open(spec_path, encoding="utf-8") as handle:
                old = json.load(handle)
            if "compose" not in old:
                fail(model + ": has a hand spec of its own (no compose block)")
            recipe = old["compose"]
        if args.views:
            recipe["views"] = args.views.split(",")
        views = recipe.get("views") or []
        if not views:
            fail(model + ": give --views")
        r = run_engine(args, "devices", "reference", "show", model)
        if r.returncode != 0:
            fail(model + ": engine failed")
        sch = json.loads(r.stdout[r.stdout.index("{"):])
        types = {m["Id"]: m["Type"] for m in sch["Markers"]}
        H0, gap = 1000.0, 70.0
        includes, frames, placed, sources, mappings = [], {}, set(), [], []
        x = 30.0
        for token in views:
            view, mirror = token.rstrip("~"), token.endswith("~")
            set_file = os.path.join(LAYOUTS, "%s.%s.json" % (model, view))
            with open(set_file, encoding="utf-8-sig") as handle:
                data = json.load(handle)
            part_name = "%s-%s" % (model, view)
            if not os.path.isfile(os.path.join(SPECS, "parts", part_name + ".json")) or args.refresh:
                sha = ((data.get("photo") or {}).get("sha256") or "").lower()
                key = hashlib.sha256(("joypro-drawing:" + sha).encode("ascii")).hexdigest()[:16]
                cmd_auto(argparse.Namespace(models=[model], part=part_name, view=None, key=key, force=True, dry=False, preview=None, engine=args.engine))
                if not os.path.isfile(os.path.join(SPECS, "parts", part_name + ".json")):
                    fail("%s: no drawing of the %s photo to make a template from" % (model, view))
            with open(os.path.join(SPECS, "parts", part_name + ".json"), encoding="utf-8") as handle:
                part = json.load(handle)
            W, H = float(part["width"]), float(part["height"])
            scale = H0 / H
            entry = {"file": part_name + ".json", "dx": round(x + (W * scale if mirror else 0), 1), "dy": 30.0, "scale": round(scale, 5), "mirror": mirror}
            if mirror:
                entry.update({"map": {}, "only": True})
            else:
                entry["map"] = {k: k for k in sorted(data.get("markers", {})) if k not in placed}
                placed.update(entry["map"])
                for key, value in (("source", sources), ("mappingSource", mappings)):
                    if data.get(key) and data[key] not in value:
                        value.append(data[key])
            sizes = [float(sh.get("size", 0)) for sh in part["shapes"] if sh.get("key") and sh.get("size")]
            frames[token] = (entry["dx"], 30.0, scale, mirror, W, H, sorted(sizes)[len(sizes) // 2] if sizes else H * 0.03)
            includes.append(entry)
            x += W * scale + gap
        controls = []
        for p in recipe.get("place", []):
            dx, dy, scale, mirror, W, H, size = frames[p["view"]]
            u, v = p["uv"]
            g = p.get("glyph") or TYPE_GLYPH.get(types.get(p["part"], "button"), "button")
            if g == "none":
                g = "button"
            c = {"part": p["part"], "glyph": g, "at": [round(u * W, 1), round(v * H, 1)], "size": round(float(p.get("size", size)), 1)}
            for k in ("angle", "w", "h", "press"):
                if k in p:
                    c[k] = p[k]
            if g in ("lever", "slider") and "w" not in c:
                c.update({"w": round(c["size"] * 0.5, 1), "h": round(c["size"] * 2.6, 1)})
            if g in ("wheel", "rocker") and "w" not in c:
                c.update({"w": round(c["size"] * 0.85, 1), "h": round(c["size"] * 1.95, 1)})
            if p["part"] in placed:
                fail("%s: %s is already on a view" % (model, p["part"]))
            placed.add(p["part"])
            controls.append({"type": "group", "dx": dx, "dy": dy, "scale": scale, "mirror": mirror, "items": [c]})
        unplaced = {k: "not physical: " + v for k, v in (recipe.get("notPhysical") or {}).items()}
        unplaced.update(recipe.get("unplaced") or {})
        missing = sorted(set(types) - placed - set(unplaced))
        spec = {
            "model": model, "view": "main", "class": sch.get("Kind", "") or "", "width": round(x - gap + 30, 1), "height": H0 + 60,
            "source": recipe.get("source") or ("JoyPro template drawing of the maker's photos (%s): smoothed silhouettes, JoyPro's control symbols at the curated spots" % ", ".join(views)),
            "mappingSource": recipe.get("mappingSource") or " / ".join(mappings),
            "confidence": "medium", "compose": recipe, "include": includes, "shapes": [], "controls": controls,
        }
        if unplaced:
            spec["unplaced"] = unplaced
        write_json(spec_path, spec)
        cmd_build(argparse.Namespace(specs=[spec_path], all=False, preview=None, no_sheet=args.no_sheet, engine=args.engine))
        print("%s: %d of %d parts on the drawing%s" % (model, len(placed), len(types), (", missing: " + ", ".join(missing)) if missing else ""))
        if args.grid:
            grid_preview(model, spec, frames, os.path.join(args.grid, model + ".png"))


def grid_preview(model, spec, frames, out):
    """The composed drawing with a 0..1 grid per view (tenths, labelled) and every marker's short name: for placing
    the controls of the compose block by eye."""
    from PIL import Image, ImageDraw, ImageFont
    with open(drawing_path(spec), encoding="utf-8") as handle:
        dj = json.load(handle)
    with open(set_path(spec), encoding="utf-8") as handle:
        markers = json.load(handle)["markers"]
    os.makedirs(os.path.dirname(out), exist_ok=True)
    width = int(os.environ.get("JOYPRO_GRID_WIDTH", "1500"))
    render_png(dj, out, width=width)
    im = Image.open(out).convert("RGB")
    dr = ImageDraw.Draw(im)
    k = width / float(dj["width"])
    try:
        font = ImageFont.truetype("arial.ttf", 12)
    except OSError:
        font = ImageFont.load_default()
    for token, (dx, dy, scale, mirror, W, H, _) in frames.items():
        def at(u, v):
            px = u * W * scale
            return ((dx - px if mirror else dx + px) * k, (dy + v * H * scale) * k)
        for i in range(11):
            t = i / 10.0
            dr.line([at(t, 0), at(t, 1)], fill=(70, 110, 160) if i % 5 else (110, 160, 220), width=1)
            dr.line([at(0, t), at(1, t)], fill=(70, 110, 160) if i % 5 else (110, 160, 220), width=1)
            dr.text(at(t, 0), "%.1f" % t, fill=(140, 190, 255), font=font)
            dr.text(at(0, t), "%.1f" % t, fill=(140, 190, 255), font=font)
        dr.text((at(0.02, 0.02)[0], at(0, 0.02)[1] + 12), token, fill=(255, 220, 120), font=font)
    for name, (u, v) in markers.items():
        x, y = u * dj["width"] * k, v * dj["height"] * k
        dr.ellipse([x - 4, y - 4, x + 4, y + 4], fill=(61, 139, 255))
        short = name.split(".", 1)[-1].replace("button.", "b.").replace("axis.", "")
        dr.text((x + 6, y - 7), short, fill=(255, 255, 255), font=font)
    im.save(out)
    print("  grid preview " + out)


# --------------------------------------------------------------------------------------------------- 3D view

# The 3D views are not shipped: they are made into a work folder (--out, else JOYPRO_DEVICE_3D_DIR) and published as packs
# by tools/build_device_3d_packs.ps1 (release "device-3d-v1").
def turntables_dir(args):
    out = getattr(args, "out", None) or os.environ.get("JOYPRO_DEVICE_3D_DIR")
    if not out:
        sys.exit("turntable: give --out <folder> or set JOYPRO_DEVICE_3D_DIR (the 3D views are not part of the repository)")
    if os.path.abspath(out).lower().startswith(os.path.abspath(REPO).lower()):
        sys.exit("turntable: the output folder must be outside the repository: " + out)
    return out


def cmd_turntable(args):
    """The rotatable 3D view of a replica device: the game grip's turntable (engine devices reference mesh-render
    --turntable), with the device's parts as anchors: every mapped key of the spec's game-model includes, placed in 3D
    through the include's view. Writes <out>/<model>/ (frames + turntable.json with part ids)."""
    import tempfile
    for path in spec_files(args.specs, False):
        with open(path, encoding="utf-8") as handle:
            spec = json.load(handle)
        model = spec["model"]
        if spec.get("mesh"):
            # A 3D model file instead of a game's cockpit model (tools/device_mesh_turntable.py).
            sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
            import device_mesh_turntable
            base = turntables_dir(args)
            device_mesh_turntable.run(path, os.path.join(base, ".models"), os.path.join(base, model), steps=args.steps,
                                      pitches=tuple(float(p) for p in args.pitches.split(",")), size=args.size, views_dir=args.views)
            continue
        includes = []
        for item in spec.get("include", []):
            entry = item if isinstance(item, dict) else {"file": item}
            with open(os.path.join(SPECS, "parts", entry["file"]), encoding="utf-8") as handle:
                part = json.load(handle)
            if part.get("render"):
                includes.append((entry, part))
        if not includes:
            print(model + ": no game model in its drawing (no 3D view)")
            continue
        render = includes[0][1]["render"]
        out = os.path.join(turntables_dir(args), model)
        os.makedirs(out, exist_ok=True)
        work = tempfile.mkdtemp(prefix="joypro-turntable-")
        anchor_files, mapping = [], {}
        for k, (entry, part) in enumerate(includes):
            r = part["render"]
            points = {}
            for sh in part.get("shapes", []):
                key = sh.get("key")
                if not key:
                    continue
                target = (entry.get("map") or spec.get("map") or {}).get(key)
                if isinstance(target, dict):
                    target = target.get("part")
                if not target:
                    continue
                points[key + "#" + str(k)] = [r["x0"] + sh["at"][0] / r["scale"], r["y0"] + sh["at"][1] / r["scale"]]
                for t in (target if isinstance(target, list) else [target]):
                    mapping.setdefault(t, key + "#" + str(k))
            # The spec's own controls drawn on this include's view ("anchor3d": the include's index): a control the game
            # model has no piece for (a button the replica adds) is placed in 3D through the same view.
            for c in spec.get("controls", []):
                if c.get("anchor3d") != k or not c.get("part"):
                    continue
                sc = float(entry.get("scale", 1))
                lx, ly = (c["at"][0] - float(entry.get("dx", 0))) / sc, (c["at"][1] - float(entry.get("dy", 0))) / sc
                key = "ctl-" + c["part"] + "#" + str(k)
                points[key] = [r["x0"] + lx / r["scale"], r["y0"] + ly / r["scale"]]
                mapping.setdefault(c["part"], key)
            f = os.path.join(work, "anchors%d.json" % k)
            write_json(f, {"view": [r["yaw"], r["pitch"]], "size": r["size"], "points": points})
            anchor_files.append(f)
        command = ["devices", "reference", "mesh-render", "--game", render["game"], "--aircraft", render["aircraft"], "--grip", render["grip"],
                   "--folder", out, "--size", str(args.size), "--turntable", str(args.steps), "--pitches", args.pitches,
                   "--keep-top", str(spec.get("turntableKeepTop", 1)), "--anchors", ";".join(anchor_files)]
        if render.get("skip"):
            command += ["--skip", render["skip"]]
        r = run_engine(args, *command)
        if r.returncode != 0:
            print(model + ": engine failed: " + (r.stdout + r.stderr).strip()[:400])
            continue
        with open(os.path.join(out, "turntable.json"), encoding="utf-8-sig") as handle:
            tt = json.load(handle)
        # Keys -> the device's part ids.
        for frame in tt["frames"]:
            frame["points"] = {p: frame["points"][a] for p, a in mapping.items() if a in frame["points"]}
        tt["anchors"] = sorted(mapping)
        tt["model"] = model
        tt["source"] = "Rendered from %s's %s cockpit model (%s grip) by JoyPro" % (render["game"] if render["game"] != "DCS" else "DCS World", render["aircraft"], render["grip"])
        write_json(os.path.join(out, "turntable.json"), tt)
        missing = sorted(set(v if isinstance(v, str) else v.get("part") for e, _ in includes for v in (e.get("map") or {}).values() if v) - set(mapping))
        print("%s: %d frames, %d parts in 3D%s" % (model, len(tt["frames"]), len(mapping), (", not in the model: " + ", ".join(missing)) if missing else ""))


def cmd_preview(args):
    for f in preview(args, args.model, args.view, args.out):
        print(f)


def cmd_glyphs(args):
    glyphs = ["button", "square", "key", "hat4", "hat8", "ministick", "toggle", "rocker", "wheel", "encoder", "knob", "slider", "lever",
              "gimbal", "twist", "pedal"]
    spec = {"model": "glyphs", "view": "sheet", "width": 120 * len(glyphs), "height": 200, "shapes": [], "controls": []}
    for i, g in enumerate(glyphs):
        spec["controls"].append({"glyph": g, "at": [60 + 120 * i, 100], "size": 34})
    doc = build_doc(spec)
    out = args.out or os.path.join(os.environ.get("TEMP", "."), "device-drawings")
    os.makedirs(out, exist_ok=True)
    dj = drawing_json(spec, doc)
    dj["paths"].insert(0, {"role": "outline", "d": rrect_d(spec["width"] / 2, 100, spec["width"] - 10, 190, 20)})
    with open(os.path.join(out, "glyphs.svg"), "w", encoding="utf-8") as handle:
        handle.write(svg_of(dj))
    print(render_png(dj, os.path.join(out, "glyphs.png"), width=1900))


def flatten_d(d, steps=10):
    """Absolute path data as polylines (for the quick preview)."""
    tokens = d.replace(",", " ")
    for op in "MLCQZ":
        tokens = tokens.replace(op, " " + op + " ")
    tokens = tokens.split()
    lines, cur, pos, start, op, j = [], None, (0.0, 0.0), None, None, 0
    while j < len(tokens):
        t = tokens[j]
        if t in "MLCQZ":
            op = t
            j += 1
            if t == "Z" and cur:
                cur.append(start)
                lines.append((cur, True))
                cur = None
            continue
        n = {"M": 1, "L": 1, "Q": 2, "C": 3}[op]
        p = [(float(tokens[j + 2 * k]), float(tokens[j + 2 * k + 1])) for k in range(n)]
        j += 2 * n
        if op == "M":
            if cur:
                lines.append((cur, False))
            cur, start, pos, op = [p[0]], p[0], p[0], "L"
        elif op == "L":
            cur.append(p[0])
            pos = p[0]
        elif op == "Q":
            for k in range(1, steps + 1):
                u = k / steps
                v = 1 - u
                cur.append((v * v * pos[0] + 2 * v * u * p[0][0] + u * u * p[1][0], v * v * pos[1] + 2 * v * u * p[0][1] + u * u * p[1][1]))
            pos = p[1]
        elif op == "C":
            for k in range(1, steps + 1):
                u = k / steps
                v = 1 - u
                cur.append((v ** 3 * pos[0] + 3 * v * v * u * p[0][0] + 3 * v * u * u * p[1][0] + u ** 3 * p[2][0],
                            v ** 3 * pos[1] + 3 * v * v * u * p[0][1] + 3 * v * u * u * p[1][1] + u ** 3 * p[2][1]))
            pos = p[2]
    if cur:
        lines.append((cur, False))
    return lines


def render_png(dj, out, width=1200, dark=True, markers=None):
    """A quick preview of a drawing with PIL (the engine's renderer is the reference): cel fills, strokes, markers."""
    from PIL import Image, ImageDraw
    k = width / float(dj["width"])
    ss = 2
    W, H = int(dj["width"] * k * ss), int(dj["height"] * k * ss)
    paper, ink = ("#1E2430", "#F2F2F2") if dark else ("#FFFFFF", "#141414")
    im = Image.new("RGB", (W, H), paper)
    dr = ImageDraw.Draw(im)
    for f in dj["fills"]:
        for line, closed in flatten_d(f["d"]):
            if len(line) > 2:
                dr.polygon([(x * k * ss, y * k * ss) for x, y in line], fill=f["color"])
    for p in dj["paths"]:
        sw = dj["outlineWidth"] * 1.6 if p["role"] == "outline" else dj["detailWidth"] * (0.65 if p["role"] == "minor" else 1.25)
        for line, closed in flatten_d(p["d"]):
            dr.line([(x * k * ss, y * k * ss) for x, y in line], fill=ink, width=max(1, int(round(sw * k * ss))), joint="curve")
    for name, (x, y) in (markers or {}).items():
        r = 5 * ss
        dr.ellipse([x * k * ss - r, y * k * ss - r, x * k * ss + r, y * k * ss + r], fill="#3D8BFF", outline="#FFFFFF")
    im = im.resize((W // ss, H // ss), Image.LANCZOS)
    im.save(out)
    return out


def svg_of(dj):
    w, h = dj["width"], dj["height"]
    parts = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %s %s" width="%s" height="%s"><rect width="100%%" height="100%%" fill="#1e2430"/>' % (w, h, w, h)]
    for f in dj["fills"]:
        parts.append('<path fill="%s" d="%s"/>' % (f["color"], f["d"]))
    for p in dj["paths"]:
        sw = dj["outlineWidth"] if p["role"] == "outline" else dj["detailWidth"] * (0.65 if p["role"] == "minor" else 1)
        parts.append('<path fill="none" stroke="#f2f2f2" stroke-width="%s" stroke-linejoin="round" stroke-linecap="round" d="%s"/>' % (sw, p["d"]))
    parts.append("</svg>")
    return "\n".join(parts)


def cmd_list(args):
    for path in spec_files([], True):
        with open(path, encoding="utf-8") as handle:
            spec = json.load(handle)
        doc = build_doc(spec)
        print("%-58s %3d parts placed, %d unplaced" % (os.path.basename(path), len(doc.markers), len(spec.get("unplaced", {}))))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--engine")
    sub = ap.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("build")
    b.add_argument("specs", nargs="*")
    b.add_argument("--all", action="store_true")
    b.add_argument("--preview")
    b.add_argument("--no-sheet", action="store_true")
    b.set_defaults(func=cmd_build)
    p = sub.add_parser("preview")
    p.add_argument("model")
    p.add_argument("--view", default="main")
    p.add_argument("--out", required=True)
    p.set_defaults(func=cmd_preview)
    m = sub.add_parser("mesh")
    m.add_argument("render")
    m.add_argument("name")
    m.add_argument("--crop")
    m.add_argument("--size", type=float, default=800)
    m.add_argument("--shadow", type=float, default=0.42)
    m.add_argument("--components", type=int, default=0)
    m.set_defaults(func=cmd_mesh)
    a = sub.add_parser("auto")
    a.add_argument("models", nargs="*")
    a.add_argument("--dry", action="store_true")
    a.add_argument("--force", action="store_true")
    a.add_argument("--preview")
    a.add_argument("--part")
    a.add_argument("--view")
    a.set_defaults(func=cmd_auto)
    co = sub.add_parser("compose")
    co.add_argument("models", nargs="+")
    co.add_argument("--views")
    co.add_argument("--grid")
    co.add_argument("--refresh", action="store_true")
    co.add_argument("--no-sheet", action="store_true")
    co.set_defaults(func=cmd_compose)
    t = sub.add_parser("turntable")
    t.add_argument("specs", nargs="+")
    t.add_argument("--steps", type=int, default=24)
    t.add_argument("--pitches", default="5,25,45")
    t.add_argument("--size", type=int, default=420)
    t.add_argument("--out")
    t.add_argument("--views", help="3D model recipes: only draw the anchor views with a pixel grid into this folder")
    t.set_defaults(func=cmd_turntable)
    g = sub.add_parser("glyphs")
    g.add_argument("--out")
    g.set_defaults(func=cmd_glyphs)
    li = sub.add_parser("list")
    li.set_defaults(func=cmd_list)
    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
