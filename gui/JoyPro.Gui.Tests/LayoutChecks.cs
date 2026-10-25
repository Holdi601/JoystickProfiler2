using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using JoyPro.Gui.Tests.QualityGate;

namespace JoyPro.Gui.Tests;

// The layout rules of gate I10, also used by the fast layout tests (device cards, text size, fonts):
//   - no two visible interactive controls (buttons, toggles, check boxes, text and combo boxes) or chips intersect,
//     unless one holds the other;
//   - no visible text is drawn over an interactive control or a chip it does not belong to (a chip's text running into
//     the "Model..." button next to it);
//   - no one-line text without trimming needs more width than it got.
// Popups and flyouts are their own layer and not part of the page.
internal static class LayoutChecks
{
    public static IEnumerable<(Control A, Rect RA, Control B, Rect RB)> Overlaps(Control root, double width, double height, Control? scope = null)
    {
        List<(Control Control, Rect Rect)> boxes = [];
        List<(TextBlock Text, Rect Rect)> texts = [];
        foreach (Control c in (scope ?? root).GetVisualDescendants().OfType<Control>())
        {
            if (c is TextBlock text)
            {
                if (string.IsNullOrWhiteSpace(text.Text) || IsFreeText(text))
                    continue;
                Rect? seen = GateRender.SeenRect(text, root, width, height);
                Rect? ink = seen == null ? null : InkRect(text, root);
                if (ink != null && seen != null && ink.Value.Intersect(seen.Value) is { Width: >= 1, Height: >= 1 } inked)
                    texts.Add((text, inked));
                continue;
            }
            // Chips, and the floating panels over a picture (a question, a note, an empty state): none may lie on another.
            bool chip = c is Border && (c.Classes.Contains("chip") || c.Classes.Contains("floatingLabel") || c.Classes.Contains("emptyState"));
            if (!chip && c is not (Button or TextBox or ComboBox or AutoCompleteBox or NumericUpDown))
                continue;
            // A part of another control's template (scroll bar arrows, a spinner's buttons, a combo box's toggle) is
            // that control's own business.
            if (!chip && c.TemplatedParent != null)
                continue;
            Rect? r = GateRender.SeenRect(c, root, width, height);
            if (r != null)
                boxes.Add((c, r.Value));
        }
        boxes = boxes.OrderBy(i => i.Rect.X).ToList();
        for (int i = 0; i < boxes.Count; i++)
        {
            for (int j = i + 1; j < boxes.Count && boxes[j].Rect.X < boxes[i].Rect.Right; j++)
            {
                if (Conflict(boxes[i].Control, boxes[i].Rect, boxes[j].Control, boxes[j].Rect))
                    yield return (boxes[i].Control, boxes[i].Rect, boxes[j].Control, boxes[j].Rect);
            }
        }
        foreach ((TextBlock text, Rect tr) in texts)
        {
            foreach ((Control box, Rect br) in boxes)
            {
                if (br.X >= tr.Right || tr.X >= br.Right)
                    continue;
                if (Conflict(text, tr, box, br))
                    yield return (text, tr, box, br);
            }
        }
    }

    private static bool Conflict(Control a, Rect ra, Control b, Rect rb)
    {
        Rect cut = ra.Intersect(rb);
        if (cut.Width <= 2 || cut.Height <= 2)
            return false;
        if (a.IsVisualAncestorOf(b) || b.IsVisualAncestorOf(a))
            return false;
        // A text inside a control's template (a button's caption, a combo box's item) belongs to it.
        if (a is TextBlock && a.TemplatedParent is Control owner && (owner == b || b.IsVisualAncestorOf(owner) || owner.IsVisualAncestorOf(b)))
            return false;
        // A modal question over a dimmed area (Classes="modalOverlay") covers what is under it on purpose.
        return InModalOverlay(a) == InModalOverlay(b);
    }

    // A text placed freely on a canvas (gauge ticks, HUD previews, picture labels) or in a scaled preview.
    private static bool IsFreeText(TextBlock text) =>
        text.GetVisualParent() is Canvas || text.GetVisualAncestors().Any(a => a is Viewbox or LayoutTransformControl { Name: not "UiScaleHost" });

    // Where the glyphs of the text are (a stretched text block is wider than its text), in window coordinates.
    private static Rect? InkRect(TextBlock text, Control root)
    {
        Rect? bounds = GateRender.BoundsInWindow(text, root);
        if (bounds == null)
            return null;
        double inkWidth = Math.Min(text.Bounds.Width, text.TextLayout.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right);
        double inkHeight = Math.Min(text.Bounds.Height, text.TextLayout.Height + text.Padding.Top + text.Padding.Bottom);
        double x = text.TextAlignment switch
        {
            TextAlignment.Center => (text.Bounds.Width - inkWidth) / 2,
            TextAlignment.Right or TextAlignment.End => text.Bounds.Width - inkWidth,
            _ => 0
        };
        Matrix? m = text.TransformToVisual(root);
        return m == null ? null : new Rect(x, 0, inkWidth, inkHeight).TransformToAABB(m.Value);
    }

