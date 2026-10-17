import math, os, sys

root = sys.argv[1]
out = os.path.join(root, 'gui', 'JoyPro.Gui', 'Styles', 'Branding.axaml')

def fmt(v):
    s = ('%.2f' % v).rstrip('0').rstrip('.')
    return '0' if s == '-0' else s

def path(polys, tx=lambda p: p):
    parts = []
    for poly in polys:
        pts = [tx(p) for p in poly]
        parts.append('M' + fmt(pts[0][0]) + ',' + fmt(pts[0][1]) + ' ' + ' '.join('L' + fmt(x) + ',' + fmt(y) for x, y in pts[1:]) + ' Z')
    return ' '.join(parts)

def place(dx, dy, s, skew=0.0, height=None):
    # scale, then skew (x shifts right the higher the point: italic), then move
    def t(p):
        x, y = p[0] * s, p[1] * s
        if skew:
            x += (height - y) * math.tan(math.radians(skew))
        return (x + dx, y + dy)
    return t

# The faceted "2" (10 x 14 box): chamfered top, diagonal, flat foot; stroke weight about 3.
TWO = [[(0, 4.6), (3.2, 0), (7.8, 0), (10, 2.6), (10, 6), (4.6, 11), (10, 11), (10, 14), (0, 14), (0, 11.2), (6.6, 5.2), (6.6, 3.6), (5.8, 3), (4.2, 3), (3.1, 4.6)]]
TWO_TIP = [[(7.2, 14), (10, 11.6), (10, 14)]]          # the blue canopy-like tip of the foot
# ">_" (14 x 10 box): an angular chevron and the cursor bar (blue).
CHEVRON = [[(0, 0), (3, 0), (8, 5), (3, 10), (0, 10), (5, 5)]]
CURSOR = [[(9, 8), (14, 8), (14, 10), (9, 10)]]

# Letters, 20 units high, stroke weight 4, chamfers at 45 degrees like the delta's cuts.
L = {
    'J': ([[(10, 0), (14, 0), (14, 16), (10, 20), (3, 20), (0, 17), (0, 12), (4, 12), (4, 16), (10, 16)]], 14),
    'O': ([[(3, 0), (11, 0), (14, 3), (14, 17), (11, 20), (3, 20), (0, 17), (0, 3)], [(4, 4), (4, 16), (10, 16), (10, 4)]], 14),
    'Y': ([[(0, 0), (4.4, 0), (7, 5.4), (9.6, 0), (14, 0), (9, 10), (9, 20), (5, 20), (5, 10)]], 14),
    'P': ([[(0, 0), (11, 0), (14, 3), (14, 10), (11, 13), (4, 13), (4, 20), (0, 20)], [(4, 4), (4, 9), (10, 9), (10, 4)]], 14),
    'R': ([[(0, 0), (11, 0), (14, 3), (14, 10), (11.6, 12.4), (14.4, 20), (10.2, 20), (7.6, 13), (4, 13), (4, 20), (0, 20)], [(4, 4), (4, 9), (10, 9), (10, 4)]], 14.4),
}
CANOPY_IN_O = [[(7, 6.5), (8.2, 10), (7, 13.5), (5.8, 10)]]

def word(text, x0, skew, gap=3.2):
    polys_by_letter = []
    x = x0
    for ch in text:
        polys, w = L[ch]
        polys_by_letter.append((ch, x, polys))
        x += w + gap
    return polys_by_letter, x - gap

def word_path(letters, skew, start=0, end=None):
    sel = letters[start:end]
    parts = []
    for ch, x, polys in sel:
        parts.append(path(polys, place(x, 0, 1.0, skew, 20)))
    return ' '.join(parts)

