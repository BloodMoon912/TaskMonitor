using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace task_monitor
{
    /// <summary>
    /// 監控歷史 window: pick a metric and a time range, see the minute-averaged curve plus its
    /// average/max/min, and open the CSV folder.
    ///
    /// Built in code (no XAML) on purpose: it is one straight column of three controls, and a
    /// XAML file would only add a second place to keep the metric list in sync with
    /// <see cref="HistoryStore.MetricNames"/>.
    ///
    /// Where the numbers come from: 1 h / 6 h / 24 h are inside the memory ring
    /// (<see cref="HistoryStore.Series"/>); 7 days is longer than the ring, so it is read back
    /// from <c>history\yyyy-MM-dd.csv</c> (<see cref="HistoryStore.SeriesFromFiles"/>, which takes
    /// today from memory so no minute is counted twice). That read is bounded and runs only on
    /// this window's 5 s refresh — never on the sampling tick.
    ///
    /// Every user-visible string goes through <see cref="Loc.T"/> with its Simplified-Chinese
    /// source string as the key, because this window is built in code and so never passes through
    /// <see cref="Loc.Apply"/>'s visual-tree walk with untranslated content.
    /// </summary>
    internal sealed class HistoryWindow : Window
    {
        private const int ChartPoints = 60;

        /// <summary>1440 minutes is the ring's own horizon; anything longer needs the CSV.</summary>
        private const int MemoryRangeLimit = 24 * 60;

        private static readonly int[] RangeMinutes = { 60, 360, 1440, 10080 };

        /// <summary>Simplified-Chinese source strings (= Loc keys), same order as
        /// <see cref="RangeMinutes"/>; translated once at construction.</summary>
        private static readonly string[] RangeKeys = { "最近 1 小时", "最近 6 小时", "最近 24 小时", "最近 7 天" };

        /// <summary>Loc keys for the nine metrics, same indices as <see cref="HistoryStore.Cpu"/>
        /// … <see cref="HistoryStore.NetDown"/>. The store's own <see cref="HistoryStore.MetricNames"/>
        /// stays the persisted truth (its text is written into the CSV header), so the display names
        /// live here — and only here — with the store untouched.</summary>
        private static readonly string[] MetricKeys =
        {
            "CPU %", "内存 %", "磁盘 %", "GPU %", "GPU 温度", "GPU 显存 %", "CPU 温度", "网络 上行", "网络 下行",
        };

        private readonly HistoryStore _store;
        private readonly UsageHistoryChart _chart;
        private readonly ComboBox _metricBox;
        private readonly ComboBox _rangeBox;
        private readonly TextBlock _stats;
        private int _metric;
        private int _range = 1;                    // default 6 小時
        private DispatcherTimer _timer;

        public HistoryWindow(HistoryStore store)
        {
            _store = store;
            Title = Loc.T("监控历史");
            Width = 540;
            Height = 360;
            MinWidth = 460;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = ThemeBrush("ApplicationBackgroundBrush", Color.FromRgb(0x20, 0x20, 0x20));
            Foreground = ThemeBrush("TextFillColorPrimaryBrush", Colors.White);

            var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

            var pickers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            _metricBox = new ComboBox { Width = 150, ItemsSource = MetricLabels(), SelectedIndex = 0 };
            _metricBox.SelectionChanged += (s, e) => { _metric = Math.Max(0, _metricBox.SelectedIndex); Refresh(); };
            _rangeBox = new ComboBox { Width = 150, ItemsSource = RangeLabels(), SelectedIndex = _range, Margin = new Thickness(10, 0, 0, 0) };
            _rangeBox.SelectionChanged += (s, e) => { _range = Math.Max(0, _rangeBox.SelectedIndex); Refresh(); };
            pickers.Children.Add(_metricBox);
            pickers.Children.Add(_rangeBox);
            root.Children.Add(pickers);

            _chart = new UsageHistoryChart { Height = 170, HorizontalAlignment = HorizontalAlignment.Stretch };
            // Same palette the four detail pages hand their history chart. The DPs default to a
            // near-black grid plus a fixed accent, which on this window's theme-derived (dark)
            // background makes the grid, the frame and the axis labels fade out — the other half
            // of "the chart is empty".
            _chart.AccentColor = ChartPalette.Accent;
            _chart.GridColor = ChartPalette.Grid;
            _chart.FrameColor = ChartPalette.FaintBase(0x66);
            root.Children.Add(_chart);

            _stats = new TextBlock { Margin = new Thickness(0, 12, 0, 8), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            root.Children.Add(_stats);

            var open = new Button { Content = Loc.T("打开历史文件夹（CSV）"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4) };
            open.Click += (s, e) => OpenFolder();
            root.Children.Add(open);

            Content = root;

            // The buckets change once a minute; a 5 s refresh keeps the curve live without
            // making the window itself a sampling load. The 7-day range re-reads its day files
            // on the same tick — bounded (≤ 30 files), line-streamed, and off the sampler.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) => { Refresh(); _timer.Start(); };
            Closed += (s, e) => _timer.Stop();
        }

        private void Refresh()
        {
            if (_store == null) return;
            int minutes = RangeMinutes[Math.Min(Math.Max(_range, 0), RangeMinutes.Length - 1)];
            bool fromFiles = minutes > MemoryRangeLimit;

            double?[] series = fromFiles
                ? _store.SeriesFromFiles(_metric, minutes, ChartPoints)
                : _store.Series(_metric, minutes, ChartPoints);
            _chart.Values = series;

            // The chart's Y ceiling follows the metric: percentages top out at 100, a
            // temperature needs headroom (a 65 °C line inside a 0–100 box looks wrong and a
            // 105 °C one is clipped), and a byte rate has no natural bound at all.
            _chart.Maximum = Ceiling(series);

            // "No sample at all" is not an average of zero: the minute ring starts empty and gains
            // one bucket per minute, so a session that was just started legitimately has nothing to
            // plot. Judge it from the series that was just charted, so the text below can never
            // contradict the picture above it.
            bool hasSeries = TryStatsFromSeries(series, out double seriesAvg, out double seriesMax, out double seriesMin, out int seriesBins);

            if (fromFiles)
            {
                // The CSV path has no per-minute bucket statistics of its own, so the summary is
                // computed from the series just charted — and marked as file-sourced.
                _stats.Text = hasSeries
                    ? StatsText(seriesAvg, seriesMax, seriesMin, string.Format(Loc.T("（读自 CSV，{0} 个分区）"), seriesBins))
                    : Loc.T("此时段没有历史文件可读（CSV 只保留最近 30 天，今天的数据来自内存）。");
                return;
            }

            if (!hasSeries)
            {
                _stats.Text = Loc.T("此时段尚无数据——历史每分钟累积一笔，请稍候；或把范围调大。");
                return;
            }

            if (_store.TryGetStats(_metric, minutes, out double a, out double mx, out double mn, out int samples))
                _stats.Text = StatsText(a, mx, mn, string.Format(Loc.T("（{0} 个分钟桶）"), samples));
            else
                // The ring's own tally came back empty for the very window the curve already
                // covers; print the numbers the chart is showing instead of blanking the line.
                _stats.Text = StatsText(seriesAvg, seriesMax, seriesMin, string.Format(Loc.T("（{0} 个分钟桶）"), seriesBins));
        }

        /// <summary>Chart ceiling for the current metric — see the call site for the reasoning.
        /// A byte-rate series follows the peak it actually reached, which matters more on the
        /// 7-day range: one forgotten 12 MB/s burst would otherwise flatten the whole week.</summary>
        private double Ceiling(double?[] series)
        {
            if (_metric == HistoryStore.CpuTemp || _metric == HistoryStore.GpuTemp) return 110;
            if (_metric == HistoryStore.NetUp || _metric == HistoryStore.NetDown)
            {
                double peak = 0;
                if (series != null) foreach (double? v in series) if (v.HasValue && v.Value > peak) peak = v.Value;
                return peak > 0 ? peak * 1.1 : 1024;
            }
            return 100;
        }

        private string StatsText(double average, double max, double min, string scopeNote)
        {
            return MetricLabel(_metric) + "　" +
                   Loc.T("平均") + " " + Format(average) + "　" +
                   Loc.T("最高") + " " + Format(max) + "　" +
                   Loc.T("最低") + " " + Format(min) + "　" +
                   scopeNote;
        }

        /// <summary>Average/max/min over the plotted series, skipping the empty bins. Only the
        /// file-backed path uses this: the memory path keeps <see cref="HistoryStore.TryGetStats"/>
        /// (it can say how many real minute buckets the window holds).</summary>
        private static bool TryStatsFromSeries(double?[] series, out double average, out double max, out double min, out int samples)
        {
            average = max = min = double.NaN;
            samples = 0;
            if (series == null) return false;

            double sum = 0;
            foreach (double? v in series)
            {
                if (!v.HasValue) continue;
                double d = v.Value;
                sum += d;
                samples++;
                if (double.IsNaN(max) || d > max) max = d;
                if (double.IsNaN(min) || d < min) min = d;
            }
            if (samples == 0) return false;
            average = sum / samples;
            return true;
        }

        /// <summary>Formats a bucket value for the metric: percentages, °C, or a byte rate
        /// through the app's shared formatter (so 「1.2 MB/s」 reads the same as the net page).</summary>
        private string Format(double value)
        {
            if (_metric == HistoryStore.CpuTemp || _metric == HistoryStore.GpuTemp) return $"{value:0.0} °C";
            if (_metric == HistoryStore.NetUp || _metric == HistoryStore.NetDown)
                return NetRateFormatter.Format((long)value);
            return $"{value:0.0} %";
        }

        /// <summary>Display name for a metric through the Loc catalogue. Falls back to the
        /// store's own name when the key table has drifted out of step with
        /// <see cref="HistoryStore.MetricNames"/> — a wrong label is worse than an
        /// untranslated one, and a future 10th metric must not silently show metric 9's name.</summary>
        private static string MetricLabel(int metric)
        {
            if (metric < 0 || metric >= HistoryStore.MetricNames.Length) return string.Empty;
            if (MetricKeys.Length != HistoryStore.MetricNames.Length) return HistoryStore.MetricNames[metric];
            return Loc.T(MetricKeys[metric]);
        }

        private static string[] MetricLabels()
        {
            var labels = new string[HistoryStore.MetricNames.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = MetricLabel(i);
            return labels;
        }

        private static string[] RangeLabels()
        {
            var labels = new string[RangeKeys.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = Loc.T(RangeKeys[i]);
            return labels;
        }

        private void OpenFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(_store.Directory);
                System.Diagnostics.Process.Start("explorer.exe", _store.Directory);
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("history-open-folder", "開啟歷史資料夾失敗", ex);
            }
        }

        private static Brush ThemeBrush(string key, Color fallback)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is Brush b) return b;
            }
            catch
            {
                // resource lookup is best-effort; the fallback keeps the window readable
            }
            return new SolidColorBrush(fallback);
        }
    }
}
