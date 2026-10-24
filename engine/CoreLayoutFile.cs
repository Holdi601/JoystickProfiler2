using System;
using System.Collections.Generic;
using System.Drawing;

namespace JoyPro
{
    [Serializable]
    public sealed class LayoutFile
    {
        public string Joystick { get; set; }
        public string Font { get; set; }
        private byte r = 255;
        private byte g = 255;
        private byte b = 255;
        private byte a = 255;
        public Dictionary<string, System.Windows.Point> Positions;
        public int Size { get; set; }
        public Bitmap backup;
        public string KneeboardPostfix = string.Empty;

        public LayoutFile()
        {
            Joystick = string.Empty;
            Font = string.Empty;
            Positions = new Dictionary<string, System.Windows.Point>(StringComparer.OrdinalIgnoreCase);
            Size = 12;
            backup = null;
            KneeboardPostfix = string.Empty;
        }

        // Who made the sheet (a 2D layout variant's credit, LayoutVariant.Credit): printed on the cheat sheets. Optional so
        // JoyPro 1 layouts and older files load as before.
        [System.Runtime.Serialization.OptionalField]
        public string Credit = string.Empty;

        // The room of a label in the sheet's boxes (picture pixels, 0 = up to the next label): longer texts are drawn
        // smaller or cut (LayoutVariant.LabelWidth).
        [System.Runtime.Serialization.OptionalField]
        public float LabelWidth;

        public Color ToDrawingColor()
        {
            return Color.FromArgb(a, r, g, b);
        }

        public void SetDrawingColor(Color color)
        {
            a = color.A;
            r = color.R;
            g = color.G;
            b = color.B;
        }
    }
}