# ---- icon compositions (100 x 100) ----
MARK_SCALE = 0.86
# Variant A: solid white "2" with a dark outline in the empty lower right corner, blue tip.
A_TWO = path(TWO, place(65, 55, 3.0))
A_TIP = path(TWO_TIP, place(65, 55, 3.0))
A_PROMPT = path(CHEVRON, place(63, 70, 2.45))
A_CURSOR = path(CURSOR, place(63, 70, 2.45))
# Small sizes (16 and 24 px icon frames): a smaller mark and a bigger numeral / prompt, so they stay readable.
SMALL_MARK_SCALE = 0.74
S_TWO = path(TWO, place(57, 42, 4.0))
S_TIP = path(TWO_TIP, place(57, 42, 4.0))
S_PROMPT = path(CHEVRON, place(54, 62, 3.25))
S_CURSOR = path(CURSOR, place(54, 62, 3.25))
# Variant C: a small faceted plate (shield shape: flat top, pointed foot) with rim, numeral inside.
PLATE = path([[(61, 52), (99, 52), (99, 84), (80, 99), (61, 84)]])
C_TWO = path(TWO, place(70.5, 56.5, 2.3))
C_TIP = path(TWO_TIP, place(70.5, 56.5, 2.3))
C_PROMPT = path(CHEVRON, place(64.5, 63, 2.15))
C_CURSOR = path(CURSOR, place(64.5, 63, 2.15))

# ---- wordmarks ----
letters_up, w_up = word('JOYPRO', 0, 0)
letters_it, w_it = word('JOYPRO', 0, 12)
SKEW = 12
two_x_up = w_up + 7
two_x_it = w_it + 10
W_TWO_UP = path(TWO, place(two_x_up, 0, 20 / 14))
W_TWO_UP_TIP = path(TWO_TIP, place(two_x_up, 0, 20 / 14))
W_TWO_IT = path(TWO, place(two_x_it, 0, 20 / 14, SKEW, 14))
W_TWO_IT_TIP = path(TWO_TIP, place(two_x_it, 0, 20 / 14, SKEW, 14))
o2_x = letters_it[5][1]
W_CANOPY = path(CANOPY_IN_O, place(o2_x, 0, 1.0, SKEW, 20))
width_up = two_x_up + 10 * 20 / 14 + 1
width_it = two_x_it + 10 * 20 / 14 + 20 * math.tan(math.radians(SKEW)) + 1

def frame(w, h):
    return 'M0,0 H' + fmt(w) + ' V' + fmt(h) + ' H0 Z'

mark_group = '''<DrawingGroup x:Key="Brand.Drawing.Mark">
        <GeometryDrawing Geometry="{StaticResource Brand.ShieldGeometry}" Brush="{StaticResource Brand.ShieldBrush}">
            <GeometryDrawing.Pen>
                <Pen Brush="#F1F5F9" Thickness="3.2" LineJoin="Round" />
            </GeometryDrawing.Pen>
        </GeometryDrawing>
        <GeometryDrawing Geometry="{StaticResource Brand.ShieldInnerGeometry}">
            <GeometryDrawing.Pen>
                <Pen Brush="#5538BDF8" Thickness="1" LineJoin="Round" />
            </GeometryDrawing.Pen>
        </GeometryDrawing>
        <GeometryDrawing Geometry="{StaticResource Brand.WingGeometry}" Brush="#F8FAFC" />
        <GeometryDrawing Geometry="{StaticResource Brand.CanardsGeometry}" Brush="#F8FAFC" />
        <GeometryDrawing Geometry="{StaticResource Brand.FuselageGeometry}" Brush="#F8FAFC">
            <GeometryDrawing.Pen>
                <Pen Brush="#1B1E24" Thickness="1.3" LineJoin="Round" />
            </GeometryDrawing.Pen>
        </GeometryDrawing>
        <GeometryDrawing Geometry="{StaticResource Brand.CanopyGeometry}" Brush="#38BDF8" />
    </DrawingGroup>'''

