# JoyPro branding

The JoyPro mark is the delta canard jet of the original JoyPro logo (top view, nose down, white line art on a dark kite
shield), redrawn as clean vector shapes: a filled white silhouette (delta wing, canards, fuselage with a dark outline
and a blue canopy) on a dark shield with a light rim and a thin blue inner line. The dark shield reads on light and
dark backgrounds; the filled silhouette stays readable at 16 px.

The "2" of JoyPro 2 is not a solid numeral: three bold strokes just outside the shield's lower right edge, all
parallel to that edge, with a clear constant gap to the shield like a second rim. The long middle stroke is the 2's
diagonal and ends in a blue tip (like the canopy); a short stroke level with its top and set to the left is the top,
a short stroke level with its bottom and set to the right is the foot. The stroke ends are cut level, and the ends
line up on common cut lines. At 16 / 24 px the strokes are heavier and keep at least 1 px of background to the
shield. This is variant TipBoldAligned (round 7); the earlier rounds below can still be picked.

The engine icon has its own mark in the same place and stroke language: a command prompt (two small top ticks, a
">" of three parallel strokes of falling length, a "_" as a level row of five short ticks, the last tick blue like a
blinking cursor). This is `PromptCursorBlue` (round 8).

The title "JOYPRO 2" is drawn as paths too: letters 20 units high with a stroke weight of 4, 45 degree chamfers,
slanted 12 degrees, "JOY" white, "PRO" in the accent blue, followed by the same three-stroke "2". No font is used
anywhere in the mark, icons or title (the letters are JoyPro's own path data).

| File | What |
|---|---|
| `branding/generate-branding.py` | The mark, the round 1 variants and the letters; writes `Styles/Branding.axaml` (and `Styles/BrandingConcepts.axaml`). |
| `branding/branding_concepts.py` | The round 2 concepts, the round 3 faceted "2" / ">_" (`F2`, `F_PROMPT`) and the round 4 angled ones (`ANGLE_STYLES`, `angled_two`, `angled_prompt`). Edit those shapes here. |
| `gui/JoyPro.Gui/Styles/Branding.axaml` | Generated, used by the app: `Brand.Logo` (mark), `Brand.AppIcon` (mark + "2"), `Brand.EngineIcon` (mark + ">_"), their `.Small` forms for 16/24 px, `Brand.Wordmark` (title), the round 1 variants, the banner brush. |
| `gui/JoyPro.Gui/Styles/BrandingConcepts.axaml` | Generated, only for the comparison sheets (not loaded by the app): round 2 and 3. |
| `docs/branding/joypro-logo.svg` | The mark as SVG. |
| `branding/generate-icons.ps1 [-Icon <variant>] [-Title <variant>] [-Engine <variant>]` | Writes the XAML with the chosen variants and renders all icons and the previews below. Default: `-Icon TipBoldAligned -Title W3TipBoldAligned -Engine PromptCursorBlue`. |
| `branding/base_joypro.ico`, `gui/JoyPro.Gui/Assets/joypro2_gui.ico`, `engine/assets/engine_console.ico` | Mark, GUI/tray icon, engine icon: PNG frames 16 to 256 px rendered from the vector shapes. |

## Variants

Pick any with `branding/generate-icons.ps1 -Icon <name> -Title <name>` (then commit the changed files).

**Round 12, Trace11Large rebalanced** (`variants-round12.png`; not the default yet). The user picked `Trace11Large`.
Everything from it is kept: width 4.6, gap 0.9, the solid grey `#707C8E`, blue on the long bar and the last "_"
tick, the edge's angle, the level-cut "_" ticks, the gap to the rim, and the "_" row's gap to the long bars. Changed:

- ">": the two short ticks are half as long as the long bars. Their tops stay on the long bars' top line.
- "_": the band moves down along the bars until its bottom edge is level with the lowest point of the ">" (the grey
  long bar's lower right corner), or 2 units above that (about 40 % of the band's height). It has 3, 4 or 5 ticks.
  Extra ticks extend the band to the right.

The icons keep Trace11Large's scale (0.89). The extra ticks only move the whole picture left to keep it centred:
compared with 3 ticks, the shield sits about 2.9 units further left with 4 ticks and about 5.8 with 5. The sheet is laid out like round 11.
The close-ups show the icon from 48 to 100.8 units at 9.7 px per unit, so the lowered band has room below it.
Shapes: `TRACE12_STYLES`, `trace12_strokes` in `branding/branding_concepts.py`.

| Variant | |
|---|---|
| `Trace12T3` | band on the bottom line of the ">", 3 ticks |
| `Trace12T4` | band on the bottom line of the ">", 4 ticks |
| `Trace12T5` | band on the bottom line of the ">", 5 ticks |
| `Trace12T3Up` | band 2 units above the bottom line, 3 ticks |
| `Trace12T4Up` | band 2 units above the bottom line, 4 ticks |
| `Trace12T5Up` | band 2 units above the bottom line, 5 ticks |

**Round 11, round 10 corrected** (`variants-round11.png`; not the default yet). Kept from round 10: every bar at the
edge's angle, one width, equal gaps, the gap to the rim, the blue placement. Changed after the user's notes:

- ">": only two short ticks; the second long bar is as long as the blue one, so both long bars share the top line
  and the bottom line.
- Bars: solid, no light rim. One grey for all non-blue bars, `#707C8E`, chosen for equal contrast on both
  backgrounds (about 4.2:1 on the dark `#111827` and 3.9:1 on the light `#F1F5F9`; white or near-black bars
  would be 17:1 on one background and almost invisible on the other).
- "_": each tick is cut level at the top (at the height of its top right corner) and at the bottom (at the height
  of its bottom left corner). The ticks become slanted parallelograms with level ends on one level band.

The sheet shows each variant with the drawing laid over it (magenta), at 256/48/32/16 px on dark and light, and as
a 512 px close-up of the strokes (the lower right quarter of the icon at 1024 px) on dark and light. Shapes:
`TRACE11_STYLES`, `trace11_strokes` in `branding/branding_concepts.py`.

| Variant | |
|---|---|
| `Trace11Exact` | width 3.6, blue on the long bar and the last "_" tick |
| `Trace11Large` | width 4.6, the group scaled up, blue on the long bar and the last "_" tick |
| `Trace11BarBlue` | Large, blue only on the long bar |

**Round 10, the ">_" drawn on our icon** (`variants-round10.png`; not the default yet): the user drew the engine mark
on our icon in GIMP (`images/9.png`; canvas 186..915 x 69..798 px = the 100 grid, 7.29 px per unit, the mark at
scale 1). Measured: two short dark ticks, a long blue bar and a long dark bar side by side (the ">"), their tops on one
line across the bars; below to the right a nearly level row of three short ticks (the "_": dark, dark, blue); all
parallel to the shield's lower right edge. Cleaned up: every bar exactly at the edge's angle (58.4 degrees), one
width, equal gaps, square ends with the ">" tops on one line and the "_" centres on one level line, the nearest bar
4 units off the rim (as the "2"). His bars are dark like the shield, so like the shield they get a thin light rim on
dark backgrounds. When a variant's strokes would leave the canvas, the whole icon (shield and strokes) is shrunk and
centred. Shapes: `TRACE10_STYLES`, `trace10_strokes` in `branding/branding_concepts.py`; his strokes for the overlay:
`sketch-9-strokes.png`.

| Variant | |
|---|---|
| `Trace10Exact` | his layout, width 3.6 (his 2.65 is too thin at 32 px), blue on the long bar and the last tick (as drawn) |
| `Trace10Large` | width 4.6, the group scaled up |
| `Trace10Ticks3` | Large with three short ticks in the ">" |
| `Trace10BarBlue` | Large, blue only on the long bar |

**Round 9, the engine mark traced from the user's sketch** (`variants-round9.png`; not the default yet): his drawing
(`images/6.png`, 865 x 679 px) shows our shield at full size, so his strokes were measured in its pixels and mapped
onto the mark (X = 50 + (x - 248.5) / 4.95, Y = 5.75 + (y - 21) / 4.92; shapes: `trace_strokes` in
`branding/branding_concepts.py`). Every stroke as drawn - the tiny tick and the blue tick on top, the two long strokes
(each a light head and a body on one line, the second hooking to the lower left), the separate row of five short ticks
- only cleaned up: straight lines, level-cut ends, one weight (his pen width, 1.9 units: TipBoldAligned's 5 would merge
his closely set strokes), the row's ticks at one angle (67.5 degrees) and evenly spaced. The sheet lays his strokes
(`sketch-6-strokes.png`, magenta) over the 256 px render. Variants: `TraceTopBlue` (his blue tick), `TraceHookBlue`,
`TraceRowTickBlue` (middle row tick), `TraceLastBlue` (last row tick), `TraceSketch` (his colours #808285, #58595B,
#00AACC). Pick one with `branding/generate-icons.ps1 -Engine TraceTopBlue`.

**Round 8, the engine mark (current default `PromptCursorBlue`)** (`variants-round8.png`, after the user's sketches):
the engine icon has its own mark in the place of the GUI's "2" (outside the shield's lower right edge, the same clear
gap), so the two programs can be told apart at a glance. It is a command prompt drawn in the same stroke language
(strokes parallel to the edge, TipBoldAligned's weight, level-cut ends): two small ticks on top (a grey one and a
white one stepping down to the right), a ">" made of three parallel strokes of falling length centred on one level (a
hatched arrow pointing right), and below it a "_" made of a level row of five short ticks. Exactly one element is blue;
the variants differ in where: `PromptTopBlue` (the second top tick), `PromptArmBlue` (the middle stroke of the arrow),
**`PromptCursorBlue`** (the last tick of the "_" row, like a blinking cursor; the default) and `PromptRowBlue` (the
whole "_" row). At 16 / 24 px: two arrow strokes and a row of three heavy ticks. Pick another with
`branding/generate-icons.ps1 -Engine PromptRowBlue`.

**Round 7, the current default** (`variants-round7.png`): **TipBoldAligned** (`-Icon TipBoldAligned -Title
W3TipBoldAligned`), the user's pick TipBold with two fixes. The strokes keep a clear, constant gap to the shield: the
stroke nearest to it (the top one) stays 4 units (of 100) off the shield's light rim, at 16 / 24 px 7.5 units (at
least 1 px of background). The bar ends line up: the top stroke ends level with the top of the diagonal and the foot
starts level with its bottom, so each pair shares one level cut line. The ">_" keeps the same clear gap: both arms run
at one distance from the shield's upper and lower right edges (the engine icon draws the mark a little smaller so the
">" fits), the blue "_" one stroke further out. `TipBoldAlignedWide` is the same with a 6 unit gap.

