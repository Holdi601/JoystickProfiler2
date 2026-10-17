"""Second round of "2" / ">_" concepts and matching titles for the JoyPro mark (docs/branding/variants-round2.png).

Loaded by generate-branding.py. Every concept gives
    Brand.AppIcon.<Name>, Brand.AppIcon.<Name>.Small      the mark with its "2"   (100 x 100)
    Brand.EngineIcon.<Name>, Brand.EngineIcon.<Name>.Small the mark with its ">_"
    Brand.Wordmark.<Name>                                  "JOYPRO 2" with letters and "2" in the concept's style
Concepts: LineArt, Formation, Stencil, Hud, Metal, Integrated. All shapes are JoyPro's own path data (no fonts).
"""
import math

WHITE = '#F8FAFC'
DARK = '#0B1220'
BLUE = '#38BDF8'
SKEW = 12


def fmt(v):
    s = ('%.2f' % v).rstrip('0').rstrip('.')
    return '0' if s in ('-0', '') else s


def geo(polys):
    return ' '.join('M' + fmt(p[0][0]) + ',' + fmt(p[0][1]) + ' ' + ' '.join('L' + fmt(x) + ',' + fmt(y) for x, y in p[1:]) + ' Z' for p in polys)


def tf(polys, t):
    return [[t(p) for p in poly] for poly in polys]


def place(dx, dy, s, skew=0.0, height=0.0):
    def t(p):
        x, y = p[0] * s, p[1] * s
        if skew:
            x += (height - y) * math.tan(math.radians(skew))
        return (x + dx, y + dy)
    return t


def layer(polys, fill=None, pen=None, thick=0.0, join='Miter', opacity=None):
    attrs = ' Geometry="' + geo(polys) + '"'
    if fill:
        attrs += ' Brush="' + fill + '"'
    if not pen:
        return '<GeometryDrawing' + attrs + ' />'
    op = '' if opacity is None else '#%02X' % int(opacity * 255) + pen[1:]
    return ('<GeometryDrawing' + attrs + '><GeometryDrawing.Pen><Pen Brush="' + (op or pen) + '" Thickness="' + fmt(thick) +
            '" LineJoin="' + join + '" /></GeometryDrawing.Pen></GeometryDrawing>')


def lines(segments, pen, thick, opacity=None):
    polys = [[a, b] for a, b in segments]
    d = ' '.join('M' + fmt(a[0]) + ',' + fmt(a[1]) + ' L' + fmt(b[0]) + ',' + fmt(b[1]) for a, b in segments)
    brush = pen if opacity is None else '#%02X' % int(opacity * 255) + pen[1:]
    return ('<GeometryDrawing Geometry="' + d + '"><GeometryDrawing.Pen><Pen Brush="' + brush + '" Thickness="' + fmt(thick) +
            '" LineCap="Flat" /></GeometryDrawing.Pen></GeometryDrawing>')


def area(poly):
    return sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly))) / 2


def inset(poly, d, hole=False):
    """The polygon moved d inwards (outwards for a hole of a letter)."""
    n = len(poly)
    sign = 1 if area(poly) > 0 else -1
    if hole:
        sign = -sign
    offs = []
    for i in range(n):
        a, b = poly[i], poly[(i + 1) % n]
        dx, dy = b[0] - a[0], b[1] - a[1]
        ln = math.hypot(dx, dy) or 1
        nx, ny = -dy / ln * sign, dx / ln * sign
        offs.append(((a[0] + nx * d, a[1] + ny * d), (b[0] + nx * d, b[1] + ny * d)))
    out = []
    for i in range(n):
        (p1, p2), (p3, p4) = offs[i - 1], offs[i]
        den = (p1[0] - p2[0]) * (p3[1] - p4[1]) - (p1[1] - p2[1]) * (p3[0] - p4[0])
        if abs(den) < 1e-9:
            out.append(p3)
            continue
        t = ((p1[0] - p3[0]) * (p3[1] - p4[1]) - (p1[1] - p3[1]) * (p3[0] - p4[0])) / den
        out.append((p1[0] + t * (p2[0] - p1[0]), p1[1] + t * (p2[1] - p1[1])))
    return out


# ---------------------------------------------------------------- glyph shapes (numeral box 10 x 14)

TWO = [[(0, 4.6), (3.2, 0), (7.8, 0), (10, 2.6), (10, 6), (4.6, 11), (10, 11), (10, 14), (0, 14), (0, 11.2), (6.6, 5.2), (6.6, 3.6), (5.8, 3), (4.2, 3), (3.1, 4.6)]]
TWO_TIP = [[(7.2, 14), (10, 11.6), (10, 14)]]
CHEVRON = [[(0, 0), (3, 0), (8, 5), (3, 10), (0, 10), (5, 5)]]
CURSOR = [[(9, 8), (14, 8), (14, 10), (9, 10)]]

# Formation: swept wing leading edge (top), sweep line / spine (diagonal), trailing edge with canard notch (foot).
# Three separate pieces with small gaps between them, like aircraft flying in formation.
FORM_TOP = [[(0, 4.4), (3.6, 0), (9.6, 0.9), (9.0, 3.1), (4.6, 2.6), (2.7, 4.9)]]
FORM_SPINE = [[(9.5, 3.9), (10, 6.2), (3.2, 12.0), (0.9, 11.2), (7.4, 5.4), (8.6, 4.0)]]
FORM_FOOT = [[(0, 14), (1.0, 12.6), (10, 12.6), (10, 14)]]
FORM_CANOPY = [[(7.2, 6.1), (7.7, 6.7), (5.3, 8.9), (4.8, 8.3)]]
FORM_CHEV = [[(0, 0), (3.4, 0.4), (7.6, 4.6), (4.6, 4.6)], [(4.6, 5.4), (7.6, 5.4), (3.4, 9.6), (0, 10)]]

# Stencil: the numeral with two bridges (gaps), as cut stencil letters have.
STENCIL_TWO = [
    [(0, 4.6), (3.2, 0), (5.2, 0), (5.2, 3), (4.2, 3), (3.1, 4.6)],
    [(6.2, 0), (7.8, 0), (10, 2.6), (10, 6), (7.4, 8.5), (6.0, 7.2), (6.6, 6.6), (6.6, 3.6), (6.2, 3.3)],
    [(6.6, 9.4), (4.6, 11), (10, 11), (10, 14), (0, 14), (0, 11.2), (5.2, 8.1)],
]
STENCIL_CHEV = [[(0, 0), (3, 0), (5.4, 2.4), (4.0, 3.8), (0, 0)], [(4.8, 4.6), (6.2, 3.2), (8, 5), (3, 10), (0, 10), (5, 5)]]

# HUD: a seven segment readout (top, upper right, middle, lower left, bottom) and a chevron underline.
def _skew(polys, deg, height):
    k = math.tan(math.radians(deg))
    return [[(x + (height - y) * k * 0.7, y) for x, y in poly] for poly in polys]


HUD_SEGS = _skew([
    [(1.6, 0), (8.4, 0), (7.4, 2), (2.6, 2)],
    [(10, 1.4), (10, 6.0), (9.0, 7.0), (8.0, 6.0), (8.0, 2.4)],
    [(2.4, 6.0), (7.6, 6.0), (8.6, 7.0), (7.6, 8.0), (2.4, 8.0), (1.4, 7.0)],
    [(0, 8.0), (1.0, 7.0), (2.0, 8.0), (2.0, 11.6), (0, 12.6)],
    [(2.6, 12), (7.4, 12), (8.4, 14), (1.6, 14)],
], 10, 14)
HUD_UNDER = [[(1.0, 15.2), (5.0, 16.8), (9.0, 15.2), (9.0, 16.2), (5.0, 17.8), (1.0, 16.2)]]
HUD_CHEV = [[(0, 0), (1.5, 0), (6.5, 5), (5.75, 5.75), (0.75, 0.75)], [(5.75, 4.25), (6.5, 5), (1.5, 10), (0, 10), (0.75, 9.25)]]
HUD_CURSOR = [[(8.5, 8.5), (14, 8.5), (14, 10), (8.5, 10)]]

# Letters (20 high, stroke 4) as in generate-branding.py.
LETTERS = {
    'J': ([[(10, 0), (14, 0), (14, 16), (10, 20), (3, 20), (0, 17), (0, 12), (4, 12), (4, 16), (10, 16)]], 14),
    'O': ([[(3, 0), (11, 0), (14, 3), (14, 17), (11, 20), (3, 20), (0, 17), (0, 3)], [(4, 4), (4, 16), (10, 16), (10, 4)]], 14),
    'Y': ([[(0, 0), (4.4, 0), (7, 5.4), (9.6, 0), (14, 0), (9, 10), (9, 20), (5, 20), (5, 10)]], 14),
    'P': ([[(0, 0), (11, 0), (14, 3), (14, 10), (11, 13), (4, 13), (4, 20), (0, 20)], [(4, 4), (4, 9), (10, 9), (10, 4)]], 14),
    'R': ([[(0, 0), (11, 0), (14, 3), (14, 10), (11.6, 12.4), (14.4, 20), (10.2, 20), (7.6, 13), (4, 13), (4, 20), (0, 20)], [(4, 4), (4, 9), (10, 9), (10, 4)]], 14.4),
}


# ---------------------------------------------------------------- styles of a filled shape

def solid(polys, k):
    """White with a dark outline (reads on light backgrounds), like variant A."""
    return [layer(polys, WHITE, DARK, 1.1 * k), layer(polys, WHITE)]


def line_art(polys, k, holes=()):
    """The original logo's language: dark fill, white outer contour, inner contour and facet lines."""
    out = [layer(polys, '{StaticResource Brand.ShieldBrush}', WHITE, 0.55 * k)]
    for i, poly in enumerate(polys):
        inner = inset(poly, 0.55, hole=i in holes)
        out.append(layer([inner], None, WHITE, 0.22 * k, opacity=0.9))
        out.append(lines(list(zip(poly, inner)), WHITE, 0.16 * k, opacity=0.55))
    return out


def metal(polys, k, holes=(), blue_edge=None):
    """Faceted: a light top face and bevels lit from the upper left; one bevel in the blue accent."""
    out = [layer(polys, '#64748B', DARK, 0.8 * k)]
    light = (-0.6, -0.8)
    faces = []
    for pi, poly in enumerate(polys):
        inner = inset(poly, 0.7, hole=pi in holes)
        sign = (1 if area(poly) > 0 else -1) * (-1 if pi in holes else 1)
        for i in range(len(poly)):
            a, b = poly[i], poly[(i + 1) % len(poly)]
            ia, ib = inner[i], inner[(i + 1) % len(poly)]
            dx, dy = b[0] - a[0], b[1] - a[1]
            ln = math.hypot(dx, dy) or 1
            # outward normal
            nx, ny = dy / ln * sign, -dx / ln * sign
            lit = nx * light[0] + ny * light[1]
            color = '#F1F5F9' if lit > 0.45 else '#CBD5E1' if lit > 0 else '#94A3B8' if lit > -0.45 else '#475569'
            if blue_edge is not None and (pi, i) == blue_edge:
                color = BLUE
            out.append(layer([[a, b, ib, ia]], color))
        faces.append(inner)
    # One face geometry for the outline and its counters (even-odd: the counters stay open).
    out.append(layer(faces, '{StaticResource Brand.MetalFaceBrush}'))
    return out