    private static bool InModalOverlay(Control c) => c.GetVisualAncestors().OfType<Control>().Any(a => a.Classes.Contains("modalOverlay"));

    // One-line texts without trimming that need more width than they got.
    public static IEnumerable<(TextBlock Text, double Need, double Got)> ClippedTexts(Control root, double width, double height, Control? scope = null)
    {
        foreach (TextBlock text in (scope ?? root).GetVisualDescendants().OfType<TextBlock>())
        {
            if (string.IsNullOrWhiteSpace(text.Text) || text.TextTrimming != TextTrimming.None || text.TextWrapping != TextWrapping.NoWrap)
                continue;
            // A text box's placeholder is its own business.
            if (text.TemplatedParent is TextBox || IsFreeText(text))
                continue;
            if (!GateRender.IsSeen(text, root, width, height))
                continue;
            FormattedText formatted = new(text.Text!, CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, null);
            double need = formatted.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;
            double got = text.Bounds.Width;
            // What the text is drawn in can also be narrower than the text block (a clipping parent).
            for (Visual? v = text.GetVisualParent(); v != null && v != root; v = v.GetVisualParent())
            {
                if (v is Control parent && parent.ClipToBounds)
                {
                    Rect? pr = GateRender.BoundsInWindow(parent, root);
                    Rect? tr = GateRender.BoundsInWindow(text, root);
                    // Window units are the text's own units times the UI scale.
                    double scale = text.TransformToVisual(root)?.M11 is double m11 && m11 > 0 ? m11 : 1;
                    if (pr != null && tr != null)
                        got = Math.Min(got, Math.Max(0, (pr.Value.Right - tr.Value.X) / scale));
                    break;
                }
            }
            if (need > got + 1.5)
                yield return (text, need, got);
        }
    }

    // Controls and texts that stick out of their frame (a panel, card, callout or floating question with
    // Classes surface / card / callout / floatingLabel): content that does not fit its frame must scroll or wrap, never
    // run over what is below it (the device question's tiles over the picture's status row).
    public static IEnumerable<(Control Item, Rect ItemRect, Border Frame, Rect FrameRect)> Spills(Control root, double width, double height, Control? scope = null)
    {
        foreach (Border frame in (scope ?? root).GetVisualDescendants().OfType<Border>())
        {
            if (!frame.Classes.Contains("surface") && !frame.Classes.Contains("card") && !frame.Classes.Contains("callout") && !frame.Classes.Contains("floatingLabel"))
                continue;
            if (GateRender.SeenRect(frame, root, width, height) == null || GateRender.BoundsInWindow(frame, root) is not Rect fr)
                continue;
            foreach (Control c in frame.GetVisualDescendants().OfType<Control>())
            {
                bool text = c is TextBlock t && !string.IsNullOrWhiteSpace(t.Text);
                if (!text && c is not (Button or TextBox or ComboBox))
                    continue;
                if (!text && c.TemplatedParent != null)
                    continue;
                // Inside a scroll viewer or a clipping parent within the frame: cut there, not spilled.
                bool clipped = false;
                for (Visual? v = c.GetVisualParent(); v != null && v != frame; v = v.GetVisualParent())
                {
                    if (v is Avalonia.Controls.Presenters.ScrollContentPresenter or Canvas or Viewbox || v is Control { ClipToBounds: true })
                    {
                        clipped = true;
                        break;
                    }
                }
                if (clipped)
                    continue;
                Rect? seen = text ? InkRect((TextBlock)c, root) : GateRender.SeenRect(c, root, width, height);
                if (seen is not Rect r || r.Width < 1 || r.Height < 1 || GateRender.SeenRect(c, root, width, height) == null)
                    continue;
                if (r.X < fr.X - 2 || r.Y < fr.Y - 2 || r.Right > fr.Right + 2 || r.Bottom > fr.Bottom + 2)
                    yield return (c, r, frame, fr);
            }
        }
    }

    // The share of the page's pixels that are not opaque when the page is drawn on its own: text drawn onto transparency
    // is smoothed against nothing and comes out garbled on the screen it lands on; every page must carry its background.
    public static double TransparentShare(Control page, int width, int height)
    {
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        bitmap.Render(page);
        int stride = width * 4;
        byte[] buffer = new byte[stride * height];
        System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }
        long transparent = 0;
        for (int i = 3; i < buffer.Length; i += 4)
        {
            if (buffer[i] < 255)
                transparent++;
        }
        return transparent / (double)(width * height);
    }

    public static string Describe(Control c) => c is TextBlock t ? "text '" + Gate.Short(t.Text ?? string.Empty, 40) + "'" : GateRender.Describe(c);
}