**Round 6** (`variants-round6.png`, variations of Rim3Tip): three strokes parallel to the shield's
lower right edge, cut ends, blue on the diagonal; only details vary (shapes: `RIM_STYLES` in
`branding/branding_concepts.py`). The ">_" uses the same cut strokes around the shield's right shoulder.

| Variant | Icon / title name | |
|---|---|---|
| TipThin | `-Icon TipThin -Title W3TipThin` | thinner strokes, tighter gap, a little more offset |
| TipBold | `-Icon TipBold -Title W3TipBold` | heavier strokes, looser gap, longer top and foot |
| TipEdgeCut | `-Icon TipEdgeCut -Title W3TipEdgeCut` | ends cut parallel to the shield's upper edges, the blue a separate short segment beyond the diagonal |
| TipBlueTop | `-Icon TipBlueTop -Title W3TipBlueTop` | the blue on the top stroke |
| TipRimLine | `-Icon TipRimLine -Title W3TipRimLine` | a thin light line along the rim as a 4th stroke |
| TipClear2 | `-Icon TipClear2 -Title W3TipClear2` | longer top and foot, more offset: reads more clearly as a "2" |

**Round 5** (`variants-round5.png`, after the user's two sketches): no solid numeral. The "2" is a
few line strokes just outside the shield's lower right edge, every stroke parallel to that edge (58.4 degrees), the
long one hugging it with a constant gap like a second rim. Read as an italic "2" along the edge: the long stroke is the
diagonal, short strokes or tick groups set level to the left of its top are the top, those to the right of its bottom
the foot; one blue stroke or stroke end. The ">_" follows the shield's right shoulder with two strokes (the ">"),
the "_" is a blue stroke along the lower edge. 16/24 px: three heavy strokes.

| Variant | Icon / title name | |
|---|---|---|
| Rim3 | `-Icon Rim3 -Title W3Rim3` | sketch 1: three strokes (top, diagonal, foot), round ends, blue foot |
| Rim3Tip | `-Icon Rim3Tip -Title W3Rim3Tip` | the same with level-cut (chamfered) ends, blue tip at the top of the diagonal |
| Hatch | `-Icon Hatch -Title W3Hatch` | sketch 2: two ticks, the long stroke, three ticks; the outer foot tick blue |
| HatchRim | `-Icon HatchRim -Title W3HatchRim` | Hatch with a thin light second rim along the whole edge |
| HatchChamfer | `-Icon HatchChamfer -Title W3HatchChamfer` | heavier level-cut strokes, the diagonal's lower end blue |

**Round 4** (`variants-round4.png`): the faceted "2" built only from the mark's own angles,
measured from its path data (degrees from the horizontal): shield upper edges 24, canard leading edge 32, canard inner
edge 48.5, wing sweep 51, shield lower edges 58, fuselage sides 80. The top edge falls to the right, the shoulder is cut
at 48.5, the right side leans at the fuselage angle, the diagonal runs at the wing sweep, the foot's edges rise slightly
to the right and its ends lean like the fuselage, the hook end and the seams are cut at 32 / 58; no right angles
between the pieces. The ">_" has long chevron arms with fuselage-angled ends, a seam at 32 and a slanted blue cursor.

| Variant | Icon / title name | |
|---|---|---|
| AngleA | `-Icon AngleA -Title W3AngleA` | mild: top 8, foot 3, right side 84, diagonal at the wing sweep |
| AngleB | `-Icon AngleB -Title W3AngleB` | medium: top 14, foot 5, right side at the fuselage angle (80) |
| AngleC | `-Icon AngleC -Title W3AngleC` | strong: top at the shield edge (24), diagonal at the shield's lower edge (58) |

**Round 3** (`variants-round3.png`, icon A placement and title W3 with the faceted "2"):

| Variant | Icon / title name | |
|---|---|---|
| FacetA | `-Icon FacetA -Title W3FacetA` | pieces with thin gaps, alternating white / light grey |
| FacetB | `-Icon FacetB -Title W3FacetB` | pieces joined by thin dark seams, each piece split into a light and a grey plane |
| FacetC | `-Icon FacetC -Title W3FacetC` | pieces with gaps, each split into two planes |

**Round 2** (`variants-round2.png`, broader concepts; icon and title share the name): `LineArt` (double-line outline
like the original logo), `Formation` (wing, sweep line and trailing edge as separate pieces), `Stencil` (cut out of a
second kite shield), `Hud` (segmented readout with a chevron underline), `Metal` (faceted bevels, blue edge),
`Integrated` (the shield's edge and tip form the "2").

**Round 1** (`variants.png`): icons `A` (solid numeral), `B` (line art), `C` (on a faceted plate); titles `W1` (upright,
blue "2"), `W2` (slanted, white), `W3` (slanted, JOY white, PRO blue, solid "2").

The main window starts with a banner: the mark, the title and the tagline on a dark navy gradient with an accent line;
it shrinks on small windows (Viewbox, never larger than designed); the theme buttons stay on its right. The banner is
dark in the light and the dark theme.

Game pictures: the Start page cards, export tiles and game lists show the cover Steam keeps for the game on this PC
(`Steam\appcache\librarycache\<app id>\...`, the portrait cover first). Nothing is downloaded and no game artwork is in
the repository. Games without a Steam cover get a JoyPro badge (`Views/Controls/GameBadge.cs`): a colour per game, a
simple motif (aircraft, helicopter for Arma and Hell Let Loose, rocket for the space games) and the game's short
name; Star Citizen, Falcon BMS and DCS standalone show their own executable icon on it.

## Previews (offscreen renders by `BrandingRenderTests`)

| Preview | |
|---|---|
| `variants-round10.png` | Round 10: the ">_" drawn on our icon, cleaned up, with the drawing laid over it |
| `variants-round11.png` | Round 11: round 10 with solid equal-contrast bars, aligned long bars and a level "_" band, plus 512 px close-ups |
| `variants-round12.png` | Round 12: Trace11Large with half-length short ticks and the "_" band lowered, 3/4/5 ticks |
| `variants-round9.png` | Round 9: the engine mark traced from the sketch, with the sketch laid over it |
| `variants-round8.png` | Round 8: the engine mark (a prompt in strokes; default PromptCursorBlue) next to the GUI icon |
| `variants-round7.png` | Round 7: TipBold vs TipBoldAligned (default) and TipBoldAlignedWide |
| `variants-round6.png` | Round 6: variations of Rim3Tip |
| `variants-round5.png` | Round 5: the "2" from strokes parallel to the shield's edge (the user's sketches) |
| `variants-round4.png` | Round 4: the "2" in the mark's angle language |
| `variants-round3.png` | Round 3: the faceted "2" with orthogonal pieces (FacetA/B/C) |
| `variants-round2.png` | Round 2: six concepts |
| `variants.png` | Round 1: icons A/B/C, titles W1/W2/W3 |
| `icon-app-16/32/48/256.png` | GUI / tray icon (mark + "2") |
| `icon-engine-16/32/48/256.png` | Engine icon (mark + ">_") |
| `joypro-mark-16/32/48/256.png` | The mark alone |
| `mark-with-2-on-light.png`, `mark-with-2-on-dark.png` | The icon at 16 to 160 px on light and dark, plus the engine icon |
| `banner-wide.png`, `banner-narrow.png` | The top of the main window at 1500 and 1200 px |
| `game-badges.png` | The badges of every supported game |
| `start-game-cards.png` | Start page game cards |
| `bottom-bar-<width>-output-open.png` / `-output-closed.png` | The bottom bar at 1920, 1500 and 1200 px; the same test checks the status line is never covered |