# ---------------------------------------------------------------- the mark inside an icon

MARK = '''<DrawingGroup><DrawingGroup.Transform><MatrixTransform Matrix="{S},0,0,{S},{X},{Y}" /></DrawingGroup.Transform>
<GeometryDrawing Geometry="{StaticResource Brand.ShieldGeometry}" Brush="{StaticResource Brand.ShieldBrush}"><GeometryDrawing.Pen><Pen Brush="#F1F5F9" Thickness="3.2" LineJoin="Round" /></GeometryDrawing.Pen></GeometryDrawing>
<GeometryDrawing Geometry="{StaticResource Brand.WingGeometry}" Brush="#F8FAFC" />
<GeometryDrawing Geometry="{StaticResource Brand.CanardsGeometry}" Brush="#F8FAFC" />
<GeometryDrawing Geometry="{StaticResource Brand.FuselageGeometry}" Brush="#F8FAFC"><GeometryDrawing.Pen><Pen Brush="#1B1E24" Thickness="1.3" LineJoin="Round" /></GeometryDrawing.Pen></GeometryDrawing>
<GeometryDrawing Geometry="{StaticResource Brand.CanopyGeometry}" Brush="#38BDF8" />
</DrawingGroup>'''


def mark(s=0.86, x=0.0, y=0.0):
    return MARK.replace('{S}', fmt(s)).replace('{X}', fmt(x)).replace('{Y}', fmt(y))


def image(key, layers, w=100, h=100):
    return ('    <DrawingImage x:Key="' + key + '">\n        <DrawingImage.Drawing>\n            <DrawingGroup>\n'
            '                <GeometryDrawing Geometry="M0,0 H' + fmt(w) + ' V' + fmt(h) + ' H0 Z" Brush="Transparent" />\n' +
            ''.join('                ' + l + '\n' for l in layers) +
            '            </DrawingGroup>\n        </DrawingImage.Drawing>\n    </DrawingImage>\n')


# shield of the mark at scale .86: shoulder (81.7, 20.6), bottom tip (43, 83.4)
def shield_pt(p, s=0.86, x=0.0, y=0.0):
    return (p[0] * s + x, p[1] * s + y)


# ---------------------------------------------------------------- the concepts

def numeral_layers(name, t, k):
    """The concept's "2" (as in the icon corner and the title)."""
    if name == 'LineArt':
        return line_art(tf(TWO, t), k) + [layer(tf(TWO_TIP, t), BLUE)]
    if name == 'Formation':
        return (solid(tf(FORM_TOP, t), k) + solid(tf(FORM_SPINE, t), k) + solid(tf(FORM_FOOT, t), k) +
                [layer(tf(FORM_CANOPY, t), BLUE)])
    if name == 'Stencil':
        return [layer(tf(p, t), WHITE, DARK, 1.1 * k) for p in [[q] for q in STENCIL_TWO]] + [layer(tf(STENCIL_TWO, t), WHITE), layer(tf(TWO_TIP, t), BLUE)]
    if name == 'Hud':
        return [layer(tf(HUD_SEGS, t), WHITE, DARK, 1.0 * k), layer(tf(HUD_SEGS, t), WHITE), layer(tf(HUD_UNDER, t), BLUE, DARK, 0.8 * k)]
    if name == 'Metal':
        return metal(tf(TWO, t), k, blue_edge=(0, 4))
    return solid(tf(TWO, t), k) + [layer(tf(TWO_TIP, t), BLUE)]


def prompt_layers(name, t, k):
    if name == 'LineArt':
        return line_art(tf(CHEVRON, t), k) + [layer(tf(CURSOR, t), BLUE)]
    if name == 'Formation':
        return solid(tf(FORM_CHEV, t), k) + [layer(tf(CURSOR, t), BLUE, DARK, 0.8 * k)]
    if name == 'Stencil':
        return [layer(tf(STENCIL_CHEV, t), WHITE, DARK, 1.1 * k), layer(tf(STENCIL_CHEV, t), WHITE), layer(tf(CURSOR, t), BLUE, DARK, 0.8 * k)]
    if name == 'Hud':
        return [layer(tf(HUD_CHEV, t), WHITE, DARK, 1.0 * k), layer(tf(HUD_CHEV, t), WHITE), layer(tf(HUD_CURSOR, t), BLUE, DARK, 0.8 * k)]
    if name == 'Metal':
        return metal(tf(CHEVRON, t), k) + [layer(tf(CURSOR, t), BLUE, DARK, 0.8 * k)]
    return solid(tf(CHEVRON, t), k) + [layer(tf(CURSOR, t), BLUE, DARK, 0.8 * k)]


def stencil_plate(glyph_polys, accent):
    """A second, smaller kite shield over the main shield's lower right edge, the glyph cut out of it."""
    kite = [(77, 40), (99.5, 53), (77, 99.5), (54.5, 53)]
    inner = [(77, 44.6), (95, 55), (77, 92.8), (59, 55)]
    return [layer([kite] + glyph_polys, '{StaticResource Brand.ShieldBrush}', '#F1F5F9', 2.4),
            layer([inner], None, BLUE, 0.8, opacity=0.5)] + accent


def integrated_two(small):
    """The "2" grown out of the shield: the shield's lower right edge is its diagonal, the bottom point its tail."""
    s, x, y = (0.78, 0, 14) if not small else (0.74, 0, 16)
    S = shield_pt((95, 24), s, x, y)        # right shoulder
    T = shield_pt((50, 97), s, x, y)        # bottom point
    Top = shield_pt((50, 4), s, x, y)       # top point
    d = (T[0] - S[0], T[1] - S[1])
    ln = math.hypot(*d)
    n = (d[1] / ln, -d[0] / ln)
    if n[0] < 0:
        n = (-n[0], -n[1])
    g = 1.8
    u = (d[0] / ln, d[1] / ln)
    foot_top, foot_bottom = T[1] - 3, 96.5
    inner_s = (S[0] + n[0] * g, S[1] + n[1] * g)            # the inner side runs along the shield's edge

    def along(p, yy):
        return (p[0] + u[0] * (yy - p[1]) / u[1], yy)

    E = (S[0] + 16, S[1] - 6)                               # outer right side of the bowl, then parallel to the edge
    poly = [
        (Top[0] + 13, Top[1] - 3.5),                        # hook tip, above the upper right edge
        (Top[0] + 19, Top[1] - 12),                         # chamfer up
        (S[0] + 6, Top[1] - 12),                            # top
        (E[0], Top[1] - 2),                                 # chamfer down
        E,
        along(E, foot_top),                                 # the diagonal's outer side
        (99, foot_top), (99, foot_bottom),                  # the foot
        (T[0] - 4, foot_bottom),
        along(inner_s, foot_top),
        inner_s,                                            # up along the shield's edge
        (S[0] + 7, S[1] - 7),
        (S[0] + 7, Top[1] - 1),                             # the bowl's inside
        (S[0] + 4, Top[1] - 4.5),
        (Top[0] + 21, Top[1] - 4.5),
        (Top[0] + 18.5, Top[1] - 1),
    ]
    tip = [(99 - 6.5, foot_bottom), (99, foot_bottom - 6.5), (99, foot_bottom)]
    return mark(s, x, y), [poly], tip


def integrated_prompt(small):
    """">" = the shield's right shoulder outlined, "_" in blue below it."""
    s, x, y = (0.78, 0, 14) if not small else (0.74, 0, 16)
    Top = shield_pt((50, 4), s, x, y)
    S = shield_pt((95, 24), s, x, y)
    T = shield_pt((50, 97), s, x, y)
    w = 6.5 if not small else 8
    a = (Top[0] + (S[0] - Top[0]) * 0.45, Top[1] + (S[1] - Top[1]) * 0.45)
    b = (S[0] + (T[0] - S[0]) * 0.42, S[1] + (T[1] - S[1]) * 0.42)
    chev = [(a[0], a[1] - 4), (S[0] + w + 3, S[1]), (b[0] + 1.5, b[1] + w), (b[0] - 3.5, b[1] + 2), (S[0] + 2.5, S[1] + 0.5), (a[0] - 1.5, a[1] + 1.5)]
    cursor = [(70, 86), (99, 86), (99, 86 + w), (70, 86 + w)]
    return mark(s, x, y), [chev], cursor


CONCEPTS = ['LineArt', 'Formation', 'Stencil', 'Hud', 'Metal', 'Integrated']


def icon_resources():
    out = []
    for name in CONCEPTS:
        for small in (False, True):
            suffix = '.Small' if small else ''
            app, eng = icon_layers(name, small)
            out.append(image('Brand.AppIcon.' + name + suffix, app))
            out.append(image('Brand.EngineIcon.' + name + suffix, eng))
    return ''.join(out)


def default_icons(name):
    """Brand.AppIcon / .Small and Brand.EngineIcon / .Small from a concept (generate-icons.ps1 -Icon <name>)."""
    out = []
    for small in (False, True):
        suffix = '.Small' if small else ''
        app, eng = icon_layers(name, small)
        out.append(image('Brand.AppIcon' + suffix, app))
        out.append(image('Brand.EngineIcon' + suffix, eng))
    return ''.join(out)


def icon_layers(name, small):
    """The icon with the concept's "2" and the engine icon with its ">_" (small: the 16 / 24 px form)."""
    if name == 'Integrated':
        m, polys, tip = integrated_two(small)
        app = [m] + solid(polys, 3.2) + [layer([tip], BLUE)]
        m2, chev, cursor = integrated_prompt(small)
        eng = [m2] + solid(chev, 3.2) + [layer([cursor], BLUE, DARK, 2.4)]
    elif name == 'Stencil':
        s = 2.1 if not small else 2.2
        tx = place(66.5 if not small else 66, 49 if not small else 48.5, s)
        app = [mark(0.84 if not small else 0.78)] + stencil_plate(tf(TWO, tx), [layer(tf(TWO_TIP, tx), BLUE)])
        pc = place(64.5, 53, 1.85)
        eng = [mark(0.84 if not small else 0.78)] + stencil_plate(tf(CHEVRON, pc), [layer(tf(CURSOR, pc), BLUE)])
    else:
        if not small:
            tx, k = place(65, 55 if name != 'Hud' else 50, 3.0), 3.0
            pc = place(63, 70, 2.45)
            ms = 0.86
        else:
            tx, k = place(57, 42 if name != 'Hud' else 36, 4.0), 4.0
            pc = place(54, 62, 3.25)
            ms = 0.74
        small_name = name if not (small and name in ('LineArt',)) else 'Solid'
        app = [mark(ms)] + numeral_layers(small_name, tx, k)
        eng = [mark(ms)] + prompt_layers(small_name, pc, k * 0.82)
    return app, eng


