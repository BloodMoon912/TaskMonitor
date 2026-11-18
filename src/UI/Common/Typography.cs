using System;

namespace task_monitor
{
    /// <summary>
    /// UI text scale for the WPF surfaces (设置 → 外观, or the taskbar right-click menu).
    ///
    /// Deliberately WPF-ONLY. The native taskbar overlay draws its own text with DirectWrite
    /// at 13 px / 11 px and sizes every cell from hand-measured line boxes; its two-line block
    /// is 44 DIP inside a ~48 DIP taskbar band, so it can absorb roughly a 4 % increase and
    /// nothing more. Scaling it would mean re-deriving the whole layout (the plan's P0
    /// "runtime text measurement" work), not flipping a number — so the overlay keeps 13 px
    /// and this scale changes the detail windows only.
    ///
    /// Values are offered as a small ladder rather than a free slider: every step has to be
    /// checked against the popup's fixed 360-DIP column and its 124/216-DIP blocks.
    /// </summary>
    internal static class Typography
    {
        public const double Small = 0.9;
        public const double Normal = 1.0;
        public const double Large = 1.15;
        public const double ExtraLarge = 1.3;

        /// <summary>Active scale; 1.0 = the shipped sizes.</summary>
        public static volatile float ScaleValue = (float)Normal;

        public static double Scale => ScaleValue;

        /// <summary>Taskbar right-click menu / settings combo index → scale (0 = 標準).</summary>
        public static double FromIndex(int index)
        {
            switch (index)
            {
                case 1: return Small;
                case 2: return Large;
                case 3: return ExtraLarge;
                default: return Normal;
            }
        }

        public static int IndexOf(double scale)
        {
            if (Math.Abs(scale - Small) < 0.01) return 1;
            if (Math.Abs(scale - Large) < 0.01) return 2;
            if (Math.Abs(scale - ExtraLarge) < 0.01) return 3;
            return 0;
        }

        public static void Apply(double scale) => ScaleValue = (float)scale;
    }
}
