using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// Disk detail panel: a header with the live headline utilization (mean / max across
    /// disks or one specific disk's — 设置 → 采样 → 磁盘 → 显示方式), a 60s history area
    /// chart, one tab per physical disk (model name,
    /// SSD/HDD type, utilization, read/write speeds, average response time — Task Manager's
    /// own metrics, see <see cref="DiskSampler"/>), and a per-process list of each process's
    /// real-time I/O read/write rate (not a percentage). Self-contained — owns its chart
    /// theming, the hover tooltip, and the icon cache. Created fresh per popup open;
    /// refreshed on every sampling tick by <see cref="DetailWindow"/>.
    /// </summary>
    public partial class DiskDetailView : UserControl, IDetailView
    {
        // Hover tooltip + the per-exe icon cache — shared implementations, see UI/Common. The
        // tip state lives in the host so the per-second Refresh can update a shown value
        // without waiting for the mouse to move (what re-fires MouseMove).
        private readonly ChartTipHost _tip = new ChartTipHost();
        private readonly ProcessIconCache _icons = new ProcessIconCache();

        // Current sampling cadence (from each snapshot) — the "N 秒前" history tooltip
        // scales its tick offset by it (settings 采样间隔).
        private int _intervalMs = 1000;

        // Header slot the shell parks its pin toggle in (IDetailView).
        public ContentControl PinSlot => PinSlotHost;

        public DiskDetailView(bool dark)
        {
            InitializeComponent();

            // Same pivot-style flaw as the GPU view: the invisible Previous scroll button
            // covers the first tab's left 20px and swallows clicks (see helper).
            PivotNavButtonFix.Apply(DiskTabs);

            // Tab switching has to be observable: when the user reports "clicking the second
            // disk does nothing", this line says whether the click reached the TabControl
            // (selection changed) or was swallowed by the pivot template's nav buttons.
            DiskTabs.SelectionChanged += (s, e) =>
            {
                if (DiskTabs.SelectedItem is DiskInfo d)
                {
                    LastSelectedDiskIndex = d.Index;   // sticky across popup reopens
                    Logger.Debug($"磁碟分页切换 → {d.TabTitle}（第 {DiskTabs.SelectedIndex + 1} 块）");
                }
            };

            ApplyTheme(dark);

            _tip.Attach(DiskHistoryChart, DiskHistoryChart.HitTest, HistoryTip, HistoryTipText, i =>
            {
                var values = DiskHistoryChart.Values;
                if (i < 0 || values == null || i >= values.Count) return "";
                var v = values[i];
                return v is null ? "" : $"{HistoryTimeFormatter.Ago(59 - i, _intervalMs)} · {v:F0}%";
            });

            ProcessListTip.Attach(DiskProcessList, ProcessTip, ProcessTipDesc, ProcessTipPath);
            ProcessKillMenu.Attach(DiskProcessList);
        }

        // Static (not per-instance): the popup is recreated on every open, and the user
        // expects the disk they were looking at to still be selected next time.
        private static int LastSelectedDiskIndex = -1;

        void IDetailView.Refresh(SystemSnapshot s)
        {
            _intervalMs = s.SampleIntervalMs;   // the history tooltip scales tick offsets by it
            DiskHeaderPercentText.Text = $"{s.DiskPercent:F0}%";

            // 60-tick history, null-padded during warm-up (same shape as CPU/RAM history).
            const int HistoryCapacity = 60;
            int histCount = s.DiskHistory?.Length ?? 0;
            var historyValues = new double?[HistoryCapacity];
            for (int i = 0; i < HistoryCapacity; i++)
                historyValues[i] = i < HistoryCapacity - histCount ? (double?)null : s.DiskHistory[i - (HistoryCapacity - histCount)];
            DiskHistoryChart.Values = historyValues;

            // Keep the open tooltip in lockstep with this per-second refresh.
            _tip.Refresh();

            // Per-disk tabs: the DiskInfo objects are long-lived and update themselves via
            // INotifyPropertyChanged, so ItemsSource is only (re)set when the disk SET
            // changes (hot-plug). The selection is preserved BY IDENTITY, not by index: a
            // removed drive shifts every later index, and an index restore would silently
            // jump the user to another disk (or back to the first tab) instead of keeping
            // the one they picked.
            if (TabsNeedRebind(s.Disks))
            {
                // Selection restore, in priority order:
                //   1. the currently selected disk's INDEX (stable across the sampler's
                //      30-tick object recreation — an identity match never survives it),
                //   2. the tab index while the disk count is unchanged (SelectedItem can be
                //      null for one tick around a rebind),
                //   3. the disk this user looked at last in a previous popup,
                //   4. the first disk.
                int keepIndex = DiskTabs.SelectedItem is DiskInfo keep ? keep.Index : -1;
                if (keepIndex < 0 && DiskTabs.SelectedIndex >= 0 && DiskTabs.SelectedIndex < s.Disks.Count)
                    keepIndex = s.Disks[DiskTabs.SelectedIndex].Index;
                if (keepIndex < 0) keepIndex = LastSelectedDiskIndex;

                DiskTabs.ItemsSource = s.Disks;
                int idx = s.Disks.FindIndex(d => d.Index == keepIndex);
                DiskTabs.SelectedIndex = idx >= 0 ? idx : 0;
                Logger.Debug($"磁碟分页重绑：{s.Disks.Count} 块，保留选中={(idx >= 0 ? s.Disks[idx].TabTitle : "无（回第一块）")}");
            }

            // Per-process list: resolve each row's icon (cache-backed) and bind.
            if (s.TopDiskProcesses != null)
            {
                foreach (var p in s.TopDiskProcesses)
                    p.Icon = _icons.Resolve(p.ExePath);
                DiskProcessList.ItemsSource = s.TopDiskProcesses;
            }
        }

        // True when the published disk list no longer matches what the tabs show (first
        // bind, or a disk added/removed). Item identity comparison: same objects = same set.
        private bool TabsNeedRebind(List<DiskInfo> disks)
        {
            if (disks == null) return false;
            if (DiskTabs.ItemsSource is not List<DiskInfo> cur || cur.Count != disks.Count) return true;
            for (int i = 0; i < cur.Count; i++)
                if (!ReferenceEquals(cur[i], disks[i])) return true;
            return false;
        }

        // ---------- Theming: tooltip surfaces + paint the chart with the iNKORE accent ----------
        public void ApplyTheme(bool dark)
        {
            HistoryTipBorder.Background = dark
                ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
                : new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

            ApplyChartTheme();
        }

        private void ApplyChartTheme()
        {
            DiskHistoryChart.AccentColor = ChartPalette.Accent;
            DiskHistoryChart.GridColor = ChartPalette.Grid;
            // Theme-adaptive outer frame (see CpuDetailView): the DP default is a fixed
            // 40%-black, which reads as a dark hairline on the dark acrylic card.
            DiskHistoryChart.FrameColor = ChartPalette.FaintBase(0x66);
            DiskHistoryChart.FillOpacity = 48.0 / 255.0;

            // A theme switch re-instantiates the TabControl's template, which drops the
            // pivot hit-test fix (see PivotNavButtonFix).
            PivotNavButtonFix.Reapply(DiskTabs);
        }
    }
}