# ---------------------------------------------------------------- titles

def word(text, gap=3.2):
    x = 0
    placed = []
    for ch in text:
        polys, w = LETTERS[ch]
        placed.append((x, polys))
        x += w + gap
    return placed, x - gap


def title(key, name):
    placed, width = word('JOYPRO')
    k = 1.0
    layers = []
    for x, polys in placed:
        t = place(x, 0, 1.0, SKEW, 20)
        ps = tf(polys, t)
        holes = tuple(range(1, len(ps)))
        if name == 'LineArt':
            layers += [layer(ps, '#1E2A3F', WHITE, 0.8)]
            for i, poly in enumerate(ps):
                inner = inset(poly, 0.9, hole=i in holes)
                layers.append(layer([inner], None, WHITE, 0.35, opacity=0.85))
        elif name == 'Metal':
            layers += metal(ps, 1.0, holes)
        elif name == 'Hud':
            layers += [layer(ps, WHITE)]
        else:
            layers += [layer(ps, WHITE)]
    two_x = width + 10
    s = 20 / 14
    t2 = place(two_x, 0 if name != 'Hud' else -1.5, s * (0.9 if name == 'Hud' else 1.0), SKEW, 14)
    nm = name if name != 'Integrated' else 'Solid'
    if name == 'Stencil':
        layers += [layer(tf(STENCIL_TWO, t2), WHITE), layer(tf(TWO_TIP, t2), BLUE)]
    elif name == 'Formation':
        layers += [layer(tf(FORM_TOP + FORM_SPINE + FORM_FOOT, t2), WHITE),
                   layer(tf(FORM_CANOPY, t2), BLUE)]
    elif name == 'Hud':
        layers += [layer(tf(HUD_SEGS, t2), WHITE), layer(tf(HUD_UNDER, t2), BLUE)]
        # the HUD chevron under the whole title
        under = [[(0, 22.5), (width / 2, 24.5), (width, 22.5), (width, 23.4), (width / 2, 25.4), (0, 23.4)]]
        layers.append(layer(under, BLUE, None, 0, opacity=None))
    elif name == 'Metal':
        layers += metal(tf(TWO, t2), 1.0, blue_edge=(0, 4))
    elif name == 'LineArt':
        layers += [layer(tf(TWO, t2), '#1E2A3F', WHITE, 0.8), layer([inset(tf(TWO, t2)[0], 0.9)], None, WHITE, 0.35, opacity=0.85), layer(tf(TWO_TIP, t2), BLUE)]
    else:
        layers += [layer(tf(TWO, t2), WHITE), layer(tf(TWO_TIP, t2), BLUE)]
    total = two_x + 10 * s + 20 * math.tan(math.radians(SKEW)) + 1
    return image(key, layers, total, 26 if name == 'Hud' else 20)


def title_resources():
    return ''.join(title('Brand.Wordmark.' + n, n) for n in CONCEPTS)


def brushes():
    return '''    <LinearGradientBrush x:Key="Brand.MetalFaceBrush" StartPoint="0%,0%" EndPoint="100%,100%">
        <GradientStop Color="#FFFFFF" Offset="0" />
        <GradientStop Color="#CBD5E1" Offset="1" />
    </LinearGradientBrush>
'''


# ================================================================ round 3: the faceted "2" (Icon A placement, title W3)
# The numeral is built from separate pieces like the mark (rim, wing, fuselage, canopy): hook blade, top bar with
# shoulder, diagonal blade, foot, and the blue foot tip. All strokes have the same optical weight (about 3.3 on the
# 10 x 14 grid; the hook and the diagonal a little heavier, as slanted strokes look thinner).
#   FacetA  pieces with thin gaps, alternating white / light grey
#   FacetB  pieces joined by thin dark seams, each piece split into a light and a grey plane
#   FacetC  pieces with gaps and each piece split into two planes
F2 = {
    'hook': [(0, 4.8), (3.3, 0), (4.6, 0), (5.04, 3.3), (2.72, 6.67)],
    'top': [(4.6, 0), (7.6, 0), (10, 2.4), (10, 4.0), (6.7, 4.0), (6.7, 3.8), (6.2, 3.3), (5.04, 3.3)],
    'blade': [(6.7, 4.0), (10, 4.0), (10, 6.3), (5.5, 10.8), (0.7, 10.8), (6.7, 4.8)],
    'foot': [(0, 10.8), (10, 10.8), (10, 11.4), (7.0, 14), (0, 14)],
}
F2_TIP = [(10, 11.4), (10, 14), (7.0, 14)]
F2_OUTLINE = [(0, 4.8), (3.3, 0), (7.6, 0), (10, 2.4), (10, 6.3), (5.5, 10.8), (10, 10.8), (10, 14), (0, 14), (0, 11.5),
              (6.7, 4.8), (6.7, 3.8), (6.2, 3.3), (5.04, 3.3), (2.72, 6.67)]
F_PROMPT = {
    'upper': [(0, 0), (4.4, 0), (9.4, 5), (5, 5)],
    'lower': [(5, 5), (9.4, 5), (4.4, 10), (0, 10)],
}
F_CURSOR = [(10.6, 7.4), (15.4, 7.4), (15.4, 10), (10.6, 10)]
TONE_A = '#F8FAFC'
TONE_B = '#C9D3E0'
FACET_STYLES = ['FacetA', 'FacetB', 'FacetC']


def _split(poly):
    """Two planes of a piece: cut from the first vertex to the middle one."""
    m = len(poly) // 2
    return poly[:m + 1], [poly[0]] + poly[m:]


def _pieces(pieces, t, style, i_offset=0):
    out = []
    g = 0.32 if style in ('FacetA', 'FacetC') else 0.12
    for i, (name, p) in enumerate(pieces):
        q = inset(p, g / 2)
        if style == 'FacetA':
            out.append(layer(tf([q], t), TONE_A if (i + i_offset) % 2 == 0 else TONE_B))
        else:
            a, b = _split(q)
            out += [layer(tf([a], t), TONE_A), layer(tf([b], t), TONE_B)]
    return out, g


def faceted_layers(pieces, accent, t, k, style):
    """pieces: [(name, polygon)], accent: the blue polygon; t: placement; k: px per grid unit."""
    whole = [tf([p], t)[0] for _, p in pieces] + [tf([accent], t)[0]]
    out = [layer(whole, DARK, DARK, 1.1 * k)]            # outline for light backgrounds; also fills the gaps / seams
    parts, g = _pieces(pieces, t, style)
    return out + parts + [layer(tf([inset(accent, g / 2)], t), BLUE)]


def faceted_small_two(t, k):
    """16 / 24 px: one shape with the blue tip (the facets would only blur)."""
    return [layer(tf([F2_OUTLINE], t), WHITE, DARK, 1.1 * k), layer(tf([F2_OUTLINE], t), WHITE), layer(tf([F2_TIP], t), BLUE)]


def faceted_small_prompt(t, k):
    whole = tf(list(F_PROMPT.values()), t)
    return [layer(whole, WHITE, DARK, 1.1 * k), layer(whole, WHITE), layer(tf([F_CURSOR], t), BLUE, DARK, 0.9 * k)]


def faceted_icon_layers(style, small):
    if not small:
        return ([mark(0.86)] + faceted_layers(list(F2.items()), F2_TIP, place(65, 55, 3.0), 3.0, style),
                [mark(0.86)] + faceted_layers(list(F_PROMPT.items()), F_CURSOR, place(63, 70, 2.2), 2.2, style))
    return ([mark(0.74)] + faceted_small_two(place(57, 42, 4.0), 4.0),
            [mark(0.74)] + faceted_small_prompt(place(53, 62, 2.95), 2.95))


def faceted_icons(key_app, key_engine, style):
    out = []
    for small in (False, True):
        suffix = '.Small' if small else ''
        app, eng = faceted_icon_layers(style, small)
        out.append(image(key_app + suffix, app))
        out.append(image(key_engine + suffix, eng))
    return ''.join(out)


def faceted_title(key, style):
    """W3: slanted letters, JOY white, PRO blue, and the faceted "2"."""
    placed, width = word('JOYPRO')
    layers = [layer(tf(polys, place(x, 0, 1.0, SKEW, 20)), WHITE if i < 3 else BLUE) for i, (x, polys) in enumerate(placed)]
    two_x = width + 10
    s = 20 / 14
    t2 = place(two_x, 0, s, SKEW, 14)
    parts, g = _pieces(list(F2.items()), t2, style)
    layers += parts + [layer(tf([inset(F2_TIP, g / 2)], t2), BLUE)]
    total = two_x + 10 * s + 20 * math.tan(math.radians(SKEW)) + 1
    return image(key, layers, total, 20)


def round3_resources():
    out = ''.join(faceted_icons('Brand.AppIcon.' + st, 'Brand.EngineIcon.' + st, st) for st in FACET_STYLES)
    return out + ''.join(faceted_title('Brand.Wordmark.W3' + st, st) for st in FACET_STYLES)


# ================================================================ round 4: the faceted "2" in the mark's angle language
# The mark has almost no horizontal or vertical lines. Its angles (from the path data above, degrees from the
# horizontal, y down):
#   shield upper edges   (50,4)-(95,24)      24.0
#   canard leading edge  (36,59)-(48,66.5)   32.0
#   canard inner edge    (36,59)-(48.4,73)   48.5
#   wing sweep           (79,26)-(50,62)     51.1
#   shield lower edges   (95,24)-(50,97)     58.4
#   fuselage sides       (50,9)-(52.2,22)    80.4
# Round 4 builds the numeral and its seams only from these angles: a top edge falling to the right, the shoulder cut
# at the canard angle, a right side leaning like the fuselage, the diagonal at the wing sweep (or the shield edge), a
# foot whose edges rise slightly to the right and whose ends lean like the fuselage sides, hook end and
# seams at shield / canard angles. Every stroke is offset by the same weight (slanted ones a little heavier).
#   AngleA  mild: top 8 deg, foot 3 deg, right side 84 deg, diagonal at the wing sweep
#   AngleB  medium: top 14 deg, foot 5 deg, right side 80 deg (fuselage), diagonal at the wing sweep
#   AngleC  strong: top 24 deg (shield edge), foot 8 deg, right side 76 deg, diagonal at the shield's lower edge
ANGLE_STYLES = {
    'AngleA': {'top': 8, 'foot': 3, 'base': 2, 'right': 84, 'diag': 51.1},
    'AngleB': {'top': 14, 'foot': 5, 'base': 3, 'right': 80.4, 'diag': 51.1},
    'AngleC': {'top': 24, 'foot': 8, 'base': 5, 'right': 76, 'diag': 58.4},
}


def _dir(angle):
    a = math.radians(angle)
    return (math.cos(a), math.sin(a))


def _line(p, angle):
    return (p, _dir(angle))