def icon(key, glyph, accent, variant, mark_scale=None):
    mark_scale = MARK_SCALE if mark_scale is None else mark_scale
    # mark scaled into the upper left, the numeral / prompt in the lower right corner
    if variant == 'A':
        badge = '''
                <GeometryDrawing Geometry="''' + glyph + '''" Brush="#F8FAFC">
                    <GeometryDrawing.Pen>
                        <Pen Brush="#0B1220" Thickness="3.2" LineJoin="Miter" />
                    </GeometryDrawing.Pen>
                </GeometryDrawing>
                <GeometryDrawing Geometry="''' + glyph + '''" Brush="#F8FAFC" />
                <GeometryDrawing Geometry="''' + accent + '''" Brush="#38BDF8" />'''
    elif variant == 'B':
        badge = '''
                <GeometryDrawing Geometry="''' + glyph + '''" Brush="#0B1220">
                    <GeometryDrawing.Pen>
                        <Pen Brush="#0B1220" Thickness="4" LineJoin="Miter" />
                    </GeometryDrawing.Pen>
                </GeometryDrawing>
                <GeometryDrawing Geometry="''' + glyph + '''">
                    <GeometryDrawing.Pen>
                        <Pen Brush="#F8FAFC" Thickness="1.6" LineJoin="Miter" />
                    </GeometryDrawing.Pen>
                </GeometryDrawing>
                <GeometryDrawing Geometry="''' + accent + '''" Brush="#38BDF8" />'''
    else:
        badge = '''
                <GeometryDrawing Geometry="''' + PLATE + '''" Brush="{StaticResource Brand.ShieldBrush}">
                    <GeometryDrawing.Pen>
                        <Pen Brush="#F1F5F9" Thickness="2.6" LineJoin="Miter" />
                    </GeometryDrawing.Pen>
                </GeometryDrawing>
                <GeometryDrawing Geometry="''' + glyph + '''" Brush="#F8FAFC" />
                <GeometryDrawing Geometry="''' + accent + '''" Brush="#38BDF8" />'''
    return '''    <DrawingImage x:Key="''' + key + '''">
        <DrawingImage.Drawing>
            <DrawingGroup>
                <GeometryDrawing Geometry="{StaticResource Brand.FrameGeometry}" Brush="Transparent" />
                <DrawingGroup>
                    <DrawingGroup.Transform>
                        <ScaleTransform ScaleX="''' + fmt(mark_scale) + '''" ScaleY="''' + fmt(mark_scale) + '''" />
                    </DrawingGroup.Transform>
                    <GeometryDrawing Geometry="{StaticResource Brand.ShieldGeometry}" Brush="{StaticResource Brand.ShieldBrush}">
                        <GeometryDrawing.Pen>
                            <Pen Brush="#F1F5F9" Thickness="3.2" LineJoin="Round" />
                        </GeometryDrawing.Pen>
                    </GeometryDrawing>
                    <GeometryDrawing Geometry="{StaticResource Brand.WingGeometry}" Brush="#F8FAFC" />
                    <GeometryDrawing Geometry="{StaticResource Brand.CanardsGeometry}" Brush="#F8FAFC" />
                    <GeometryDrawing Geometry="{StaticResource Brand.FuselageGeometry}" Brush="#F8FAFC">
                        <GeometryDrawing.Pen>
                            <Pen Brush="#1B1E24" Thickness="1.3" LineJoin="Round" />
                        </GeometryDrawing.Pen>
                    </GeometryDrawing>
                    <GeometryDrawing Geometry="{StaticResource Brand.CanopyGeometry}" Brush="#38BDF8" />
                </DrawingGroup>''' + badge + '''
            </DrawingGroup>
        </DrawingImage.Drawing>
    </DrawingImage>
'''

