using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// Lightweight WPF-only area/line chart for a 0–100 usage history (CPU / RAM /
    /// 磁盘 / GPU %). Renders directly via DrawingContext with no external charting
    /// library.
    /// </summary>
    public sealed class UsageHistoryChart : FrameworkElement
    {
        public static readonly DependencyProperty BackgroundProperty =
            DependencyProperty.Register(
                nameof(Background),
                typeof(Brush),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush Background
        {
            get => (Brush)GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public static readonly DependencyProperty ValuesProperty =
            DependencyProperty.Register(
                nameof(Values),
                typeof(IReadOnlyList<double?>),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Value at the top of the plot (default 100 = a percentage chart). The
        /// history window points temperature / byte-rate series at this too: without it a
        /// 65 °C or a 12 MB/s series is clipped by the 0–100 grid.</summary>
        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(
                nameof(Maximum),
                typeof(double),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public static readonly DependencyProperty AccentColorProperty =
            DependencyProperty.Register(
                nameof(AccentColor),
                typeof(Color),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0x78, 0xD4), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GridColorProperty =
            DependencyProperty.Register(
                nameof(GridColor),
                typeof(Color),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(Color.FromArgb(0x33, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillOpacityProperty =
            DependencyProperty.Register(
                nameof(FillOpacity),
                typeof(double),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(48.0 / 255.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FrameColorProperty =
            DependencyProperty.Register(
                nameof(FrameColor),
                typeof(Color),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(Color.FromArgb(0x66, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FrameCornerRadiusProperty =
            DependencyProperty.Register(
                nameof(FrameCornerRadius),
                typeof(double),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<double?> Values
        {
            get => (IReadOnlyList<double?>)GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        public Color AccentColor
        {
            get => (Color)GetValue(AccentColorProperty);
            set => SetValue(AccentColorProperty, value);
        }

        public Color GridColor
        {
            get => (Color)GetValue(GridColorProperty);
            set => SetValue(GridColorProperty, value);
        }

        public double FillOpacity
        {
            get => (double)GetValue(FillOpacityProperty);
            set => SetValue(FillOpacityProperty, value);
        }

        public Color FrameColor
        {
            get => (Color)GetValue(FrameColorProperty);
            set => SetValue(FrameColorProperty, value);
        }

        public double FrameCornerRadius
        {
            get => (double)GetValue(FrameCornerRadiusProperty);
            set => SetValue(FrameCornerRadiusProperty, value);
        }

        /// <summary>
        /// Returns the nearest data point index for the given mouse position,
        /// or -1 if the position is too far from any point.
        /// </summary>
        public int HitTest(Point pt)
        {
            var values = Values;
            if (values == null || values.Count < 2)
                return -1;

            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
                return -1;

            int n = values.Count;
            double stepX = w / (n - 1);
            double tolerance = Math.Max(stepX * 0.5, 8.0);

            int nearest = (int)Math.Round(pt.X / stepX);
            nearest = Math.Max(0, Math.Min(n - 1, nearest));

            double pointX = nearest * stepX;
            return Math.Abs(pt.X - pointX) <= tolerance ? nearest : -1;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
                return;

            // Clip everything to the rounded shape so the area fill / line never leak
            // past the frame's rounded corners.
            double clipR = Math.Min(FrameCornerRadius, Math.Min(w, h) / 2.0);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), clipR, clipR));

            if (Background != null)
                dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

            // Plot from ONE sample on: a single bucket is exactly what the history window holds
            // during the first minutes of a session, and the grid plus the scale labels are what
            // keeps that frame from reading as "nothing was ever collected".
            var values = Values;
            if (values != null && values.Count > 0)
            {
                DrawGrid(dc, w, h);
                DrawAreaAndLine(dc, w, h, values);
                DrawScaleLabels(dc, w, h);
            }

            dc.Pop();

            // Frame drawn last (unclipped, on top) so the border stays crisp.
            DrawFrame(dc, w, h);
        }

        /// <summary>Plot ceiling; a non-positive Maximum falls back to 100 so a bad caller
        /// can never divide by zero.</summary>
        private double PlotTop() => Maximum > 0 ? Maximum : 100.0;

        private void DrawGrid(DrawingContext dc, double w, double h)
        {
            var gridPen = new Pen(new SolidColorBrush(GridColor), 0.75);
            if (gridPen.CanFreeze)
                gridPen.Freeze();

            // Inner grid lines only — the 0%/100% boundary is drawn as a rounded
            // frame in DrawFrame so the outer edge reads as a single crisp border.
            // Horizontal at 25%, 50%, 75%.
            for (int i = 1; i <= 3; i++)
            {
                double y = h * (1.0 - i / 4.0);
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }

            // Vertical divides the 60-tick history (30/60/120 s at 0.5/1/2 s 采样间隔) into 4 segments (interior only).
            for (int i = 1; i <= 3; i++)
            {
                double x = w * (i / 4.0);
                dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
            }
        }

        // Outer frame: rounded corners + a more prominent stroke than the inner grid.
        private void DrawFrame(DrawingContext dc, double w, double h)
        {
            var framePen = new Pen(new SolidColorBrush(FrameColor), 1.0);
            if (framePen.CanFreeze)
                framePen.Freeze();

            // Inset by half the thickness so the full stroke is visible inside the
            // element bounds (otherwise the outer half is clipped at the edges).
            double inset = framePen.Thickness / 2.0;
            double r = Math.Min(FrameCornerRadius, Math.Min(w, h) / 2.0);
            var rect = new Rect(inset, inset, w - inset * 2, h - inset * 2);
            dc.DrawRoundedRectangle(null, framePen, rect, r, r);
        }

        private void DrawAreaAndLine(DrawingContext dc, double w, double h, IReadOnlyList<double?> values)
        {
            int n = values.Count;
            double scale = PlotTop();

            // One column per sample. A one-sample series has no spacing to derive (n - 1 == 0),
            // so its single point is centred rather than pinned to the left edge.
            double stepX = n > 1 ? w / (n - 1) : 0.0;

            double XOf(int i) => n > 1 ? i * stepX : w * 0.5;
            double YOf(double v) => h * (1.0 - v / scale);

            var areaGeometry = new PathGeometry();
            var lineGeometry = new PathGeometry();
            var marks = new List<Point>();

            // One figure per contiguous RUN of samples. A null bin is a minute that never got
            // recorded, so joining across it would draw a straight line through data that does
            // not exist; the run ends there instead and the next one starts fresh. A non-finite
            // value would poison the whole path geometry, so it counts as a hole too.
            var run = new List<Point>();
            for (int i = 0; i <= n; i++)
            {
                double? v = i < n ? values[i] : null;
                if (v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value))
                {
                    run.Add(new Point(XOf(i), YOf(v.Value)));
                    continue;
                }

                if (run.Count >= 2)
                {
                    var line = new PathFigure { StartPoint = run[0] };
                    for (int k = 1; k < run.Count; k++)
                        line.Segments.Add(new LineSegment(run[k], true));
                    lineGeometry.Figures.Add(line);

                    // The fill is the same run dropped to the baseline at both ends, so the
                    // shaded area stops where the data stops.
                    var area = new PathFigure { StartPoint = new Point(run[0].X, h) };
                    for (int k = 0; k < run.Count; k++)
                        area.Segments.Add(new LineSegment(run[k], true));
                    area.Segments.Add(new LineSegment(new Point(run[run.Count - 1].X, h), true));
                    area.IsClosed = true;
                    areaGeometry.Figures.Add(area);
                }
                else if (run.Count == 1)
                {
                    // A run of one has zero width, so neither the stroke nor the fill can show
                    // it — this is the case that left the history window blank.
                    marks.Add(run[0]);
                }

                run.Clear();
            }

            if (areaGeometry.Figures.Count > 0)
            {
                byte fillAlpha = (byte)Math.Round(Math.Max(0, Math.Min(255, FillOpacity * 255)));
                var fillBrush = new SolidColorBrush(Color.FromArgb(fillAlpha, AccentColor.R, AccentColor.G, AccentColor.B));
                if (fillBrush.CanFreeze)
                    fillBrush.Freeze();
                dc.DrawGeometry(fillBrush, null, areaGeometry);
            }

            if (lineGeometry.Figures.Count > 0)
            {
                var linePen = new Pen(new SolidColorBrush(AccentColor), 1.5);
                if (linePen.CanFreeze)
                    linePen.Freeze();
                dc.DrawGeometry(null, linePen, lineGeometry);
            }

            // Markers last: a lone bucket has no neighbour to connect to, so a dot is the only
            // trace it can leave — without it the frame reads as "no data" while the stats below
            // it already report a value.
            foreach (var p in marks)
                DrawSinglePoint(dc, p, w, h);
        }

        /// <summary>
        /// The one visible mark a sample can get when nothing connects to it: a filled dot at
        /// its height plus a short level dash. The dash is what makes a single 46 % bucket
        /// readable across a frame several hundred DIP wide, where a 4 DIP dot is a speck.
        /// </summary>
        private void DrawSinglePoint(DrawingContext dc, Point p, double w, double h)
        {
            const double radius = 4.0;
            const double halfDash = 12.0;

            // Keep the mark whole. The newest bucket sits ON the right edge (a 100 % one on the
            // top edge), where an unclamped dot is sliced in half by the rounded clip — and this
            // dot is the only evidence the sample exists.
            double x = Math.Max(radius + 1, Math.Min(w - radius - 1, p.X));
            double y = Math.Max(radius + 1, Math.Min(h - radius - 1, p.Y));

            var brush = new SolidColorBrush(AccentColor);
            if (brush.CanFreeze)
                brush.Freeze();

            double from = Math.Max(radius + 1, x - halfDash);
            double to = Math.Min(w - radius - 1, x + halfDash);
            if (to - from >= 1)
            {
                var pen = new Pen(brush, 1.5);
                if (pen.CanFreeze)
                    pen.Freeze();
                dc.DrawLine(pen, new Point(from, y), new Point(to, y));
            }

            dc.DrawEllipse(brush, null, new Point(x, y), radius, radius);
        }

        /// <summary>
        /// The plot's ceiling and its zero, in the same muted secondary color the network
        /// chart's edge annotations use: the existing grid already fixes those two heights (the
        /// frame's inner top and bottom edge), so this only puts a number on what is drawn —
        /// without it a flat line says nothing about whether it sits at 46 % or at 96 %.
        /// </summary>
        private void DrawScaleLabels(DrawingContext dc, double w, double h)
        {
            // Below this the two labels would land on each other; the grid alone then has to
            // carry the frame.
            if (w < 48 || h < 36)
                return;

            const double size = 10;
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(SystemFonts.MessageFontFamily,
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var brush = new SolidColorBrush(ChartPalette.LabelGray);
            if (brush.CanFreeze)
                brush.Freeze();

            // Inset so the rounded clip never eats a glyph, and clear of the 25/50/75 guide
            // lines DrawGrid already draws.
            var top = new FormattedText(FormatScale(PlotTop()), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
            dc.DrawText(top, new Point(5, 2));

            var zero = new FormattedText("0", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
            dc.DrawText(zero, new Point(5, h - zero.Height - 2));
        }

        /// <summary>
        /// A compact ceiling. <see cref="Maximum"/> is a bare double in whatever unit the caller
        /// plots (percent, °C, bytes per second — 1 318 912 for a 1.2 MB/s peak), so the label
        /// keeps the magnitude and drops the noise.
        /// </summary>
        private static string FormatScale(double value)
        {
            if (value >= 1e9) return (value / 1e9).ToString("0.#", CultureInfo.CurrentCulture) + "G";
            if (value >= 1e6) return (value / 1e6).ToString("0.#", CultureInfo.CurrentCulture) + "M";
            if (value >= 1e3) return (value / 1e3).ToString("0.#", CultureInfo.CurrentCulture) + "K";
            return value.ToString("0.#", CultureInfo.CurrentCulture);
        }
    }
}