def _cross(l1, l2):
    (p, d), (q, e) = l1, l2
    den = d[0] * e[1] - d[1] * e[0]
    t = ((q[0] - p[0]) * e[1] - (q[1] - p[1]) * e[0]) / den
    return (p[0] + d[0] * t, p[1] + d[1] * t)


def _shift(l, dist, toward):
    """The line moved by dist along its normal on the side of the point toward."""
    (p, d) = l
    n = (-d[1], d[0])
    if (toward[0] - p[0]) * n[0] + (toward[1] - p[1]) * n[1] < 0:
        n = (-n[0], -n[1])
    return ((p[0] + n[0] * dist, p[1] + n[1] * dist), d)


def angled_two(cfg):
    """Pieces (hook, top, blade, foot), the blue tip and the whole outline of the angled numeral (about 10.5 x 14)."""
    W, Wd = 3.3, 3.5                                            # straight and slanted stroke weight
    centre = (5.0, 6.0)
    HO = _line((0, 5.4), -58.4)                                 # hook outer side: shield lower edge, rising
    T = _line((4.4, 0), cfg['top'])                             # top: falls to the right
    R = _line((10.4, 4.0), 180 - cfg['right'])                  # right side: leans like the fuselage
    corner = _cross(T, R)
    SH = _line((corner[0] - 2.6, corner[1]), 48.5)              # shoulder cut at the canard's inner edge angle
    D = _line(_cross(R, _line((0, 6.6), 0)), 180 - cfg['diag'])  # diagonal: wing sweep / shield edge, falling left
    FT = _line((5, 10.9), -cfg['foot'])                         # foot top rises to the right
    FB = _line((5, 14), -cfg['base'])                           # foot bottom rises a little
    FR = _line((10.5, 12.4), 180 - 80.4)                        # foot ends lean like the fuselage sides
    FL = _line((0.2, 12.4), 180 - 80.4)
    HE = _line((0, 5.4), 32)                                    # hook end: canard leading edge angle
    HI = _shift(HO, Wd, centre)
    TI = _shift(T, W, centre)
    RI = _shift(R, W, centre)
    SHI = _shift(SH, W * 0.9, centre)
    DI = _shift(D, Wd, (0, 0))
    v = {
        'h0': _cross(HO, HE), 'h1': _cross(HO, T), 't1': _cross(T, SH), 's1': _cross(SH, R), 'r1': _cross(R, D),
        'd1': _cross(D, FT), 'f1': _cross(FT, FR), 'f2': _cross(FR, FB), 'f3': _cross(FB, FL), 'f4': _cross(FL, FT),
        'di0': _cross(FT, DI), 'di1': _cross(DI, RI), 'ri1': _cross(RI, SHI), 'ti1': _cross(SHI, TI), 'hi1': _cross(TI, HI),
        'hi0': _cross(HI, HE),
    }
    # seams: hook|top at the shield lower edge angle, top|blade at the canard angle, foot|tip at the shield upper edge
    seam1 = _line(_cross(T, _line((4.9, 0), 90)), 180 - 58.4)
    s1o, s1i = _cross(T, seam1), _cross(TI, seam1)
    seam2 = _line(_cross(RI, _line((0, 5.0), 0)), 32)
    s2i, s2o = _cross(RI, seam2), _cross(R, seam2)
    seam3 = _line((8.0, 14), -58.4)
    s3t, s3b = _cross(FT, seam3), _cross(FB, seam3)
    pieces = [
        ('hook', [v['h0'], v['h1'], s1o, s1i, v['hi1'], v['hi0']]),
        ('top', [s1o, v['t1'], v['s1'], s2o, s2i, v['ri1'], v['ti1'], s1i]),
        ('blade', [s2i, s2o, v['r1'], v['d1'], v['di0'], v['di1']]),
        ('foot', [v['f4'], v['di0'], v['d1'], s3t, s3b, v['f3']]),
    ]
    tip = [s3t, v['f1'], v['f2'], s3b]
    outline = [v['h0'], v['h1'], v['t1'], v['s1'], v['r1'], v['d1'], v['f1'], v['f2'], v['f3'], v['f4'], v['di0'], v['di1'],
               v['ri1'], v['ti1'], v['hi1'], v['hi0']]
    # move into a box starting at 0, 0
    xs = [p[0] for p in outline]
    ys = [p[1] for p in outline]
    ox, oy = min(xs), min(ys)
    mv = lambda poly: [(x - ox, y - oy) for x, y in poly]
    return [(n, mv(p)) for n, p in pieces], mv(tip), mv(outline)


def angled_prompt(cfg):
    """">_": two long chevron arms, ends cut like the fuselage, the seam at the canard angle; a slanted blue cursor."""
    W, H = 3.0, 12.0
    arm = 40 + (cfg['top'] - 8) * 0.3
    UA = _line((0, 0), arm)
    LA = _line((0, H), -arm)
    apex = _cross(UA, LA)
    UI = _shift(UA, W, (0, H / 2))
    LI = _shift(LA, W, (0, H / 2))
    ia = _cross(UI, LI)
    E0 = _line((0, 0), 180 - 80.4)
    E1 = _line((0, H), -(180 - 80.4))
    e0, e0i = _cross(UA, E0), _cross(UI, E0)
    e1, e1i = _cross(LA, E1), _cross(LI, E1)
    seam = _line(ia, 32)
    s = _cross(LA, seam)
    pieces = [('upper', [e0, apex, s, ia, e0i]), ('lower', [ia, s, e1, e1i])]
    c0 = apex[0] + 1.6
    slope = math.tan(math.radians(cfg['base']))
    cursor = [(c0, H - 2.8), (c0 + 5.4, H - 2.8 - 5.4 * slope), (c0 + 5.4 - 0.5, H - 5.4 * slope), (c0 - 0.5, H)]
    ox = min(min(x for _, poly in pieces for x, _ in poly), min(x for x, _ in cursor))
    oy = min(min(y for _, poly in pieces for _, y in poly), min(y for _, y in cursor))
    mv = lambda poly: [(x - ox, y - oy) for x, y in poly]
    return [(n, mv(q)) for n, q in pieces], mv(cursor)


def angled_layers(pieces, accent, t, k, gap=0.32):
    whole = [tf([p], t)[0] for _, p in pieces] + [tf([accent], t)[0]]
    out = [layer(whole, DARK, DARK, 1.1 * k)]
    for i, (name, p) in enumerate(pieces):
        out.append(layer(tf([inset(p, gap / 2)], t), TONE_A if i % 2 == 0 else TONE_B))
    out.append(layer(tf([inset(accent, gap / 2)], t), BLUE))
    return out


def angled_icon_layers(style, small):
    cfg = ANGLE_STYLES[style]
    pieces, tip, outline = angled_two(cfg)
    ppieces, cursor = angled_prompt(cfg)
    w = max(p[0] for p in outline)
    h = max(p[1] for p in outline)
    pw = max(max(p[0] for _, poly in ppieces for p in poly), max(p[0] for p in cursor))
    ph = max(max(p[1] for _, poly in ppieces for p in poly), max(p[1] for p in cursor))

    def corner(width, height, max_s, room_w, room_h):
        s = min(max_s, room_w / width, room_h / height)
        return place(99 - width * s, 98.5 - height * s, s), s

    if not small:
        t, k = corner(w, h, 3.0, 36, 44)
        tp, kp = corner(pw, ph, 2.7, 38, 31)
        return ([mark(0.86)] + angled_layers(pieces, tip, t, k),
                [mark(0.86)] + angled_layers(ppieces, cursor, tp, kp))
    t, k = corner(w, h, 4.0, 44, 57)
    tp, kp = corner(pw, ph, 2.95, 46, 36)
    whole = tf([p for _, p in ppieces], tp)
    return ([mark(0.74), layer(tf([outline], t), WHITE, DARK, 1.1 * k), layer(tf([outline], t), WHITE), layer(tf([tip], t), BLUE)],
            [mark(0.74), layer(whole, WHITE, DARK, 1.1 * kp), layer(whole, WHITE), layer(tf([cursor], tp), BLUE, DARK, 0.9 * kp)])


def angled_icons(key_app, key_engine, style):
    out = []
    for small in (False, True):
        suffix = '.Small' if small else ''
        app, eng = angled_icon_layers(style, small)
        out.append(image(key_app + suffix, app))
        out.append(image(key_engine + suffix, eng))
    return ''.join(out)


def angled_title(key, style):
    """W3: slanted letters, JOY white, PRO blue, and the angled faceted "2"."""
    pieces, tip, outline = angled_two(ANGLE_STYLES[style])
    placed, width = word('JOYPRO')
    layers = [layer(tf(polys, place(x, 0, 1.0, SKEW, 20)), WHITE if i < 3 else BLUE) for i, (x, polys) in enumerate(placed)]
    two_x = width + 9
    h = max(p[1] for p in outline)
    w = max(p[0] for p in outline)
    s = 20 / h
    t2 = place(two_x, 0, s, SKEW, 20)
    for i, (name, p) in enumerate(pieces):
        layers.append(layer(tf([inset(p, 0.16)], t2), TONE_A if i % 2 == 0 else TONE_B))
    layers.append(layer(tf([inset(tip, 0.16)], t2), BLUE))
    total = two_x + w * s + 20 * math.tan(math.radians(SKEW)) + 1
    return image(key, layers, total, 20)


def round4_resources():
    out = ''.join(angled_icons('Brand.AppIcon.' + st, 'Brand.EngineIcon.' + st, st) for st in ANGLE_STYLES)
    return out + ''.join(angled_title('Brand.Wordmark.W3' + st, st) for st in ANGLE_STYLES)


# ================================================================ round 5: the "2" from strokes along the shield's edge
# After the user's sketches: no solid numeral. The "2" is a few line strokes just outside the shield's lower right
# edge, every stroke parallel to that edge (58.4 degrees), the long one hugging it with a constant gap like a second
# rim. Like a "2" written in italics along the edge: the long stroke is its diagonal, short strokes (or groups of short
# ticks) set level to the left of its top end are the top, those set level to the right of its bottom end the foot.
# One stroke (or the stroke's end) is blue, like the canopy.
#   Rim3          three strokes (sketch 1): top short, long diagonal, foot short; round ends, blue foot
#   Rim3Tip       the same with chamfered (level cut) ends; the diagonal ends in a blue tip
#   Hatch         two ticks for the top, the long stroke, three ticks for the foot (sketch 2); the outer foot tick blue
#   HatchRim      Hatch plus a thin light second rim along the whole lower right edge
#   HatchChamfer  Hatch with heavier chamfered strokes; the lower end of the diagonal blue
STROKE_STYLES = ['Rim3', 'Rim3Tip', 'Hatch', 'HatchRim', 'HatchChamfer']
EDGE_TOP, EDGE_SHOULDER, EDGE_TIP = (50, 4), (95, 24), (50, 97)