def wordmark(key, variant):
    if variant == 'W1':
        w = width_up
        body = '''
                <GeometryDrawing Geometry="''' + word_path(letters_up, 0) + '''" Brush="#F8FAFC" />
                <GeometryDrawing Geometry="''' + W_TWO_UP + '''" Brush="#38BDF8" />'''
    elif variant == 'W2':
        w = width_it
        body = '''
                <GeometryDrawing Geometry="''' + word_path(letters_it, SKEW) + '''" Brush="#F8FAFC" />
                <GeometryDrawing Geometry="''' + W_TWO_IT + '''" Brush="#F8FAFC" />
                <GeometryDrawing Geometry="''' + W_TWO_IT_TIP + '''" Brush="#38BDF8" />'''
    else:
        w = width_it
        body = '''
                <GeometryDrawing Geometry="''' + word_path(letters_it, SKEW, 0, 3) + '''" Brush="#F8FAFC" />
                <GeometryDrawing Geometry="''' + word_path(letters_it, SKEW, 3) + '''" Brush="#38BDF8" />
                <GeometryDrawing Geometry="''' + W_TWO_IT + '''" Brush="#F8FAFC" />'''
    return '''    <DrawingImage x:Key="''' + key + '''">
        <DrawingImage.Drawing>
            <DrawingGroup>
                <GeometryDrawing Geometry="''' + frame(w, 20) + '''" Brush="Transparent" />''' + body + '''
            </DrawingGroup>
        </DrawingImage.Drawing>
    </DrawingImage>
'''

DEFAULT_ICON = sys.argv[2] if len(sys.argv) > 2 else 'TipBoldAligned'
DEFAULT_WORD = sys.argv[3] if len(sys.argv) > 3 else 'W3TipBoldAligned'
# The engine icon's mark (round 8); empty = the one that comes with the -Icon variant.
DEFAULT_ENGINE = sys.argv[4] if len(sys.argv) > 4 else 'PromptCursorBlue'

glyphs = {'A': (A_TWO, A_TIP, A_PROMPT, A_CURSOR), 'B': (A_TWO, A_TIP, A_PROMPT, A_CURSOR), 'C': (C_TWO, C_TIP, C_PROMPT, C_CURSOR)}

text = '''<!--
    GENERATED by a script from the shapes below (see docs/branding/README.md); edit the shapes there, not by hand.
    JoyPro's mark: the delta canard jet of the original JoyPro logo (top view, nose down) on a dark kite shield,
    redrawn as clean vector shapes on a 100 x 100 grid. The shield is dark with a light rim, so the mark reads on the
    light and the dark theme alike, and the filled silhouette stays readable down to 16 px.
      Brand.Logo              the mark as an image source:  <Image Source="{StaticResource Brand.Logo}" />
      Brand.AppIcon           the mark with the faceted "2" (GUI and tray icon)
      Brand.EngineIcon        the mark with the faceted ">_" (engine icon)
      Brand.Wordmark          the title "JOYPRO 2" drawn as paths (no font), 20 units high
      Brand.*.A/B/C, .W1/2/3  the variants of docs/branding/variants.png (default: ''' + DEFAULT_ICON + ''' and ''' + DEFAULT_WORD + ''')
      Brand.BannerBrush       the banner background of the main window
-->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <StreamGeometry x:Key="Brand.FrameGeometry">M0,0 H100 V100 H0 Z</StreamGeometry>
    <StreamGeometry x:Key="Brand.ShieldGeometry">M50,4 L95,24 L50,97 L5,24 Z</StreamGeometry>
    <StreamGeometry x:Key="Brand.ShieldInnerGeometry">M50,10 L87.5,26.6 L50,88 L12.5,26.6 Z</StreamGeometry>
    <StreamGeometry x:Key="Brand.WingGeometry">M21,26 L79,26 L50,62 Z</StreamGeometry>
    <StreamGeometry x:Key="Brand.FuselageGeometry">M50,9 L52.2,22 L54,40 L54.6,58 L53,72 L50,87 L47,72 L45.4,58 L46,40 L47.8,22 Z</StreamGeometry>
    <StreamGeometry x:Key="Brand.CanardsGeometry">M36,59 L48,66.5 L48.4,73 Z M64,59 L52,66.5 L51.6,73 Z</StreamGeometry>
    <StreamGeometry x:Key="Brand.CanopyGeometry">M50,65 L51.5,71 L50,77.5 L48.5,71 Z</StreamGeometry>
    <!-- The faceted numeral "2" on a 10 x 14 grid (chamfered like the shield, foot tip in the canopy blue). -->
    <StreamGeometry x:Key="Brand.TwoGeometry">''' + path(TWO) + '''</StreamGeometry>
    <StreamGeometry x:Key="Brand.TwoTipGeometry">''' + path(TWO_TIP) + '''</StreamGeometry>

    <LinearGradientBrush x:Key="Brand.ShieldBrush" StartPoint="0%,0%" EndPoint="100%,100%">
        <GradientStop Color="#3B414B" Offset="0" />
        <GradientStop Color="#1B1E24" Offset="1" />
    </LinearGradientBrush>

    <DrawingImage x:Key="Brand.Logo">
        <DrawingImage.Drawing>
            <DrawingGroup>
                <!-- The 100 x 100 frame keeps the image square. -->
                <GeometryDrawing Geometry="{StaticResource Brand.FrameGeometry}" Brush="Transparent" />
''' + '\n'.join('                ' + l.strip() if False else l for l in mark_group.split('\n')[1:-1]).replace('        <', '                <', ).replace('            <', '                    <') + '''
            </DrawingGroup>
        </DrawingImage.Drawing>
    </DrawingImage>

'''
# simpler: write the mark drawings explicitly (the replace above may mangle indentation, but stays valid XAML)
for v in ['A', 'B', 'C']:
    t, tip, pr, cur = glyphs[v]
    text += icon('Brand.AppIcon.' + v, t, tip, v)
    text += icon('Brand.EngineIcon.' + v, pr, cur, v)
