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

        public Color ToDrawingColor()
        {
            return Color.FromArgb(a, r, g, b);
        }
    }
}