def _edge_frame(ms):
    """The lower right edge of the mark drawn at scale ms: bottom point, unit vector up the edge, outward normal."""
    tip = (EDGE_TIP[0] * ms, EDGE_TIP[1] * ms)
    sh = (EDGE_SHOULDER[0] * ms, EDGE_SHOULDER[1] * ms)
    ln = math.hypot(sh[0] - tip[0], sh[1] - tip[1])
    up = ((sh[0] - tip[0]) / ln, (sh[1] - tip[1]) / ln)
    out = (-up[1], up[0])
    if out[0] < 0:
        out = (-out[0], -out[1])
    return tip, up, out


def stroke_set(style, A, B, up):
    """The strokes [(p, q, blue)] of a style for a diagonal from B (bottom) to A (top); weight factor, cap, rim flag."""
    L = math.hypot(A[0] - B[0], A[1] - B[1])

    def seg(c, length):
        return ((c[0] - up[0] * length / 2, c[1] - up[1] * length / 2), (c[0] + up[0] * length / 2, c[1] + up[1] * length / 2))

    def at(p, dx, along):
        return (p[0] + dx + up[0] * along, p[1] + up[1] * along)

    strokes = []
    if style in ('Rim3', 'Rim3Tip'):
        short = 0.34 * L
        strokes.append(seg(at(A, -0.27 * L, -short * 0.32), short) + (False,))
        strokes.append((B, A, False))
        strokes.append(seg(at(B, 0.29 * L, short * 0.32), short) + (style == 'Rim3',))
        if style == 'Rim3Tip':
            strokes.append((at(A, 0, -0.13 * L), A, True))
        return strokes, 1.0, 'round' if style == 'Rim3' else 'chamfer', False
    tick = 0.24 * L
    sp = 0.135 * L * (1.08 if style == 'HatchChamfer' else 1.0)
    for k in (2, 1):
        strokes.append(seg(at(A, -k * sp, -tick * 0.3), tick) + (False,))
    strokes.append((B, A, False))
    for k in (1, 2, 3):
        strokes.append(seg(at(B, k * sp, tick * 0.3), tick) + (k == 3 and style != 'HatchChamfer',))
    if style == 'HatchChamfer':
        strokes.append((B, at(B, 0, 0.2 * L), True))
    return strokes, (0.85 if style != 'HatchChamfer' else 1.0), 'chamfer' if style == 'HatchChamfer' else 'round', style == 'HatchRim'


def _chamfer(a, b, w):
    """A stroke with its ends cut level (horizontal), like a stencil."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    ln = math.hypot(dx, dy)
    nx, ny = -dy / ln * w / 2, dx / ln * w / 2

    def level(p, c):
        t = (c[1] - p[1]) / dy
        return (p[0] + dx * t, p[1] + dy * t)
    return [level((a[0] + nx, a[1] + ny), a), level((b[0] + nx, b[1] + ny), b), level((b[0] - nx, b[1] - ny), b), level((a[0] - nx, a[1] - ny), a)]


def _pen_line(a, b, color, w, cap='Round'):
    return ('<GeometryDrawing Geometry="M' + fmt(a[0]) + ',' + fmt(a[1]) + ' L' + fmt(b[0]) + ',' + fmt(b[1]) +
            '"><GeometryDrawing.Pen><Pen Brush="' + color + '" Thickness="' + fmt(w) + '" LineCap="' + cap + '" /></GeometryDrawing.Pen></GeometryDrawing>')


def draw_strokes(strokes, w, cap, outline, halo):
    out = []
    if outline:
        for a, b, _ in strokes:
            out.append(_pen_line(a, b, DARK, w + halo) if cap == 'round' else layer([_chamfer(a, b, w + halo)], DARK))
    for a, b, blue in strokes:
        color = BLUE if blue else WHITE
        out.append(_pen_line(a, b, color, w) if cap == 'round' else layer([_chamfer(a, b, w)], color))
    return out


def stroke_icon_layers(style, small):
    ms = 0.94 if not small else 0.9
    tip, up, out = _edge_frame(ms)

    def xy(s, d):
        return (tip[0] + up[0] * s + out[0] * d, tip[1] + up[1] * s + out[1] * d)

    d = 12.5 if not small else 13.5
    A, B = xy(50, d), xy(17, d)
    strokes, wf, cap, rim = stroke_set(style if not small else 'Rim3', A, B, up)
    w = (3.9 if not small else 7.0) * wf
    app = [mark(ms)]
    if rim and not small:
        app.append(_pen_line(xy(2, 3.4), xy(84, 3.4), WHITE, 1.2, 'Flat'))
    app += draw_strokes(strokes, w, cap if not small else 'round', True, 1.9 if not small else 2.4)

    # engine: ">" as two strokes following the shoulder outside the rim (parallel to the upper and the lower right
    # edge), the "_" as a blue stroke parallel to the lower edge below it
    top = (EDGE_TOP[0] * ms, EDGE_TOP[1] * ms)
    sh = (EDGE_SHOULDER[0] * ms, EDGE_SHOULDER[1] * ms)
    u1 = (sh[0] - top[0], sh[1] - top[1])
    l1 = math.hypot(*u1)
    u1 = (u1[0] / l1, u1[1] / l1)
    gap = 7.0 if not small else 8.0
    apex = (sh[0] + gap * 1.1, sh[1] + gap * 0.15)
    arm = 20 if not small else 18
    a_up = (apex[0] - u1[0] * arm, apex[1] - u1[1] * arm)
    a_dn = (apex[0] - up[0] * arm, apex[1] - up[1] * arm)
    ul0, ul1 = xy(16, 15), xy(30, 15)
    wt = 3.9 if not small else 7.0
    eng = [mark(ms)]
    for p, q in ((a_up, apex), (apex, a_dn), (ul0, ul1)):
        eng.append(_pen_line(p, q, DARK, wt + 1.9))
    eng += [_pen_line(a_up, apex, WHITE, wt), _pen_line(apex, a_dn, WHITE, wt), _pen_line(ul0, ul1, BLUE, wt)]
    return app, eng


def stroke_icons(key_app, key_engine, style):
    out = []
    for small in (False, True):
        suffix = '.Small' if small else ''
        app, eng = stroke_icon_layers(style, small)
        out.append(image(key_app + suffix, app))
        out.append(image(key_engine + suffix, eng))
    return ''.join(out)


def stroke_title(key, style):
    """W3 (JOY white, PRO blue) followed by the stroke "2", its strokes at the shield edge's angle."""
    placed, width = word('JOYPRO')
    layers = [layer(tf(polys, place(x, 0, 1.0, SKEW, 20)), WHITE if i < 3 else BLUE) for i, (x, polys) in enumerate(placed)]
    up = (math.cos(math.radians(-58.4)), math.sin(math.radians(-58.4)))
    # the diagonal 18 high, its strokes measured, then the whole set moved behind the letters
    L = 18 / abs(up[1])
    B0 = (0.0, 18.0)
    A0 = (B0[0] + up[0] * L, B0[1] + up[1] * L)
    strokes, wf, cap, rim = stroke_set(style, A0, B0, up)
    w = 2.9 * wf
    xs = [p[0] for a, b, _ in strokes for p in (a, b)]
    ys = [p[1] for a, b, _ in strokes for p in (a, b)]
    dx = width + 7 - min(xs) + w / 2
    dy = 1.0 - min(ys) + w / 2
    moved = [((a[0] + dx, a[1] + dy), (b[0] + dx, b[1] + dy), blue) for a, b, blue in strokes]
    layers += draw_strokes(moved, w, cap, False, 0)
    total = width + 7 + (max(xs) - min(xs)) + w + 2
    return image(key, layers, total, 20 + w / 2)


def round5_resources():
    out = ''.join(stroke_icons('Brand.AppIcon.' + st, 'Brand.EngineIcon.' + st, st) for st in STROKE_STYLES)
    return out + ''.join(stroke_title('Brand.Wordmark.W3' + st, st) for st in STROKE_STYLES)


# ================================================================ round 6: variations of Rim3Tip
# Three strokes parallel to the shield's lower right edge (top, diagonal, foot), cut ends, the blue on the diagonal's
# tip; only details vary. L = length of the diagonal; offsets are horizontal (level), in parts of L.
#   w      stroke weight (icon units)          d      gap of the diagonal from the shield's edge
#   s0/s1  where the diagonal starts / ends up the edge
#   short  length of the top and foot strokes  top/foot  their level offset left of the top / right of the bottom
#   cut    'level' (horizontal cut) or 'edge' (cut parallel to the shield's upper edges, 24 degrees)
#   blue   'tip' (end of the diagonal), 'segment' (a separate short stroke beyond its top), 'top' (the top stroke)
#   rim    a 4th thin light line along the rim
RIM_STYLES = {
    'TipThin': {'w': 2.9, 'd': 11.0, 's0': 16, 's1': 51, 'short': 0.34, 'top': 0.31, 'foot': 0.33, 'cut': 'level', 'blue': 'tip', 'rim': False},
    'TipBold': {'w': 5.0, 'd': 13.5, 's0': 17, 's1': 50, 'short': 0.36, 'top': 0.30, 'foot': 0.32, 'cut': 'level', 'blue': 'tip', 'rim': False},
    'TipEdgeCut': {'w': 3.9, 'd': 12.5, 's0': 17, 's1': 50, 'short': 0.34, 'top': 0.29, 'foot': 0.31, 'cut': 'edge', 'blue': 'segment', 'rim': False},
    'TipBlueTop': {'w': 3.9, 'd': 12.5, 's0': 17, 's1': 50, 'short': 0.34, 'top': 0.30, 'foot': 0.32, 'cut': 'level', 'blue': 'top', 'rim': False},
    'TipRimLine': {'w': 3.6, 'd': 13.0, 's0': 17, 's1': 50, 'short': 0.34, 'top': 0.29, 'foot': 0.31, 'cut': 'level', 'blue': 'tip', 'rim': True},
    # round 7: TipBold with a clear constant gap to the shield and the bar ends on common level cut lines
    'TipBoldAligned': {'w': 5.0, 'gap': 4.0, 's0': 16, 's1': 50, 'short': 0.36, 'top': 0.30, 'foot': 0.32, 'cut': 'level', 'blue': 'tip', 'rim': False, 'align': True},
    'TipBoldAlignedWide': {'w': 5.0, 'gap': 6.0, 's0': 17, 's1': 50, 'short': 0.36, 'top': 0.30, 'foot': 0.32, 'cut': 'level', 'blue': 'tip', 'rim': False, 'align': True},
    'TipClear2': {'w': 3.9, 'd': 11.5, 's0': 18, 's1': 47, 'short': 0.40, 'top': 0.36, 'foot': 0.38, 'cut': 'level', 'blue': 'tip', 'rim': False},
}