for v in ['W1', 'W2', 'W3']:
    text += wordmark('Brand.Wordmark.' + v, v)
# Round 2 concepts (branding/branding_concepts.py): LineArt, Formation, Stencil, Hud, Metal, Integrated.
import importlib.util
_spec = importlib.util.spec_from_file_location('branding_concepts', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'branding_concepts.py'))
concepts = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(concepts)
text += concepts.brushes()
# The concepts themselves go into Styles/BrandingConcepts.axaml, which only the previews load (not the app).
concept_text = ('<!-- GENERATED by branding/generate-branding.py (branding/branding_concepts.py): the round 2 concepts of\n'
                '     docs/branding/variants-round2.png. Not loaded by the app; BrandingRenderTests merges it for the previews. -->\n'
                '<ResourceDictionary xmlns="https://github.com/avaloniaui"\n'
                '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n'
                '    <ResourceDictionary.MergedDictionaries>\n'
                '        <ResourceInclude Source="avares://JoyPro.Gui/Styles/Branding.axaml" />\n'
                '    </ResourceDictionary.MergedDictionaries>\n' +
                concepts.icon_resources() + concepts.title_resources() + concepts.round3_resources() + concepts.round4_resources() + concepts.round5_resources() + concepts.round6_resources() + concepts.round8_resources() + concepts.round9_resources() + concepts.round10_resources() + concepts.round11_resources() + concepts.round12_resources() + '</ResourceDictionary>\n')
open(os.path.join(os.path.dirname(out), 'BrandingConcepts.axaml'), 'w', encoding='utf-8', newline='\r\n').write(concept_text)

if DEFAULT_ICON in concepts.RIM_STYLES:
    text += concepts.rim_icons('Brand.AppIcon', 'Brand.EngineIcon', concepts.RIM_STYLES[DEFAULT_ICON])
elif DEFAULT_ICON in concepts.STROKE_STYLES:
    text += concepts.stroke_icons('Brand.AppIcon', 'Brand.EngineIcon', DEFAULT_ICON)
elif DEFAULT_ICON in concepts.ANGLE_STYLES:
    text += concepts.angled_icons('Brand.AppIcon', 'Brand.EngineIcon', DEFAULT_ICON)
