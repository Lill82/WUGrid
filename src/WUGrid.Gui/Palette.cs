using System.Drawing;
using WUGrid.Core;

namespace WUGrid.Gui
{
    /// <summary>One colour per state: strong for the State cell and summary chips, a pale tint for the rest of the row.</summary>
    static class Palette
    {
        public static readonly Color Text = Hex(0x1F2937);
        public static readonly Color MutedText = Hex(0x6B7280);
        public static readonly Color Header = Hex(0xF3F4F6);
        public static readonly Color GridLine = Hex(0xE5E7EB);
        public static readonly Color AlarmText = Hex(0xB91C1C);
        public static readonly Color CleanText = Hex(0x15803D);
        public static readonly Color ActionText = Hex(0x1D4ED8);

        public static readonly (Color Strong, Color Tint) Checking = (Hex(0x9CA3AF), Color.White);
        public static readonly (Color Strong, Color Tint) RestartPending = (Hex(0x7C3AED), Hex(0xF5F3FF));
        public static readonly (Color Strong, Color Tint) NoRestartPending = (Hex(0x64748B), Hex(0xF8FAFC));
        public static readonly (Color Strong, Color Tint) Installing = (Hex(0x0891B2), Hex(0xECFEFF));
        public static readonly (Color Strong, Color Tint) Down = (Hex(0xDC2626), Hex(0xFEF2F2));
        public static readonly (Color Strong, Color Tint) Booting = (Hex(0xCA8A04), Hex(0xFEFCE8));
        public static readonly (Color Strong, Color Tint) BackClean = (Hex(0x16A34A), Hex(0xF0FDF4));
        public static readonly (Color Strong, Color Tint) BackFlagged = (Hex(0xEA580C), Hex(0xFFF7ED));

        public static (Color Strong, Color Tint) For(ServerStatus s)
        {
            switch (s.State)
            {
                case ServerState.RestartPending: return RestartPending;
                case ServerState.NoRestartPending: return NoRestartPending;
                case ServerState.Installing: return Installing;
                case ServerState.Down: return Down;
                case ServerState.Booting: return Booting;
                case ServerState.Back: return s.IsClean ? BackClean : BackFlagged;
                default: return Checking;
            }
        }

        /// <summary>Slightly darker, for the selected row, so its state colour still shows.</summary>
        public static Color Darker(Color c, float by = 0.08f) =>
            Color.FromArgb((int)(c.R * (1 - by)), (int)(c.G * (1 - by)), (int)(c.B * (1 - by)));

        static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }
}