def _cut_poly(a, b, w, cutdir):
    """A stroke from a to b, w wide, its ends cut along cutdir (a unit vector) through a and b."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    ln = math.hypot(dx, dy)
    sd = (dx / ln, dy / ln)
    nx, ny = -sd[1] * w / 2, sd[0] * w / 2
    # a stroke (almost) parallel to the cut (the ">" arm along the shield's upper edge) is cut level instead, or
    # along the shield's lower edge when it is level itself
    if abs(sd[0] * cutdir[1] - sd[1] * cutdir[0]) < 0.4:
        cutdir = (1.0, 0.0) if abs(sd[1]) > 0.4 else (math.cos(math.radians(121.6)), math.sin(math.radians(121.6)))

    def onto(p, c):
        # move p along the stroke until it lies on the cut line through c
        den = sd[0] * cutdir[1] - sd[1] * cutdir[0]
        t = ((c[0] - p[0]) * cutdir[1] - (c[1] - p[1]) * cutdir[0]) / den
        return (p[0] + sd[0] * t, p[1] + sd[1] * t)
    return [onto((a[0] + nx, a[1] + ny), a), onto((b[0] + nx, b[1] + ny), b), onto((b[0] - nx, b[1] - ny), b), onto((a[0] - nx, a[1] - ny), a)]


def _cut_dir(cfg):
    if cfg['cut'] == 'edge':
        dx, dy = EDGE_SHOULDER[0] - EDGE_TOP[0], EDGE_SHOULDER[1] - EDGE_TOP[1]
        ln = math.hypot(dx, dy)
        return (dx / ln, dy / ln)
    return (1.0, 0.0)


def rim_strokes(cfg, A, B, up):
    """[(p, q, blue)]: top stroke, diagonal (B to A), foot stroke, the blue piece."""
    L = math.hypot(A[0] - B[0], A[1] - B[1])
    short = cfg['short'] * L

    def seg(c, length):
        return ((c[0] - up[0] * length / 2, c[1] - up[1] * length / 2), (c[0] + up[0] * length / 2, c[1] + up[1] * length / 2))

    def at(p, dx, along):
        return (p[0] + dx + up[0] * along, p[1] + up[1] * along)

    if cfg.get('align'):
        # the top stroke ends level with the diagonal's top, the foot starts level with its bottom (one cut line each)
        te = at(A, -cfg['top'] * L, 0)
        top = (at(te, 0, -short), te)
        fs = at(B, cfg['foot'] * L, 0)
        foot = (fs, at(fs, 0, short))
    else:
        top = seg(at(A, -cfg['top'] * L, -short * 0.32), short)
        foot = seg(at(B, cfg['foot'] * L, short * 0.32), short)
    strokes = [top + (cfg['blue'] == 'top',), (B, A, False), foot + (False,)]
    if cfg['blue'] == 'tip':
        strokes.append((at(A, 0, -0.13 * L), A, True))
    elif cfg['blue'] == 'segment':
        gap = cfg['w'] * 0.9
        strokes.append((at(A, 0, gap), at(A, 0, gap + 0.13 * L), True))
    return strokes


def draw_cut_strokes(strokes, w, cutdir, halo):
    out = []
    if halo:
        out += [layer([_cut_poly(a, b, w + halo, cutdir)], DARK) for a, b, _ in strokes]
    out += [layer([_cut_poly(a, b, w, cutdir)], BLUE if blue else WHITE) for a, b, blue in strokes]
    return out


def rim_icon_layers(cfg, small):
    ms = 0.94 if not small else 0.9
    tip, up, out = _edge_frame(ms)

    def xy(s, d):
        return (tip[0] + up[0] * s + out[0] * d, tip[1] + up[1] * s + out[1] * d)

    cutdir = _cut_dir(cfg)
    if small:
        c = dict(cfg, w=7.0, d=13.5, top=0.3, foot=0.32, short=0.38, blue='tip')
    else:
        c = dict(cfg)
    rim_half = 1.6 * ms                                         # half the shield's light rim
    if 'gap' in cfg:
        # a clear, constant gap between the shield's rim and the stroke nearest to it (the top stroke, shifted left):
        # at 16 / 24 px at least about 1 px (7.5 units of 100)
        gap = cfg['gap'] if not small else 7.5
        c['d'] = gap + rim_half + c['w'] / 2 + out[0] * c['top'] * (c['s1'] - c['s0'])
        if small:
            c['s0'], c['s1'] = 22, 52
    A, B = xy(c['s1'], c['d']), xy(c['s0'], c['d'])
    app = [mark(ms)]
    if c['rim'] and not small:
        rl = [xy(4, 4.0), xy(80, 4.0)]
        app.append(layer([_cut_poly(rl[0], rl[1], 1.3, cutdir)], WHITE, None, 0, opacity=None))
    app += draw_cut_strokes(rim_strokes(c, A, B, up), c['w'], cutdir, 1.9 if not small else 2.4)

    # engine ">_": two cut strokes around the shield's right shoulder (parallel to its upper and lower right edges),
    # the "_" a blue cut stroke along the lower edge
    if 'gap' in cfg:
        # a smaller mark, so that the ">" around the shoulder stays inside the icon
        ms = 0.8 if not small else 0.74
        tip, up, out = _edge_frame(ms)
        rim_half = 1.6 * ms

        def xy(s, d):
            return (tip[0] + up[0] * s + out[0] * d, tip[1] + up[1] * s + out[1] * d)
    top = (EDGE_TOP[0] * ms, EDGE_TOP[1] * ms)
    sh = (EDGE_SHOULDER[0] * ms, EDGE_SHOULDER[1] * ms)
    u1 = (sh[0] - top[0], sh[1] - top[1])
    l1 = math.hypot(*u1)
    u1 = (u1[0] / l1, u1[1] / l1)
    w = c['w']
    arm = 21 if not small else 18
    if 'gap' in cfg:
        # both arms at the same clear distance from the shield's upper and lower right edge; "_" one stroke further out
        D = (cfg['gap'] if not small else 7.5) + rim_half + w / 2
        n_up = (u1[1], -u1[0])
        apex = _cross(((sh[0] + n_up[0] * D, sh[1] + n_up[1] * D), u1), ((sh[0] + out[0] * D, sh[1] + out[1] * D), up))
        under_d = D + w + (2.5 if not small else 4)
        ul0, ul1 = xy(16, under_d), xy(34, under_d)
    else:
        gap = c['d'] * 0.55 + w * 0.5
        apex = (sh[0] + gap * 1.1, sh[1] + gap * 0.15)
        ul0, ul1 = xy(14, c['d'] + 2), xy(30, c['d'] + 2)
    a_up = (apex[0] - u1[0] * arm, apex[1] - u1[1] * arm)
    a_dn = (apex[0] - up[0] * arm, apex[1] - up[1] * arm)
    eng = [mark(ms)]
    pieces = [(a_up, apex, False), (apex, a_dn, False), (ul0, ul1, True)]
    eng += [layer([_cut_poly(a, b, w + 1.9, cutdir)], DARK) for a, b, _ in pieces]
    # the apex: both arms run into it, cut along the shield's right edge bisector there
    eng += [layer([_cut_poly(a, b, w, cutdir)], BLUE if blue else WHITE) for a, b, blue in pieces]
    return app, eng


def rim_icons(key_app, key_engine, cfg):
    out = []
    for small in (False, True):
        suffix = '.Small' if small else ''
        app, eng = rim_icon_layers(cfg, small)
        out.append(image(key_app + suffix, app))
        out.append(image(key_engine + suffix, eng))
    return ''.join(out)


def rim_title(key, cfg):
    """W3 (JOY white, PRO blue) followed by the Rim3Tip-style "2"."""
    placed, width = word('JOYPRO')
    layers = [layer(tf(polys, place(x, 0, 1.0, SKEW, 20)), WHITE if i < 3 else BLUE) for i, (x, polys) in enumerate(placed)]
    up = (math.cos(math.radians(-58.4)), math.sin(math.radians(-58.4)))
    L = 18 / abs(up[1])
    B0 = (0.0, 18.0)
    A0 = (B0[0] + up[0] * L, B0[1] + up[1] * L)
    strokes = rim_strokes(cfg, A0, B0, up)
    w = cfg['w'] * 0.78
    xs = [p[0] for a, b, _ in strokes for p in (a, b)]
    ys = [p[1] for a, b, _ in strokes for p in (a, b)]
    dx = width + 7 - min(xs) + w / 2
    dy = 1.0 - min(ys) + w / 2
    moved = [((a[0] + dx, a[1] + dy), (b[0] + dx, b[1] + dy), blue) for a, b, blue in strokes]
    layers += draw_cut_strokes(moved, w, _cut_dir(cfg), 0)
    total = width + 7 + (max(xs) - min(xs)) + w + 2
    return image(key, layers, total, 20 + w / 2)


def round6_resources():
    out = ''.join(rim_icons('Brand.AppIcon.' + n, 'Brand.EngineIcon.' + n, c) for n, c in RIM_STYLES.items())
    out += ''.join(rim_title('Brand.Wordmark.W3' + n, c) for n, c in RIM_STYLES.items())
    return out


# ================================================================ round 8: the engine mark (a command prompt in strokes)
# After the user's sketches: in the place of the GUI's "2" (outside the shield's lower right edge, the same clear gap)
# a prompt built only from strokes parallel to the shield's edge, in TipBoldAligned's weight and level-cut ends:
# two small ticks on top, a ">" (its upper arm a row of ticks stepping down to the right, its lower arm one long stroke
# along the edge) and, below to the right, a "_" made of a level row of short ticks. Exactly one blue element:
#   PromptTopBlue    the second top tick
#   PromptArmBlue    the long stroke of the ">"
#   PromptCursorBlue the last tick of the "_" row (a blinking cursor)
#   PromptRowBlue    the whole "_" row
ENGINE_STYLES = ['PromptTopBlue', 'PromptArmBlue', 'PromptCursorBlue', 'PromptRowBlue']
GREY = '#94A3B8'


def engine_mark_layers(style, small):
    ms = 0.86 if not small else 0.8
    tip, up, out = _edge_frame(ms)
    w = 4.6 if not small else 7.0
    rim_half = 1.6 * ms
    gap = 4.0 if not small else 7.5
    d0 = gap + rim_half + w / 2
    step = w + (2.6 if not small else 3.0)

    def pt(d, y):
        s = (y - tip[1] - out[1] * d) / up[1]
        return (tip[0] + up[0] * s + out[0] * d, y)

    def stroke(d, y_top, y_bottom, color):
        return (pt(d, y_bottom), pt(d, y_top), color)

    def blue_if(name):
        return BLUE if style == name else None

    def row_tick(x, y_bottom, height, color):
        return ((x, y_bottom), (x + up[0] * (height / -up[1]), y_bottom - height), color)

    strokes = []
    if not small:
        # two small ticks on top, stepping down to the right
        strokes.append(stroke(d0, 38, 44, GREY))
        strokes.append(stroke(d0 + step, 44, 50, blue_if('PromptTopBlue') or WHITE))
        # ">": three parallel strokes of falling length, centred on one level - a hatched arrow pointing right
        mid = 67.0
        for k, length in enumerate((27.0, 17.0, 7.0)):
            color = blue_if('PromptArmBlue') if k == 1 else None
            strokes.append(stroke(d0 + k * step, mid - length / 2, mid + length / 2, color or WHITE))
        # "_": a level row of five short ticks below, starting under the arrow
        bottom_y = 93.0
        row_x0 = pt(d0 + step, bottom_y)[0]
        for k in range(5):
            color = BLUE if (style == 'PromptRowBlue' or (style == 'PromptCursorBlue' and k == 4)) else WHITE
            strokes.append(row_tick(row_x0 + k * (w + 1.6), bottom_y, 7.0, color))
    else:
        # 16 / 24 px: the arrow as two strokes, the "_" as a heavy level row of three ticks, the blue where the style has it
        mid = 62.0
        strokes.append(stroke(d0, mid - 13, mid + 13, WHITE))
        strokes.append(stroke(d0 + step, mid - 5, mid + 5, blue_if('PromptArmBlue') or WHITE))
        x0 = pt(d0 + step, 93)[0]
        for k in range(3):
            color = BLUE if (style == 'PromptRowBlue' or (style in ('PromptCursorBlue', 'PromptTopBlue') and k == 2)) else WHITE
            strokes.append(row_tick(x0 + k * (w + 2.2), 93.0, 9.0, color))

    cut = (1.0, 0.0)
    halo = 1.9 if not small else 2.4
    layers = [mark(ms)]
    layers += [layer([_cut_poly(a, b, w + halo, cut)], DARK) for a, b, _ in strokes]
    layers += [layer([_cut_poly(a, b, w, cut)], color) for a, b, color in strokes]
    return layers


def engine_marks(key, style):
    return image(key, engine_mark_layers(style, False)) + image(key + '.Small', engine_mark_layers(style, True))


def round8_resources():
    return ''.join(engine_marks('Brand.EngineIcon.' + st, st) for st in ENGINE_STYLES)


# ================================================================ round 9: the engine mark traced from the user's sketch
# The strokes of the user's drawing (images/6.png, 865 x 679 px), measured in its pixels and mapped onto the mark: the
# sketch shows our shield at full size (dark fill top 248,21 / bottom 248,469 / left 32,117 / right 465,117), so
#   X = 50 + (x - 248.5) / 4.95,   Y = 5.75 + (y - 21) / 4.92     (the mark's 100 grid, the mark drawn at scale 1).
# Every stroke as drawn: count, positions, lengths and angles; only cleaned up: straight lines, level-cut ends, one
# weight (his pen, 1.9 units: TipBoldAligned's 5 would merge his closely set strokes), the two long strokes each on
# one straight line (light head + body, as he drew them), the bottom row's five ticks at one common angle and evenly
# spaced (their lengths as drawn). Variants differ only in the blue:
#   TraceTopBlue     his blue tick at the top (as drawn)
#   TraceHookBlue    the hooked stroke
#   TraceRowTickBlue the middle tick of the bottom row
#   TraceLastBlue    the last tick of the bottom row
#   TraceSketch      his colours: light grey #808285, dark grey #58595B, blue #00AACC
TRACE_STYLES = ['TraceTopBlue', 'TraceHookBlue', 'TraceRowTickBlue', 'TraceLastBlue', 'TraceSketch']
SKETCH_LIGHT, SKETCH_DARK, SKETCH_BLUE = '#808285', '#58595B', '#00AACC'


def _sketch(p):
    return (50 + (p[0] - 248.5) / 4.95, 5.75 + (p[1] - 21) / 4.92)


def _on_line(a, b, t):
    return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)


def trace_strokes():
    """[(name, [points], role)] in mark units; role: tick, blue, head, body, hook, row0..row4."""
    out = []
    out.append(('tick', [_sketch((332, 357)), _sketch((322.5, 379))]))
    out.append(('blue', [_sketch((348.5, 372)), _sketch((333.5, 390))]))
    # long stroke 1: light head and dark body on one straight line, split where he lifted the pen
    a, b = _sketch((363, 384)), _sketch((306, 456))
    split = 0.22
    out.append(('head1', [a, _on_line(a, b, split - 0.025)]))
    out.append(('body1', [_on_line(a, b, split + 0.025), b]))
    # long stroke 2: head, body, and the hook to the lower left
    a, b = _sketch((379.5, 397.5)), _sketch((341.25, 445))
    split = 0.3
    out.append(('head2', [a, _on_line(a, b, split - 0.035)]))
    out.append(('hook', [_on_line(a, b, split + 0.035), b, _sketch((316.25, 466.25))]))
    # bottom row: five ticks, centres evenly spaced between the first and the last, one common angle (67.5 deg)
    ticks = [((404, 456.5), (400, 466)), ((414, 455), (409.5, 465)), ((430.5, 454.5), (424, 472.5)),
             ((445, 459.5), (441, 469.5)), ((459, 462.5), (454, 474))]
    centres = [((t[0] + b[0]) / 2, (t[1] + b[1]) / 2) for t, b in ticks]
    lengths = [math.hypot(t[0] - b[0], t[1] - b[1]) for t, b in ticks]
    ang = math.radians(67.5)
    d = (-math.cos(ang), math.sin(ang))                     # down the tick
    for i, ((cx, cy), ln) in enumerate(zip(centres, lengths)):
        cx = centres[0][0] + (centres[-1][0] - centres[0][0]) * i / 4
        cy = centres[0][1] + (centres[-1][1] - centres[0][1]) * i / 4
        top = (cx - d[0] * ln / 2, cy - d[1] * ln / 2)
        bottom = (cx + d[0] * ln / 2, cy + d[1] * ln / 2)
        out.append(('row%d' % i, [_sketch(top), _sketch(bottom)]))
    return out


def _trace_colour(style, name):
    if style == 'TraceSketch':
        return {'tick': SKETCH_DARK, 'blue': SKETCH_BLUE, 'head1': SKETCH_LIGHT, 'body1': SKETCH_DARK}.get(name, SKETCH_LIGHT)
    blue = {'TraceTopBlue': 'blue', 'TraceHookBlue': 'hook', 'TraceRowTickBlue': 'row2', 'TraceLastBlue': 'row4'}[style]
    if name == blue:
        return BLUE
    if name in ('tick',):
        return GREY
    if name in ('head1', 'head2'):
        return TONE_B
    return WHITE


def trace_layers(style, small):
    w = 1.9 if not small else 2.6
    cut = (1.0, 0.0)
    layers = [mark(1.0)]
    strokes = trace_strokes()
    for halo_pass in (True, False):
        for name, pts in strokes:
            colour = DARK if halo_pass else _trace_colour(style, name)
            ww = w + (1.2 if halo_pass else 0)
            if len(pts) == 2:
                layers.append(layer([_cut_poly(pts[1], pts[0], ww, cut)], colour))
            else:
                d = 'M' + ' L'.join(fmt(p[0]) + ',' + fmt(p[1]) for p in pts)
                layers.append('<GeometryDrawing Geometry="' + d + '"><GeometryDrawing.Pen><Pen Brush="' + colour + '" Thickness="' + fmt(ww) +
                              '" LineCap="Flat" LineJoin="Miter" /></GeometryDrawing.Pen></GeometryDrawing>')
    return layers


def trace_marks(key, style):
    return image(key, trace_layers(style, False)) + image(key + '.Small', trace_layers(style, True))


def round9_resources():
    return ''.join(trace_marks('Brand.EngineIcon.' + st, st) for st in TRACE_STYLES)


# ================================================================ round 10: the engine ">_" from the user's drawing on our icon
# The user drew the strokes on our icon (images/9.png, a GIMP screenshot): the canvas (yellow border) is x 186-915,
# y 69-798, i.e. 7.29 px per unit of the 100 grid, the mark at scale 1. Measured (centre, angle, length; edge frame:
# s = up the shield's lower right edge from its bottom point, d = out from it):
#   ">": two short dark ticks (s 22, d 7.95 / 10.94, length 6.9), a long blue bar (d 13.75, 15.9) and a long dark bar
#        (d 17.05, 12.8), all tops on one line across the bars (s about 25.5), all at 53-57 degrees;
#   "_": three short ticks in a nearly level row (d 23.3 / 26.5 / 29.8, length 5.1), dark, dark, blue.
# Clean-up: every bar exactly at the edge's angle (58.4), one width, equal gaps, square ends, the ">" tops on one
# line across the bars and the "_" centres on one level line, a clear gap to the shield's rim (4 units, as the "2").
#   Trace10Exact    his layout, width 3.6 (his pen 2.65 is too thin at 32 px), his blue (long bar + last tick)
#   Trace10Large    the same, width 4.6, the group scaled up accordingly
#   Trace10Ticks3   Large with three short ticks in the ">"
#   Trace10BarBlue  Large with the blue only on the long bar
TRACE10_STYLES = {
    'Trace10Exact': {'w': 3.6, 'gap': 0.6, 'ticks': 2, 'blue_last': True},
    'Trace10Large': {'w': 4.6, 'gap': 0.9, 'ticks': 2, 'blue_last': True},
    'Trace10Ticks3': {'w': 4.6, 'gap': 0.9, 'ticks': 3, 'blue_last': True},
    'Trace10BarBlue': {'w': 4.6, 'gap': 0.9, 'ticks': 2, 'blue_last': False},
}
DARK_BAR = '#2B313A'          # the dark of his strokes (our shield's dark)


def trace10_strokes(cfg, small=False):
    """[(p_bottom, p_top, colour)] in the 100 grid (mark at scale 1)."""
    w = cfg['w'] * (1.45 if small else 1.0)
    pitch = w + cfg['gap'] * (1.3 if small else 1.0)
    f = pitch / 3.25                                         # his pitch was 3.25 units
    tip, up, out = _edge_frame(1.0)
    d0 = 4.0 + 1.6 + w / 2                                   # clear gap to the rim, as the "2"

    def xy(s, d):
        return (tip[0] + up[0] * s + out[0] * d, tip[1] + up[1] * s + out[1] * d)

    s_top = 25.5
    strokes = []
    k = 0
    for i in range(cfg['ticks']):
        strokes.append((k, s_top - 6.9 * f, s_top, DARK_BAR))
        k += 1
    strokes.append((k, s_top - 15.9 * f, s_top, BLUE))
    strokes.append((k + 1, s_top - 12.8 * f, s_top, DARK_BAR))
    # the "_": three ticks in a level row, starting 1.7 pitches after the last bar, centred where he drew them
    row_k0 = k + 1 + 1.7
    centre_s0 = s_top - (25.5 - 17.6) * f
    for i in range(3):
        kk = row_k0 + i
        # level row: stepping one pitch out lowers a point by out.y * pitch; move up the edge to stay level
        cs = centre_s0 + (out[1] * pitch * i) / (-up[1])
        colour = BLUE if (i == 2 and cfg['blue_last']) else DARK_BAR
        strokes.append((kk, cs - 5.1 * f / 2, cs + 5.1 * f / 2, colour))
    pts = [(xy(s0, d0 + kk * pitch), xy(s1, d0 + kk * pitch), c) for kk, s0, s1, c in strokes]
    return pts, w


def trace10_fit(pts, w):
    """The factor that shrinks the whole icon (mark and strokes together) so that the strokes stay inside it."""
    xs = [3.4, 96.6] + [q[0] + sgn * w * 0.6 for a, b, _ in pts for q in (a, b) for sgn in (-1, 1)]
    ys = [2.4, 98.6] + [q[1] + sgn * w * 0.6 for a, b, _ in pts for q in (a, b) for sgn in (-1, 1)]
    return min(1.0, 98.0 / (max(xs) - min(xs)), 98.0 / (max(ys) - min(ys)))


def trace10_layers(cfg, small):
    pts, w = trace10_strokes(cfg, small)
    # the whole icon (mark and strokes) shrinks together when the strokes would leave the canvas
    c = trace10_fit(pts, w)
    # then the whole picture (shield 3.4..96.6 x 2.4..98.6 and the strokes) is centred on the canvas again
    xs = [3.4, 96.6] + [q[0] + sgn * w * 0.6 for a, b, _ in pts for q in (a, b) for sgn in (-1, 1)]
    ys = [2.4, 98.6] + [q[1] + sgn * w * 0.6 for a, b, _ in pts for q in (a, b) for sgn in (-1, 1)]
    tx = 50 - c * (min(xs) + max(xs)) / 2
    ty = 50 - c * (min(ys) + max(ys)) / 2
    pts = [((a[0] * c + tx, a[1] * c + ty), (b[0] * c + tx, b[1] * c + ty), colour) for a, b, colour in pts]
    w *= c
    rim = (1.0 if not small else 1.3) * c
    # his bars are dark like the shield; like the shield they get a thin light rim, so they read on dark backgrounds
    layers = [mark(c, tx, ty)]
    for a, b, colour in pts:
        dx, dy = b[0] - a[0], b[1] - a[1]
        ln = math.hypot(dx, dy)
        square = (-dy / ln, dx / ln)
        if colour == BLUE:
            layers.append(layer([_cut_poly(a, b, w, square)], BLUE))
        else:
            layers.append(layer([_cut_poly(a, b, w - rim, square)], colour, '#F1F5F9', rim))
    return layers


def trace10_marks(key, name):
    cfg = TRACE10_STYLES[name]
    return image(key, trace10_layers(cfg, False)) + image(key + '.Small', trace10_layers(cfg, True))


def round10_resources():
    return ''.join(trace10_marks('Brand.EngineIcon.' + n, n) for n in TRACE10_STYLES)


# ================================================================ round 11: round 10 with the user's corrections
# Kept from round 10: every bar at the edge's angle, one width, equal gaps, the clear gap to the rim, the blue placement.
# Changed:
#   ">": only two short ticks; the second long bar is as long as the blue one, so both long bars share their bottom
#        line as well as their top line;
#   bars: solid, no light rim; one slate grey (BAR11) with the same contrast on dark and light backgrounds
#        (about 4.2:1 on #111827 and 3.9:1 on #F1F5F9; white / near-black would be 17:1 on one and 1.1:1 on the other);
#   "_": every tick is cut level at the top (at the height of its top right corner) and at the bottom (at the height of
#        its bottom left corner): slanted parallelograms with level ends, all on one level band, no zigzag.
#   Trace11Exact    width 3.6, blue on the long bar and the last "_" tick
#   Trace11Large    width 4.6, the group scaled up, blue on the long bar and the last "_" tick
#   Trace11BarBlue  Large, blue only on the long bar
TRACE11_STYLES = {
    'Trace11Exact': {'w': 3.6, 'gap': 0.6, 'blue_last': True},
    'Trace11Large': {'w': 4.6, 'gap': 0.9, 'blue_last': True},
    'Trace11BarBlue': {'w': 4.6, 'gap': 0.9, 'blue_last': False},
}
BAR11 = '#707C8E'


def trace11_strokes(cfg, small=False):
    """[(polygon, colour)] in the 100 grid (mark at scale 1)."""
    w = cfg['w'] * (1.45 if small else 1.0)
    pitch = w + cfg['gap'] * (1.3 if small else 1.0)
    f = pitch / 3.25
    tip, up, out = _edge_frame(1.0)
    d0 = 4.0 + 1.6 + w / 2

    def xy(s, d):
        return (tip[0] + up[0] * s + out[0] * d, tip[1] + up[1] * s + out[1] * d)

    square = (out[0], out[1])
    s_top = 25.5
    polys = []
    # ">": two short ticks, then the blue long bar and the grey long bar of the same length (common top and bottom)
    for k, length, colour in ((0, 6.9, BAR11), (1, 6.9, BAR11), (2, 15.9, BLUE), (3, 15.9, BAR11)):
        d = d0 + k * pitch
        polys.append((_cut_poly(xy(s_top - length * f, d), xy(s_top, d), w, square), colour))
    # "_": three ticks centred on one level line, cut level at the top right / bottom left corner heights
    row_k0 = 3 + 1.7
    centre_s0 = s_top - (25.5 - 17.6) * f
    half = 5.1 * f / 2
    y_top = y_bottom = None
    for i in range(3):
        d = d0 + (row_k0 + i) * pitch
        cs = centre_s0 + (out[1] * pitch * i) / (-up[1])
        c = xy(cs, d)
        if y_top is None:
            # the top end's right corner is the lower one of its two corners, the bottom end's left corner the higher
            y_top = c[1] + up[1] * half + out[1] * w / 2
            y_bottom = c[1] - up[1] * half - out[1] * w / 2
        # the points on the centre line at those heights; the cut through them is level
        a = (c[0] + up[0] * (y_bottom - c[1]) / up[1], y_bottom)
        b = (c[0] + up[0] * (y_top - c[1]) / up[1], y_top)
        colour = BLUE if (i == 2 and cfg['blue_last']) else BAR11
        polys.append((_cut_poly(a, b, w, (1.0, 0.0)), colour))
    return polys


def trace11_layers(cfg, small):
    polys = trace11_strokes(cfg, small)
    xs = [3.4, 96.6] + [p[0] for poly, _ in polys for p in poly]
    ys = [2.4, 98.6] + [p[1] for poly, _ in polys for p in poly]
    c = min(1.0, 98.0 / (max(xs) - min(xs)), 98.0 / (max(ys) - min(ys)))
    tx = 50 - c * (min(xs) + max(xs)) / 2
    ty = 50 - c * (min(ys) + max(ys)) / 2
    layers = [mark(c, tx, ty)]
    for poly, colour in polys:
        layers.append(layer([[(p[0] * c + tx, p[1] * c + ty) for p in poly]], colour))
    return layers


def trace11_marks(key, name):
    cfg = TRACE11_STYLES[name]
    return image(key, trace11_layers(cfg, False)) + image(key + '.Small', trace11_layers(cfg, True))


def round11_resources():
    return ''.join(trace11_marks('Brand.EngineIcon.' + n, n) for n in TRACE11_STYLES)


# ================================================================ round 12: Trace11Large, rebalanced
# The user picked Trace11Large. Kept: width 4.6, gap 0.9, the solid grey BAR11, blue on the long bar and the last "_"
# tick, the edge's angle, the level-cut "_" ticks, the rim gap and the "_" row's gap to the long bars (it starts 1.7
# pitches after the grey long bar). Changed:
#   ">": the two short ticks are half as long as the long bars (tops still on the long bars' top line);
#   "_": the band moves down along the bars until its bottom edge is level with the lowest point of the ">" (the grey
#        long bar's lower right corner), or 'lift' units above it; 3, 4 or 5 ticks.
#   Trace12T3/T4/T5        the band's bottom on the ">"'s bottom line, 3 / 4 / 5 ticks
#   Trace12T3Up/T4Up/T5Up  the same, the band 2 units higher (about 40 % of its height)
TRACE12_STYLES = {
    'Trace12T3': {'ticks': 3, 'lift': 0.0},
    'Trace12T4': {'ticks': 4, 'lift': 0.0},
    'Trace12T5': {'ticks': 5, 'lift': 0.0},
    'Trace12T3Up': {'ticks': 3, 'lift': 2.0},
    'Trace12T4Up': {'ticks': 4, 'lift': 2.0},
    'Trace12T5Up': {'ticks': 5, 'lift': 2.0},
}


def trace12_strokes(cfg, small=False):
    """[(polygon, colour)] in the 100 grid (mark at scale 1)."""
    w = 4.6 * (1.45 if small else 1.0)
    pitch = w + 0.9 * (1.3 if small else 1.0)
    f = pitch / 3.25
    tip, up, out = _edge_frame(1.0)
    d0 = 4.0 + 1.6 + w / 2

    def xy(s, d):
        return (tip[0] + up[0] * s + out[0] * d, tip[1] + up[1] * s + out[1] * d)

    def s_at(y, d):
        # where the centre line at distance d crosses the height y
        return (y - tip[1] - out[1] * d) / up[1]

    s_top = 25.5
    long_len = 15.9 * f
    polys = []
    for k, length, colour in ((0, long_len / 2, BAR11), (1, long_len / 2, BAR11), (2, long_len, BLUE), (3, long_len, BAR11)):
        d = d0 + k * pitch
        polys.append((_cut_poly(xy(s_top - length, d), xy(s_top, d), w, out), colour))
    # the lowest point of the ">": the grey long bar's bottom end, its outer corner
    bottom = xy(s_top - long_len, d0 + 3 * pitch)
    y_bottom = bottom[1] + out[1] * w / 2 - cfg['lift']
    # the band's height as in round 11: a tick of length 5.1 cut level at its top right and bottom left corners
    y_top = y_bottom - (-up[1] * 5.1 * f - out[1] * w)
    for i in range(cfg['ticks']):
        d = d0 + (3 + 1.7 + i) * pitch
        colour = BLUE if i == cfg['ticks'] - 1 else BAR11
        polys.append((_cut_poly(xy(s_at(y_bottom, d), d), xy(s_at(y_top, d), d), w, (1.0, 0.0)), colour))
    return polys


def trace12_layers(cfg, small):
    polys = trace12_strokes(cfg, small)
    xs = [3.4, 96.6] + [p[0] for poly, _ in polys for p in poly]
    ys = [2.4, 98.6] + [p[1] for poly, _ in polys for p in poly]
    c = min(1.0, 98.0 / (max(xs) - min(xs)), 98.0 / (max(ys) - min(ys)))
    tx = 50 - c * (min(xs) + max(xs)) / 2
    ty = 50 - c * (min(ys) + max(ys)) / 2
    layers = [mark(c, tx, ty)]
    for poly, colour in polys:
        layers.append(layer([[(p[0] * c + tx, p[1] * c + ty) for p in poly]], colour))
    return layers


def trace12_marks(key, name):
    cfg = TRACE12_STYLES[name]
    return image(key, trace12_layers(cfg, False)) + image(key + '.Small', trace12_layers(cfg, True))


def round12_resources():
    return ''.join(trace12_marks('Brand.EngineIcon.' + n, n) for n in TRACE12_STYLES)