elif DEFAULT_ICON in concepts.FACET_STYLES:
    text += concepts.faceted_icons('Brand.AppIcon', 'Brand.EngineIcon', DEFAULT_ICON)
elif DEFAULT_ICON in concepts.CONCEPTS:
    text += concepts.default_icons(DEFAULT_ICON)
else:
    t, tip, pr, cur = glyphs[DEFAULT_ICON]
    text += icon('Brand.AppIcon', t, tip, DEFAULT_ICON)
    text += icon('Brand.EngineIcon', pr, cur, DEFAULT_ICON)
    text += icon('Brand.AppIcon.Small', S_TWO, S_TIP, 'A', SMALL_MARK_SCALE)
    text += icon('Brand.EngineIcon.Small', S_PROMPT, S_CURSOR, 'A', SMALL_MARK_SCALE)
if DEFAULT_WORD.startswith('W3') and DEFAULT_WORD[2:] in concepts.RIM_STYLES:
    text += concepts.rim_title('Brand.Wordmark', concepts.RIM_STYLES[DEFAULT_WORD[2:]])
elif DEFAULT_WORD.startswith('W3') and DEFAULT_WORD[2:] in concepts.STROKE_STYLES:
    text += concepts.stroke_title('Brand.Wordmark', DEFAULT_WORD[2:])
elif DEFAULT_WORD.startswith('W3Angle') and DEFAULT_WORD[2:] in concepts.ANGLE_STYLES:
    text += concepts.angled_title('Brand.Wordmark', DEFAULT_WORD[2:])
elif DEFAULT_WORD.startswith('W3Facet') and DEFAULT_WORD[2:] in concepts.FACET_STYLES:
    text += concepts.faceted_title('Brand.Wordmark', DEFAULT_WORD[2:])
elif DEFAULT_WORD in concepts.CONCEPTS:
    text += concepts.title('Brand.Wordmark', DEFAULT_WORD)
else:
    text += wordmark('Brand.Wordmark', DEFAULT_WORD)
if DEFAULT_ENGINE in concepts.ENGINE_STYLES or DEFAULT_ENGINE in concepts.TRACE_STYLES or DEFAULT_ENGINE in concepts.TRACE10_STYLES or DEFAULT_ENGINE in concepts.TRACE11_STYLES or DEFAULT_ENGINE in concepts.TRACE12_STYLES:
    # replace the engine icon of the -Icon variant by the round 8 engine mark
    for key in ('Brand.EngineIcon', 'Brand.EngineIcon.Small'):
        start = text.index('    <DrawingImage x:Key="' + key + '">')
        end = text.index('    </DrawingImage>', start) + len('    </DrawingImage>') + 1
        text = text[:start] + text[end:]
    text += (concepts.trace12_marks if DEFAULT_ENGINE in concepts.TRACE12_STYLES else concepts.trace11_marks if DEFAULT_ENGINE in concepts.TRACE11_STYLES else concepts.trace10_marks if DEFAULT_ENGINE in concepts.TRACE10_STYLES else concepts.trace_marks if DEFAULT_ENGINE in concepts.TRACE_STYLES else concepts.engine_marks)('Brand.EngineIcon', DEFAULT_ENGINE)
text += '''
    <LinearGradientBrush x:Key="Brand.BannerBrush" StartPoint="0%,0%" EndPoint="100%,100%">
        <GradientStop Color="#0B1220" Offset="0" />
        <GradientStop Color="#132A4C" Offset="0.55" />
        <GradientStop Color="#0B1220" Offset="1" />
    </LinearGradientBrush>
    <SolidColorBrush x:Key="Brand.AccentBrush" Color="#38BDF8" />
</ResourceDictionary>
'''
open(out, 'w', encoding='utf-8', newline='\r\n').write(text)
print('written', out, 'icon', DEFAULT_ICON, 'word', DEFAULT_WORD, 'engine', DEFAULT_ENGINE)
