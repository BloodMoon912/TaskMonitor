using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern.Common.IconKeys;
using iNKORE.UI.WPF.Modern.Controls;

namespace task_monitor
{
    /// <summary>
    /// The settings window — a WinUI-look shell (iNKORE modern window style + Mica)
    /// whose left pane is an iNKORE NavigationView, one item per 类别 (Win11 Settings
    /// style; PaneDisplayMode Auto — labeled ≥1008 DIP, icon-compact 641–1007, minimal
    /// hamburger below): 通用 (开机自启动 / 采样间隔 / 检查更新 / 更新源), 外观 (主题 / 靠左显示 — an
    /// expander whose item is 靠左位置, or a plain card where that item does not exist,
    /// i.e. on the classical Win10 taskbar — plus 紧凑模式, 显示分割线, 项目顺序 (the shared
    /// taskbar/floating drag-to-reorder list), and 悬浮模式 with its five sub-cards), 采样项目 (one expander per metric, the toggle in
    /// its content area enabling/disabling that metric's sampling; 磁盘's and GPU's
    /// expanders also hold 显示方式 + the specific-device picker, GPU's additionally the
    /// 任务栏显示 (使用率/温度/两者) trio with 温度来源 and 温度单位, 网络's the 适配器
    /// picker, the 公网 IP lookup switch and the Clash/Mihomo integration (switch +
    /// endpoint + 测试连接); plus the section-wide 合并相同程序 card); 关于 lives at the
    /// pane's bottom-left (FooterMenuItems) and is NOT a card page — a centred hero
    /// (logo/name/version pill), the app's own live CPU waveform (push-fed by
    /// <see cref="RefreshAbout"/> off App's SnapshotChanged pump — no timer here), two
    /// plain link buttons and the copyright line. Each 类别
    /// is its own TouchDragScrollViewer page, switched by VISIBILITY in
    /// <see cref="NavView_SelectionChanged"/> — NOT Frame navigation, so the window
    /// keeps one namescope: every control/handler pair (and the ctor's initial-state
    /// push into all of them, visible page or not) works exactly as the old single
    /// scroll page did, and each page keeps its own scroll position across switches.
    /// Opened from the taskbar overlay's right-click menu (App keeps at most one
    /// instance). Every card is live: initial values + change callbacks arrive via the
    /// ctor (App owns settings.yaml, the scheduled task, the app theme and the taskbar
    /// overlay).
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly Action<bool, bool> _placementChanged;
        private readonly Action<IReadOnlyList<TaskbarDisplaySelection>> _displaySelectionChanged;
        private readonly Action<bool> _autoStartChanged;
        private readonly Action<int> _themeChanged;           // 0=跟随系统 1=浅色 2=深色
        private readonly Action<bool> _compactChanged;        // 紧凑模式 (the item pitch)
        private readonly Action<bool> _showSeparatorsChanged; // 显示分割线 (taskbar + floating)
        private readonly Action<IReadOnlyList<int>> _itemOrderChanged;
        private readonly List<MetricOrderItem> _itemOrderItems = new List<MetricOrderItem>();
        private bool _populatingItemOrder;   // suppresses the toggles we create ourselves
        private MetricOrderItem _dragItem;
        private Point _dragStart;
        private readonly Action<bool> _floatingChanged;       // 悬浮模式 (the whole form flips)
        private readonly Action<bool> _floatingTopmostChanged; // 悬浮模式 → 置顶显示
        private readonly Action<bool> _floatingOutOfBoundsChanged; // 悬浮模式 → 允许超出屏幕边界
        private readonly Action<bool> _floatingEdgeHideChanged; // 悬浮模式 → 贴边隐藏
        private readonly Action<bool> _floatingFullscreenHideChanged; // 悬浮模式 → 全屏时隐藏
        private readonly Action<double> _floatingOpacityChanged; // 悬浮模式 → 透明度 (0.2–1.0)
        private readonly Action<int> _intervalChanged;        // ms: 500 / 1000 / 2000
        private readonly Action<int, bool> _samplingChanged;  // (overlay hit slot 0–4, enabled)
        private readonly Action<bool> _mergeSamePathChanged;  // 合并相同程序
        private readonly Action<int, int> _diskDisplayChanged; // (mode 0=平均 1=最高 2=特定, PhysicalDrive index)
        private readonly Action<int, int> _gpuDisplayChanged;  // (mode 0=平均 1=最高 2=特定, "GPU N" number)
        private readonly Action<int, int, bool> _gpuTaskbarContentChanged; // (content 0=使用率 1=温度 2=两者, 温度来源 -1=最高, 华氏度)
        private readonly Action<string, string> _netAdapterChanged; // (adapter Id or "" = 自动, display name)
        private readonly Action<bool> _publicIpLookupChanged;       // 公网 IP lookup
        private readonly Action<bool, string, string> _clashApiChanged; // (enabled, controller address or "" = default, API secret)
        // Reads SamplingController's latest snapshot — the About page's live
        // CPU waveform; the same single-source push model as the detail windows.
        private readonly Func<SystemSnapshot> _snapshotProvider;
        // 500ms single-shot debounce for the two Clash text boxes — a yaml write +
        // sampler retarget must not fire on every keystroke.
        private DispatcherTimer _clashDebounce;
        // Guards the event handlers while the ctor pushes the initial state into the
        // controls (setting IsOn / SelectedIndex fires Toggled / SelectionChanged).
        private bool _loaded;
        // 靠左显示's host: the expander on Win11, the plain card on the classical (Win10)
        // taskbar (see the ctor) — only the one actually shown gets enabled/disabled.
        private readonly Control _onLeftHost;

        private sealed class MetricOrderItem
        {
            /// <summary>Slots this row controls. One for most metrics; TWO for a TILE — 使用率 and
            /// 溫度 of the same device share one taskbar cell (one title, two values), so they are
            /// also one row here: one switch, one drag.</summary>
            public int[] Slots { get; set; }
            public string Name { get; set; }
            /// <summary>Whether the row currently contributes slots to the taskbar order. Kept on
            /// the item so <see cref="CurrentItemOrder"/> can flatten on-tiles only.</summary>
            public bool On { get; set; }
        }

        /// <summary>The taskbar cell a slot belongs to — a TILE for 使用率+溫度 of one device, a
        /// single slot for everything else. 項目順序 lists tiles, never raw slots.</summary>
        private static int[] TileOf(int slot)
        {
            switch (slot)
            {
                case 0:
                case 5: return new[] { 0, 5 };   // CPU 使用率 + CPU 溫度
                case 3:
                case 6: return new[] { 3, 6 };   // GPU 使用率 + GPU 溫度
                default: return new[] { slot };
            }
        }

        /// <param name="overlayOnLeft">Effective 靠左显示 state (yaml null = the left default).</param>
        /// <param name="snapToStart">Current overlaySnapToStart value (left-side anchor).</param>
        /// <param name="placementChanged">(onLeft, snapToStart) reported on every user change.</param>
        /// <param name="autoStartOn">Whether the logon scheduled task currently exists.</param>
        /// <param name="autoStartChanged">Reported with the requested on/off; on a schtasks
        /// failure App calls <see cref="SyncAutoStart"/> to snap the toggle back.</param>
        /// <param name="themeIndex">0=跟随系统 (yaml null) 1=浅色 2=深色.</param>
        /// <param name="compactLayout">Current 紧凑模式 state (yaml null = the off default).</param>
        /// <param name="compactLayoutChanged">Reported with the requested on/off — applied
        /// live (the overlay re-lays out and resizes at once; both forms follow).</param>
        /// <param name="showSeparators">Current 显示分割线 state (yaml null = the off default).</param>
        /// <param name="showSeparatorsChanged">Reported with the requested on/off — redraws
        /// both overlay forms without changing their layout.</param>
        /// <param name="itemOrder">Current slot order, shared by the taskbar and floating forms.</param>
        /// <param name="itemOrderChanged">Reported once after a drag is dropped or the order is reset.</param>
        /// <param name="floatingMode">Current 悬浮模式 state (yaml null = the off default).</param>
        /// <param name="floatingChanged">Reported with the requested on/off — App rebuilds the
        /// overlay window in its other form (taskbar child ⟷ desktop widget).</param>
        /// <param name="floatingTopmost">Current 置顶显示 state (yaml null = the off default);
        /// floating form only.</param>
        /// <param name="floatingTopmostChanged">Reported with the requested on/off — applied
        /// live (no rebuild).</param>
        /// <param name="floatingEdgeHide">Current 贴边隐藏 state (yaml null = the off
        /// default); floating form only.</param>
        /// <param name="floatingEdgeHideChanged">Reported with the requested on/off — applied
        /// live (the taskbar thread re-derives the dock from the home position).</param>
        /// <param name="floatingFullscreenHide">Current 全屏时隐藏 state (yaml null = the on
        /// default); floating form only.</param>
        /// <param name="floatingFullscreenHideChanged">Reported with the requested on/off —
        /// applied live (the taskbar thread's fullscreen probe runs at once, then per
        /// tick).</param>
        /// <param name="floatingOpacity">Current 透明度 as 0.2–1.0 (yaml null = 1.0, the
        /// default); floating form only.</param>
        /// <param name="floatingOpacityChanged">Reported with the new 0.2–1.0 opacity on every
        /// slider step — App re-tints the widget live and persists debounced.</param>
        /// <param name="intervalMs">Current sampling interval (500/1000/2000).</param>
        /// <param name="intervalChanged">Reported with the new interval in ms.</param>
        /// <param name="samplingMask">Current per-metric sampling mask (SystemSampler.Mask*
        /// bits, in overlay hit-slot order).</param>
        /// <param name="samplingChanged">Reported with (slot, enabled) on every 采样 toggle.</param>
        /// <param name="mergeSamePath">Current 合并相同程序 state (yaml null = the off default).</param>
        /// <param name="diskDisplayIndex">Current 磁盘显示方式 combo index: 0=所有磁盘平均
        /// (yaml null) 1=最高利用率 2=特定磁盘.</param>
        /// <param name="pickedDiskIndex">The PhysicalDrive index the 特定磁盘 picker shows
        /// selected (yaml null = disk 0).</param>
        /// <param name="disks">The currently present physical disks (from the latest
        /// snapshot; null/empty when disk sampling is off) — the picker's items.</param>
        /// <param name="diskDisplayChanged">Reported with (modeIndex, PhysicalDrive index)
        /// on every 显示方式 / 特定磁盘 change.</param>
        /// <param name="gpuDisplayIndex">Current GPU 显示方式 combo index: 0=所有 GPU 平均
        /// 1=最高利用率 (yaml null, the default) 2=特定 GPU.</param>
        /// <param name="pickedGpuIndex">The "GPU N" number the 特定 GPU picker shows selected
        /// (yaml null = GPU 0).</param>
        /// <param name="gpus">The currently present GPU adapters (from the latest snapshot;
        /// null/empty when GPU sampling is off) — the picker's items.</param>
        /// <param name="gpuDisplayChanged">Reported with (modeIndex, "GPU N" number) on
        /// every 显示方式 / 特定 GPU change.</param>
        /// <param name="gpuContentIndex">0=使用率 (yaml null, the default) 1=温度
        /// 2=使用率+温度 — the 任务栏显示 combo.</param>
        /// <param name="pickedGpuTempIndex">The 温度来源 pick: -1 = 最高温度 (yaml null,
        /// the default), ≥0 = that "GPU N" number.</param>
        /// <param name="gpuTempUnitIndex">0=摄氏度 (yaml null, the default) 1=华氏度.</param>
        /// <param name="gpuTaskbarContentChanged">Reported with (contentIndex, 温度来源
        /// pick, 华氏度) on every 任务栏显示 / 温度来源 / 温度单位 change.</param>
        /// <param name="netAdapterId">The pinned adapter's NetworkInterface.Id (GUID);
        /// null/empty = 自动.</param>
        /// <param name="netAdapterName">The pinned adapter's display name, for the
        /// （未连接） placeholder when it's absent.</param>
        /// <param name="adapters">Every non-loopback adapter (Id + "Description (Name)"
        /// label), enumerated by App on settings open — the combo's items.</param>
        /// <param name="netAdapterChanged">Reported with (adapter Id or "" = 自动, its
        /// display name) on every 适配器 change.</param>
        /// <param name="publicIpLookupOn">The 公网 IP lookup switch state (yaml null = the
        /// on default).</param>
        /// <param name="publicIpLookupChanged">Reported with the requested on/off — takes
        /// effect live (the poll thread drops its cached address and stops BOTH the HTTP
        /// lookups and the 公网延迟 ICMP probe).</param>
        /// <param name="clashEnabled">The Clash/Mihomo integration switch state (yaml
        /// null = the on default).</param>
        /// <param name="clashApiAddress">The Clash/Mihomo controller address
        /// (host:port); null/empty = the 127.0.0.1:9090 default.</param>
        /// <param name="clashApiSecret">The controller's API secret; null/empty = none.</param>
        /// <param name="clashApiChanged">Reported with (enabled, address or "" = default,
        /// secret) — the switch reports immediately, text edits debounced 500ms.</param>
        /// <param name="snapshotProvider">Reads TaskbarWindow's LatestSnapshot (App's
        /// lambda) — the About page's live CPU waveform.</param>
        // internal: the signature mentions internal sampler types — App (same assembly) is
        // the only caller; the window is never instantiated from XAML.
        internal SettingsWindow(bool overlayOnLeft, bool snapToStart, Action<bool, bool> placementChanged,
            bool snapToStartSupported,
            IReadOnlyList<TaskbarDisplayInfo> displays,
            IReadOnlyList<TaskbarDisplaySelection> selectedDisplays,
            Action<IReadOnlyList<TaskbarDisplaySelection>> displaySelectionChanged,
            bool autoStartOn, Action<bool> autoStartChanged,
            int themeIndex, Action<int> themeChanged,
            bool compactLayout, Action<bool> compactLayoutChanged,
            bool showSeparators, Action<bool> showSeparatorsChanged,
            IReadOnlyList<int> itemOrder, Action<IReadOnlyList<int>> itemOrderChanged,
            bool floatingMode, Action<bool> floatingChanged,
            bool floatingTopmost, Action<bool> floatingTopmostChanged,
            bool floatingAllowOutOfBounds, Action<bool> floatingAllowOutOfBoundsChanged,
            bool floatingEdgeHide, Action<bool> floatingEdgeHideChanged,
            bool floatingFullscreenHide, Action<bool> floatingFullscreenHideChanged,
            double floatingOpacity, Action<double> floatingOpacityChanged,
            int intervalMs, Action<int> intervalChanged,
            int samplingMask, Action<int, bool> samplingChanged,
            bool mergeSamePath, Action<bool> mergeSamePathChanged,
            int diskDisplayIndex, int pickedDiskIndex, IReadOnlyList<DiskInfo> disks,
            Action<int, int> diskDisplayChanged,
            int gpuDisplayIndex, int pickedGpuIndex, IReadOnlyList<GpuInfo> gpus,
            Action<int, int> gpuDisplayChanged,
            int gpuContentIndex, int pickedGpuTempIndex, int gpuTempUnitIndex,
            Action<int, int, bool> gpuTaskbarContentChanged,
            string netAdapterId, string netAdapterName,
            IReadOnlyList<(string Id, string Label)> adapters,
            Action<string, string> netAdapterChanged,
            bool publicIpLookupOn, Action<bool> publicIpLookupChanged,
            bool clashEnabled, string clashApiAddress, string clashApiSecret,
            Action<bool, string, string> clashApiChanged,
            Func<SystemSnapshot> snapshotProvider)
        {
            InitializeComponent();
            // i18n: the settings shell is built once per open; translate after its visual tree
            // exists (a language switch closes this window and the next open is translated).
            Loaded += (s, e) => Loc.ApplyOnce(this);
            Loaded += (s, e) => ApplyUiScale();
            _placementChanged = placementChanged;
            _displaySelectionChanged = displaySelectionChanged;
            _autoStartChanged = autoStartChanged;
            _themeChanged = themeChanged;
            _compactChanged = compactLayoutChanged;
            _showSeparatorsChanged = showSeparatorsChanged;
            _itemOrderChanged = itemOrderChanged;
            _floatingChanged = floatingChanged;
            _floatingTopmostChanged = floatingTopmostChanged;
            _floatingOutOfBoundsChanged = floatingAllowOutOfBoundsChanged;
            _floatingEdgeHideChanged = floatingEdgeHideChanged;
            _floatingFullscreenHideChanged = floatingFullscreenHideChanged;
            _floatingOpacityChanged = floatingOpacityChanged;
            _intervalChanged = intervalChanged;
            _samplingChanged = samplingChanged;
            _mergeSamePathChanged = mergeSamePathChanged;
            _diskDisplayChanged = diskDisplayChanged;
            _gpuDisplayChanged = gpuDisplayChanged;
            _gpuTaskbarContentChanged = gpuTaskbarContentChanged;
            _netAdapterChanged = netAdapterChanged;
            _publicIpLookupChanged = publicIpLookupChanged;
            _clashApiChanged = clashApiChanged;
            _snapshotProvider = snapshotProvider;

            OnLeftSwitch.IsOn = overlayOnLeft;
            AnchorCombo.SelectedIndex = snapToStart ? 1 : 0;
            AnchorCombo.IsEnabled = overlayOnLeft;   // the anchor only matters while 靠左 is on
            // Classical (Win10) taskbar: 靠左 has a single spot (the task-buttons band's
            // end next to Start), so 靠左位置 does not exist and the expander would offer
            // a chevron opening onto nothing — 靠左显示 renders as a plain card instead
            // (OnLeftCard). Its icon and its ToggleSwitch MOVE over, so there is exactly
            // one switch and one piece of state; the expander's two properties are
            // cleared first, or its template would claim the same children the moment it
            // applies.
            _onLeftHost = OnLeftExpander;
            if (!snapToStartSupported)
            {
                var icon = OnLeftExpander.HeaderIcon;
                OnLeftExpander.HeaderIcon = null;
                OnLeftExpander.Content = null;
                OnLeftCard.HeaderIcon = icon;
                OnLeftCard.Content = OnLeftSwitch;
                OnLeftExpander.Visibility = Visibility.Collapsed;
                OnLeftCard.Visibility = Visibility.Visible;
                _onLeftHost = OnLeftCard;
            }
            PopulateDisplayPicker(displays, selectedDisplays);
            AutoStartSwitch.IsOn = autoStartOn;
            ThemeCombo.SelectedIndex = themeIndex < 0 || themeIndex > 2 ? 0 : themeIndex;
            CompactSwitch.IsOn = compactLayout;
            ShowSeparatorsSwitch.IsOn = showSeparators;
            PopulateItemOrder(itemOrder);
            FloatingSwitch.IsOn = floatingMode;
            FloatingTopmostSwitch.IsOn = floatingTopmost;
            // 置顶显示 only means anything in the floating form (the embedded overlay inherits
            // the taskbar's band), so it follows the mode switch — same shape as 靠左位置.
            FloatingTopmostCard.IsEnabled = floatingMode;
            FloatingOutOfBoundsSwitch.IsOn = floatingAllowOutOfBounds;
            FloatingOutOfBoundsCard.IsEnabled = floatingMode;
            // …and so does 贴边隐藏 (the embedded overlay is anchored to the taskbar; it has
            // no edges to dock to).
            FloatingEdgeHideSwitch.IsOn = floatingEdgeHide;
            FloatingEdgeHideCard.IsEnabled = floatingMode;
            // …and so does 全屏时隐藏 (the embedded overlay sits in the taskbar, which a
            // fullscreen app covers on its own).
            FloatingFullscreenHideSwitch.IsOn = floatingFullscreenHide;
            FloatingFullscreenHideCard.IsEnabled = floatingMode;
            // …and so does 透明度 (the embedded overlay must match the opaque taskbar surface).
            // The slider stores whole percents (snapped); the ctor hands App back a 0.2–1.0
            // double. The readout text is set here because the guarded handler skips it.
            FloatingOpacitySlider.Value = Math.Round(Math.Min(1.0, Math.Max(0.2, floatingOpacity)) * 100);
            FloatingOpacityText.Text = $"{FloatingOpacitySlider.Value:0}%";
            FloatingOpacityCard.IsEnabled = floatingMode;
            // …and the two are mutually exclusive: a floating widget is positioned by dragging,
            // not by the taskbar anchors, so 靠左显示/靠左位置 do nothing while it is on.
            _onLeftHost.IsEnabled = !floatingMode;
            DisplayExpander.IsEnabled = !floatingMode;
            IntervalCombo.SelectedIndex = IntervalToIndex(intervalMs);
            CpuSwitch.IsOn = (samplingMask & SystemSampler.MaskCpu) != 0;
            RamSwitch.IsOn = (samplingMask & SystemSampler.MaskRam) != 0;
            DiskSwitch.IsOn = (samplingMask & SystemSampler.MaskDisk) != 0;
            GpuSwitch.IsOn = (samplingMask & SystemSampler.MaskGpu) != 0;
            NetSwitch.IsOn = (samplingMask & SystemSampler.MaskNet) != 0;
            MergeSwitch.IsOn = mergeSamePath;
            DiskDisplayCombo.SelectedIndex = diskDisplayIndex < 0 || diskDisplayIndex > 2 ? 0 : diskDisplayIndex;
            PopulateDiskPicker(disks, pickedDiskIndex);
            DiskPickCombo.IsEnabled = DiskDisplayCombo.SelectedIndex == 2;
            GpuDisplayCombo.SelectedIndex = gpuDisplayIndex < 0 || gpuDisplayIndex > 2 ? 1 : gpuDisplayIndex;
            PopulateGpuPicker(gpus, pickedGpuIndex);
            GpuPickCombo.IsEnabled = GpuDisplayCombo.SelectedIndex == 2;
            // 任务栏显示 + its two temperature sub-cards; the sub-cards are enabled only
            // while a temperature actually shows (the DiskPickCombo pattern).
            GpuContentCombo.SelectedIndex = gpuContentIndex < 0 || gpuContentIndex > 3 ? 0 : gpuContentIndex;
            PopulateGpuTempPicker(gpus, pickedGpuTempIndex);
            GpuTempUnitCombo.SelectedIndex = gpuTempUnitIndex == 1 ? 1 : 0;
            GpuTempPickCombo.IsEnabled = GpuContentCombo.SelectedIndex == 1 || GpuContentCombo.SelectedIndex == 2;
            GpuTempUnitCombo.IsEnabled = GpuContentCombo.SelectedIndex == 1 || GpuContentCombo.SelectedIndex == 2;
            PopulateNetAdapterPicker(adapters, netAdapterId, netAdapterName);
            PublicIpSwitch.IsOn = publicIpLookupOn;
            ClashSwitch.IsOn = clashEnabled;
            ClashAddressBox.Text = clashApiAddress ?? "";
            ClashSecretBox.Text = clashApiSecret ?? "";
            SetClashInputsEnabled(clashEnabled);
            // 版本號前綴原本是寫死的簡體「版本」，繁體/英文介面都會停在簡體；
            // 目錄已有 settings.about.version（"版本 {0}"）帶格式化的 id，改走目錄取字。
            VersionText.Text = string.Format(Loc.T("settings.about.version"), VersionInfo.Current);   // AssemblyInformationalVersion — 与发版 tag 一致
            _loaded = true;
        }

        private void PopulateItemOrder(IReadOnlyList<int> order)
        {
            _populatingItemOrder = true;
            try
            {
                var persisted = order != null
                    ? new List<int>(order)
                    : new List<int>(TaskbarWindow.StandardItemOrder);
                var sequence = TaskbarWindow.NormalizeItemOrder(persisted);
                var cards = new List<object>();
                _itemOrderItems.Clear();

                // One row per TILE (see TileOf): 使用率 and 溫度 of the same device share one taskbar
                // cell, so they are one row here — one switch, one drag, both slots move together.
                // `order` is the list of tiles the bar SHOWS, in bar order, so every row built from
                // it is on; the tiles it does not name are appended below as off rows.
                //
                // The SAME MetricOrderItem instance must sit in _itemOrderItems and in the card's
                // DataContext: the drag handlers resolve the dropped item by reference
                // (_itemOrderItems.IndexOf(source)), so a second instance per card silently makes
                // dragging a no-op.
                foreach (int slot in sequence)
                {
                    int[] tile = TileOf(slot);
                    bool known = false;
                    foreach (var existing in _itemOrderItems)
                        if (existing.Slots[0] == tile[0]) { known = true; break; }
                    if (known) continue;

                    var item = new MetricOrderItem { Slots = tile, Name = TileName(tile), On = true };
                    _itemOrderItems.Add(item);
                    cards.Add(BuildItemOrderCard(item, true));
                }

                // The stored list holds only the SWITCHED-ON tiles, so every tile that is off has no
                // entry in it — and this page is the only place a switch lives, so walk the whole
                // catalogue afterwards and give the remainder an off row.
                foreach (int slot in TaskbarWindow.StandardItemOrder)
                {
                    int[] tile = TileOf(slot);
                    bool known = false;
                    foreach (var existing in _itemOrderItems)
                        if (existing.Slots[0] == tile[0]) { known = true; break; }
                    if (known) continue;

                    var item = new MetricOrderItem { Slots = tile, Name = TileName(tile), On = false };
                    _itemOrderItems.Add(item);
                    cards.Add(BuildItemOrderCard(item, false));
                }

                // SettingsExpander's ItemsRepeater observes replacement, not mutations of the old list.
                ItemOrderExpander.Items = cards;
            }
            finally
            {
                _populatingItemOrder = false;
            }
        }

        /// <summary>One row of 項目順序: drag handle + name + on/off switch. The switch decides
        /// whether the row takes a taskbar cell; the drag order applies to the switched-on rows
        /// (a switched-off row keeps its place in the list but is invisible on the bar).</summary>
        private SettingsCard BuildItemOrderCard(MetricOrderItem item, bool on)
        {
            var handle = new FontIcon { Icon = FluentSystemIcons.ReOrderDotsVertical_20_Regular, FontSize = 16 };
            TouchDragScrollViewer.SetTouchDragExcluded(handle, true);

            var toggle = new ToggleSwitch
            {
                IsOn = on,
                MinWidth = 0,
                OnContent = string.Empty,
                OffContent = string.Empty,
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Toggled += ItemOrderToggle_Toggled;

            var card = new SettingsCard
            {
                Header = item.Name,
                Description = null,
                HeaderIcon = handle,
                Content = toggle,
                DataContext = item,   // the toggle inherits it — one instance, see PopulateItemOrder
                AllowDrop = on,
                Cursor = on ? Cursors.Hand : Cursors.Arrow,
            };
            if (!on)
            {
                card.Opacity = 0.65;   // still clickable (the switch), visibly not on the bar
                return card;
            }
            card.PreviewMouseLeftButtonDown += ItemOrderCard_PreviewMouseLeftButtonDown;
            card.PreviewMouseMove += ItemOrderCard_PreviewMouseMove;
            card.DragEnter += ItemOrderCard_DragEnter;
            card.DragOver += ItemOrderCard_DragOver;
            card.Drop += ItemOrderCard_Drop;
            return card;
        }

        private void ItemOrderToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_populatingItemOrder) return;
            if (!(sender is ToggleSwitch toggle)) return;
            var card = ItemOrderCardFrom(toggle);
            if (!(card?.DataContext is MetricOrderItem item)) return;

            var order = new List<int>(CurrentItemOrder());
            if (toggle.IsOn)
            {
                // A TILE comes back as a unit — 使用率 and 溫度 are one cell, so one switch.
                foreach (int slot in item.Slots)
                    if (!order.Contains(slot)) order.Add(slot);
            }
            else
            {
                order.RemoveAll(s => Array.IndexOf(item.Slots, s) >= 0);
                if (order.Count == 0)
                {
                    // The bar must keep one tile: NormalizeItemOrder keeps an empty list as-is, which
                    // would collapse the overlay to a zero-width window that shows nothing but still
                    // swallows clicks. Snap the switch back instead (the guard stops the re-fire).
                    _populatingItemOrder = true;
                    try { toggle.IsOn = true; }
                    finally { _populatingItemOrder = false; }
                    return;
                }
            }
            PopulateItemOrder(order);
            ReportItemOrder();
        }

        private static string TileName(int[] tile)
        {
            // Ids, not literal text: these titles share their Chinese value with other entries in
            // the catalogue (五个 id 的值都是「内存」), and the text→id reverse lookup deliberately
            // drops values owned by more than one id. Passing text here therefore produced the
            // simplified title in the Traditional Chinese UI.
            switch (tile[0])
            {
                case 0: return "CPU";   // 使用率 + 溫度
                case 1: return Loc.T("overlay.label.ram");
                case 2: return Loc.T("overlay.label.disk");
                case 3: return "GPU";   // 使用率 + 溫度
                case 4: return Loc.T("overlay.label.net");
                case 7: return Loc.T("menu.metric.gpu-vram");
                default: return string.Empty;
            }
        }

        private int[] CurrentItemOrder()
        {
            var order = new List<int>(TaskbarWindow.SlotCount);
            foreach (var item in _itemOrderItems)
            {
                if (!item.On) continue;
                foreach (int slot in item.Slots)
                    if (!order.Contains(slot)) order.Add(slot);
            }
            return order.ToArray();
        }

        private void ReportItemOrder() => _itemOrderChanged?.Invoke(CurrentItemOrder());

        private void ResetItemOrder_Click(object sender, RoutedEventArgs e)
        {
            PopulateItemOrder(null);
            ReportItemOrder();
        }

        private static SettingsCard ItemOrderCardFrom(DependencyObject source)
        {
            while (source != null)
            {
                if (source is SettingsCard card) return card;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }

        private static bool IsItemOrderTouchHandle(DependencyObject source)
        {
            while (source != null && !(source is SettingsCard))
            {
                if (TouchDragScrollViewer.GetTouchDragExcluded(source)) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void ItemOrderCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            _dragItem = e.StylusDevice != null && !IsItemOrderTouchHandle(source)
                ? null
                : ItemOrderCardFrom(source)?.DataContext as MetricOrderItem;
            _dragStart = e.GetPosition(ItemOrderExpander);
        }

        private void ItemOrderCard_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragItem == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragItem = null; return; }
            Vector delta = e.GetPosition(ItemOrderExpander) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var data = new DataObject(typeof(MetricOrderItem), _dragItem);
            try { DragDrop.DoDragDrop(ItemOrderExpander, data, DragDropEffects.Move); }
            finally { _dragItem = null; }
        }

        private void ItemOrderCard_DragEnter(object sender, DragEventArgs e)
            => ItemOrderCard_DragOver(sender, e);

        private void ItemOrderCard_DragOver(object sender, DragEventArgs e)
        {
            bool reorder = e.Data.GetDataPresent(typeof(MetricOrderItem));
            e.Effects = reorder ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void ItemOrderCard_Drop(object sender, DragEventArgs e)
        {
            var source = e.Data.GetData(typeof(MetricOrderItem)) as MetricOrderItem;
            var target = ItemOrderCardFrom(e.OriginalSource as DependencyObject)?.DataContext as MetricOrderItem;
            if (source == null || target == null || ReferenceEquals(source, target)) return;
            int oldIndex = _itemOrderItems.IndexOf(source);
            int newIndex = _itemOrderItems.IndexOf(target);
            if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;
            _itemOrderItems.RemoveAt(oldIndex);
            _itemOrderItems.Insert(newIndex, source);
            int[] order = CurrentItemOrder();
            PopulateItemOrder(order);
            _itemOrderChanged?.Invoke(order);
            e.Handled = true;
        }

        private void PopulateDisplayPicker(IReadOnlyList<TaskbarDisplayInfo> displays,
            IReadOnlyList<TaskbarDisplaySelection> selectedDisplays)
        {
            var cards = new List<object>();
            var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selectedDisplays != null)
            {
                foreach (var selected in selectedDisplays)
                {
                    if (selected != null && !string.IsNullOrWhiteSpace(selected.Id)) selectedIds.Add(selected.Id);
                }
            }

            if (displays != null)
            {
                foreach (var display in displays)
                {
                    if (display == null || string.IsNullOrWhiteSpace(display.Id)) continue;
                    bool selected = selectedDisplays == null ? display.IsPrimary : selectedIds.Contains(display.Id);
                    AddDisplayCard(cards, display.Id, display.Name, display.DeviceName, display.IsPrimary,
                        display.HasTaskbar, selected, false);
                    seen.Add(display.Id);
                }
            }

            if (selectedDisplays != null)
            {
                foreach (var selected in selectedDisplays)
                {
                    if (selected == null || string.IsNullOrWhiteSpace(selected.Id) || seen.Contains(selected.Id)) continue;
                    AddDisplayCard(cards, selected.Id, TaskbarDisplayCatalog.ResolveDisplayName(selected.Id, selected.Name),
                        null, false, false, true, true);
                    seen.Add(selected.Id);
                }
            }
            DisplayExpander.Items = cards;
            if (CountCheckedDisplays() == 0)
            {
                foreach (var item in DisplayExpander.Items)
                {
                    if (item is SettingsCard card && card.Content is CheckBox check && check.IsEnabled)
                    {
                        check.IsChecked = true;
                        break;
                    }
                }
            }
        }

        internal void RefreshDisplays(IReadOnlyList<TaskbarDisplayInfo> displays,
            IReadOnlyList<TaskbarDisplaySelection> selectedDisplays)
        {
            bool wasLoaded = _loaded;
            _loaded = false;
            PopulateDisplayPicker(displays, selectedDisplays);
            DisplayExpander.UpdateLayout();
            _loaded = wasLoaded;
            Logger.Debug($"设置显示器列表已重建：在线={displays?.Count ?? 0}，保存选择={selectedDisplays?.Count ?? 0}");
        }

        private void AddDisplayCard(List<object> cards, string id, string name, string deviceName, bool primary,
            bool hasTaskbar, bool selected, bool offline)
        {
            // 顯示器名本身是系統資料（WMI 設備名），不進目錄；只有「沒有名字」時的占位字串要在地化。
            // 一律在建構時就取目錄譯文：ApplyOnce 在 Loaded 之後才走視覺樹，改到的是已經量測過的
            // 物件，而且這些字全都寫進 Header/Content，晚了就趕不上第一次版面。
            string displayName = string.IsNullOrWhiteSpace(name) ? Loc.T("settings.appearance.displays.item") : name;
            var selection = new TaskbarDisplaySelection
            {
                Id = id,
                Name = displayName
            };
            var check = new CheckBox
            {
                Content = displayName,
                Tag = selection,
                IsChecked = selected,
                IsEnabled = hasTaskbar || selected,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            check.Checked += DisplayCheckChanged;
            check.Unchecked += DisplayCheckChanged;
            string title = displayName;
            if (primary) title += Loc.T("settings.appearance.displays.primary");
            // 「（未连接）」在目錄裡被網路適配器與顯示器的兩個 id 共用同一個簡體值，
            // ByText 反查會判定歧義而失效——所以這裡指定 id，不靠反查。
            if (offline) title += Loc.T("settings.appearance.displays.disconnected");
            string description = offline
                ? Loc.T("settings.appearance.displays.keep-hint")
                : hasTaskbar ? deviceName : Loc.T("settings.appearance.displays.no-taskbar");
            cards.Add(new SettingsCard
            {
                Header = title,
                Description = description,
                Content = check
            });
        }

        private void DisplayCheckChanged(object sender, RoutedEventArgs e)
        {
            if (!_loaded || !(sender is CheckBox changed)) return;
            if (changed.IsChecked != true && CountCheckedDisplays() == 0)
            {
                _loaded = false;
                changed.IsChecked = true;
                _loaded = true;
                return;
            }
            var selected = new List<TaskbarDisplaySelection>();
            foreach (var item in DisplayExpander.Items)
            {
                if (item is SettingsCard card && card.Content is CheckBox check
                    && check.IsChecked == true && check.Tag is TaskbarDisplaySelection display)
                {
                    selected.Add(new TaskbarDisplaySelection { Id = display.Id, Name = display.Name });
                }
            }
            _displaySelectionChanged?.Invoke(selected);
        }

        private int CountCheckedDisplays()
        {
            int count = 0;
            foreach (var item in DisplayExpander.Items)
            {
                if (item is SettingsCard card && card.Content is CheckBox check && check.IsChecked == true) count++;
            }
            return count;
        }

        // The 特定磁盘 picker: one item per present physical disk ("磁盘 0 (C: D:) · model"),
        // Tag = the PhysicalDrive index. A stored pick whose disk is currently absent (or
        // a list that came back empty — disk sampling off) still gets a （未连接） item so
        // the user's choice stays visible instead of silently snapping to another disk.
        private void PopulateDiskPicker(IReadOnlyList<DiskInfo> disks, int selectedIndex)
        {
            int sel = -1;
            if (disks != null)
            {
                foreach (var d in disks)
                {
                    var item = new ComboBoxItem { Content = $"{d.TabTitle} · {d.Name}", Tag = d.Index };
                    DiskPickCombo.Items.Add(item);
                    if (d.Index == selectedIndex) sel = DiskPickCombo.Items.Count - 1;
                }
            }
            if (sel < 0)
            {
                DiskPickCombo.Items.Add(new ComboBoxItem
                {
                    // 目錄已有整句模板（settings.sampling.disk.pick.disconnected），
                    // 不在 C# 裡拼前綴——拼字串就等於把簡體「磁盘」「（未连接）」寫死。
                    Content = string.Format(Loc.T("settings.sampling.disk.pick.disconnected"), selectedIndex),
                    Tag = selectedIndex,
                });
                sel = DiskPickCombo.Items.Count - 1;
            }
            DiskPickCombo.SelectedIndex = sel;
        }

        private int PickedDiskIndex()
            => (DiskPickCombo.SelectedItem as ComboBoxItem)?.Tag is int i ? i : 0;

        // The 特定 GPU picker — same shape as the disk one (a missing stored pick gets a
        // （未连接） placeholder), items are "GPU 0 · NVIDIA …", Tag = the "GPU N" number.
        private void PopulateGpuPicker(IReadOnlyList<GpuInfo> gpus, int selectedIndex)
        {
            int sel = -1;
            if (gpus != null)
            {
                foreach (var g in gpus)
                {
                    var item = new ComboBoxItem { Content = $"{g.TabTitle} · {g.Name}", Tag = g.Index };
                    GpuPickCombo.Items.Add(item);
                    if (g.Index == selectedIndex) sel = GpuPickCombo.Items.Count - 1;
                }
            }
            if (sel < 0)
            {
                GpuPickCombo.Items.Add(new ComboBoxItem
                {
                    // 同磁碟的處理：整句模板來自目錄，C# 只負責塞整數。
                    Content = string.Format(Loc.T("settings.sampling.gpu.pick.disconnected"), selectedIndex),
                    Tag = selectedIndex,
                });
                sel = GpuPickCombo.Items.Count - 1;
            }
            GpuPickCombo.SelectedIndex = sel;
        }

        private int PickedGpuIndex()
            => (GpuPickCombo.SelectedItem as ComboBoxItem)?.Tag is int i ? i : 0;

        // The 温度来源 picker: 最高温度（默认） first (Tag = -1 — the sampler's "hottest
        // adapter" sentinel), then one item per present GPU (Tag = the "GPU N" number) —
        // the same items as the 特定 GPU picker. A stored pick that's currently absent (or
        // GPU sampling off) keeps a （未连接） placeholder, same contract as the other two.
        private void PopulateGpuTempPicker(IReadOnlyList<GpuInfo> gpus, int selectedIndex)
        {
            // 「（默认）」是簡體字面，且這個項目是預設選中項——ApplyOnce 之後才改 Content 的話，
            // ComboBox 收合時畫的 SelectionBoxItem 仍是舊的簡體快照，使用者看到的就是沒翻的那一個。
            GpuTempPickCombo.Items.Add(new ComboBoxItem { Content = Loc.T("settings.sampling.gpu.tempsource.max"), Tag = -1 });
            int sel = 0;
            if (gpus != null)
            {
                foreach (var g in gpus)
                {
                    var item = new ComboBoxItem { Content = $"{g.TabTitle} · {g.Name}", Tag = g.Index };
                    GpuTempPickCombo.Items.Add(item);
                    if (g.Index == selectedIndex) sel = GpuTempPickCombo.Items.Count - 1;
                }
            }
            if (sel == 0 && selectedIndex >= 0)
            {
                GpuTempPickCombo.Items.Add(new ComboBoxItem
                {
                    Content = string.Format(Loc.T("settings.sampling.gpu.pick.disconnected"), selectedIndex),
                    Tag = selectedIndex,
                });
                sel = GpuTempPickCombo.Items.Count - 1;
            }
            GpuTempPickCombo.SelectedIndex = sel;
        }

        private int PickedGpuTempIndex()
            => (GpuTempPickCombo.SelectedItem as ComboBoxItem)?.Tag is int i ? i : -1;

        // The 网络 适配器 combo: "自动（默认）" first (Tag = ""), then one item per
        // enumerated non-loopback adapter (Tag = its Id). A stored pick whose adapter is
        // absent keeps a （未连接） placeholder — same contract as the disk/GPU pickers.
        private void PopulateNetAdapterPicker(IReadOnlyList<(string Id, string Label)> adapters,
            string selectedId, string selectedName)
        {
            NetAdapterCombo.Items.Add(new ComboBoxItem { Content = Loc.T("settings.sampling.net.adapter.auto"), Tag = "" });
            int sel = 0;
            if (adapters != null)
            {
                foreach (var (id, label) in adapters)
                {
                    NetAdapterCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                    if (!string.IsNullOrEmpty(selectedId) && id == selectedId)
                        sel = NetAdapterCombo.Items.Count - 1;
                }
            }
            if (sel == 0 && !string.IsNullOrEmpty(selectedId))
            {
                NetAdapterCombo.Items.Add(new ComboBoxItem
                {
                    // 後綴指定 id（理由同顯示器）；名稱本身也走目錄，未插卡/停用時讀
                    // 「未知适配器」的對譯。
                    Content = (string.IsNullOrEmpty(selectedName) ? Loc.T("common.unknown-adapter") : selectedName)
                        + Loc.T("settings.sampling.net.adapter.disconnected"),
                    Tag = selectedId,
                });
                sel = NetAdapterCombo.Items.Count - 1;
            }
            NetAdapterCombo.SelectedIndex = sel;
        }

        private static int IntervalToIndex(int ms) => ms <= 500 ? 0 : ms >= 2000 ? 2 : 1;
        private static int IndexToInterval(int index) => index == 0 ? 500 : index == 2 ? 2000 : 1000;

        /// <summary>App pushes the real task state back after a failed schtasks call,
        /// without re-firing the change callback.</summary>
        public void SyncAutoStart(bool on)
        {
            _loaded = false;
            AutoStartSwitch.IsOn = on;
            _loaded = true;
        }

        /// <summary>悬浮模式 was flipped from the taskbar right-click menu: push the state
        /// back (incl. the sub-card enablement the Toggled handler maintains) without
        /// re-firing the change callback.</summary>
        public void SyncFloatingMode(bool on)
        {
            _loaded = false;
            FloatingSwitch.IsOn = on;
            FloatingTopmostCard.IsEnabled = on;
            FloatingOutOfBoundsCard.IsEnabled = on;
            FloatingEdgeHideCard.IsEnabled = on;
            FloatingFullscreenHideCard.IsEnabled = on;
            FloatingOpacityCard.IsEnabled = on;
            _onLeftHost.IsEnabled = !on;
            DisplayExpander.IsEnabled = !on;
            _loaded = true;
        }

        /// <summary>置顶显示 was flipped from the taskbar right-click menu, without
        /// re-firing the change callback.</summary>
        public void SyncFloatingTopmost(bool on)
        {
            _loaded = false;
            FloatingTopmostSwitch.IsOn = on;
            _loaded = true;
        }

        /// <summary>贴边隐藏 was flipped from the taskbar right-click menu, without
        /// re-firing the change callback.</summary>
        public void SyncFloatingEdgeHide(bool on)
        {
            _loaded = false;
            FloatingEdgeHideSwitch.IsOn = on;
            _loaded = true;
        }

        /// <summary>全屏时隐藏 was flipped from the taskbar right-click menu, without
        /// re-firing the change callback.</summary>
        public void SyncFloatingFullscreenHide(bool on)
        {
            _loaded = false;
            FloatingFullscreenHideSwitch.IsOn = on;
            _loaded = true;
        }

        /// <summary>紧凑模式 was flipped from the taskbar right-click menu, without
        /// re-firing the change callback.</summary>
        public void SyncCompactLayout(bool on)
        {
            _loaded = false;
            CompactSwitch.IsOn = on;
            _loaded = true;
        }

        private void CompactSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _compactChanged?.Invoke(CompactSwitch.IsOn);
        }

        private void ShowSeparatorsSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _showSeparatorsChanged?.Invoke(ShowSeparatorsSwitch.IsOn);
        }

        private void OnLeftSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            AnchorCombo.IsEnabled = OnLeftSwitch.IsOn;
            _placementChanged?.Invoke(OnLeftSwitch.IsOn, AnchorCombo.SelectedIndex == 1);
        }

        private void AnchorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _placementChanged?.Invoke(OnLeftSwitch.IsOn, AnchorCombo.SelectedIndex == 1);
        }

        private void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _autoStartChanged?.Invoke(AutoStartSwitch.IsOn);
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _themeChanged?.Invoke(ThemeCombo.SelectedIndex);
        }

        private void FloatingSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            FloatingTopmostCard.IsEnabled = FloatingSwitch.IsOn;
            FloatingOutOfBoundsCard.IsEnabled = FloatingSwitch.IsOn;
            FloatingEdgeHideCard.IsEnabled = FloatingSwitch.IsOn;
            FloatingFullscreenHideCard.IsEnabled = FloatingSwitch.IsOn;
            FloatingOpacityCard.IsEnabled = FloatingSwitch.IsOn;
            _onLeftHost.IsEnabled = !FloatingSwitch.IsOn;   // anchors don't apply to a dragged widget
            DisplayExpander.IsEnabled = !FloatingSwitch.IsOn;
            _floatingChanged?.Invoke(FloatingSwitch.IsOn);
        }

        private void FloatingTopmostSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _floatingTopmostChanged?.Invoke(FloatingTopmostSwitch.IsOn);
        }

        private void FloatingOutOfBoundsSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _floatingOutOfBoundsChanged?.Invoke(FloatingOutOfBoundsSwitch.IsOn);
        }

        private void FloatingEdgeHideSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _floatingEdgeHideChanged?.Invoke(FloatingEdgeHideSwitch.IsOn);
        }

        private void FloatingFullscreenHideSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _floatingFullscreenHideChanged?.Invoke(FloatingFullscreenHideSwitch.IsOn);
        }

        // 悬浮模式 → 透明度 slider: fires per step while dragging, and each step is reported
        // at once — the widget must re-tint live under the thumb, so no debounce here. App
        // applies immediately and defers only the settings.yaml write.
        private void FloatingOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_loaded) return;
            FloatingOpacityText.Text = $"{FloatingOpacitySlider.Value:0}%";
            _floatingOpacityChanged?.Invoke(FloatingOpacitySlider.Value / 100.0);
        }

        private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _intervalChanged?.Invoke(IndexToInterval(IntervalCombo.SelectedIndex));
        }

        // One handler for all five 采样 toggles — the switch's Tag carries the overlay
        // hit slot (0=CPU 1=内存 2=磁盘 3=GPU 4=网络). The last enabled metric cannot
        // be turned off; restore it without re-firing this handler.
        private void MetricSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            if (sender is ToggleSwitch sw && int.TryParse(sw.Tag as string, out int slot))
            {
                if (!sw.IsOn && !CpuSwitch.IsOn && !RamSwitch.IsOn && !DiskSwitch.IsOn
                    && !GpuSwitch.IsOn && !NetSwitch.IsOn)
                {
                    _loaded = false;
                    sw.IsOn = true;
                    _loaded = true;
                    return;
                }
                _samplingChanged?.Invoke(slot, sw.IsOn);
            }
        }

        private void MergeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _mergeSamePathChanged?.Invoke(MergeSwitch.IsOn);
        }

        // 磁盘 → 显示方式 combo: 0=所有磁盘平均 1=最高利用率 2=特定磁盘 (the picker below
        // is enabled only in 特定磁盘 mode — the AnchorCombo pattern).
        private void DiskDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            DiskPickCombo.IsEnabled = DiskDisplayCombo.SelectedIndex == 2;
            _diskDisplayChanged?.Invoke(DiskDisplayCombo.SelectedIndex, PickedDiskIndex());
        }

        private void DiskPickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            // Re-picking only matters while 特定磁盘 is the active mode.
            if (DiskDisplayCombo.SelectedIndex != 2) return;
            _diskDisplayChanged?.Invoke(2, PickedDiskIndex());
        }

        // GPU → 显示方式 combo: 0=所有 GPU 平均 1=最高利用率 2=特定 GPU (the GPU default is
        // 最高, unlike the disk's 平均; the picker below is enabled only in 特定 GPU mode).
        private void GpuDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            GpuPickCombo.IsEnabled = GpuDisplayCombo.SelectedIndex == 2;
            _gpuDisplayChanged?.Invoke(GpuDisplayCombo.SelectedIndex, PickedGpuIndex());
        }

        private void GpuPickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            // Re-picking only matters while 特定 GPU is the active mode.
            if (GpuDisplayCombo.SelectedIndex != 2) return;
            _gpuDisplayChanged?.Invoke(2, PickedGpuIndex());
        }

        // GPU → 任务栏显示 combo: 0=使用率（默认） 1=温度 2=使用率 + 温度 3=显存 — 温度/显存
        // replace the utilization row, 两者 packs BOTH rows. The two temperature sub-cards
        // below are enabled only while a TEMPERATURE shows (1 or 2), never for 显存.
        private void GpuContentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            bool tempShows = GpuContentCombo.SelectedIndex == 1 || GpuContentCombo.SelectedIndex == 2;
            GpuTempPickCombo.IsEnabled = tempShows;
            GpuTempUnitCombo.IsEnabled = tempShows;
            _gpuTaskbarContentChanged?.Invoke(GpuContentCombo.SelectedIndex, PickedGpuTempIndex(),
                GpuTempUnitCombo.SelectedIndex == 1);
        }

        // GPU → 温度来源 combo: the "-1"-tagged first item is 最高温度; every other item's
        // Tag is the "GPU N" number. Re-picking only matters while a temperature shows.
        private void GpuTempPickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            if (GpuContentCombo.SelectedIndex != 1 && GpuContentCombo.SelectedIndex != 2) return;
            _gpuTaskbarContentChanged?.Invoke(GpuContentCombo.SelectedIndex, PickedGpuTempIndex(),
                GpuTempUnitCombo.SelectedIndex == 1);
        }

        // GPU → 温度单位 combo: 0=摄氏度（默认） 1=华氏度 — applies to the taskbar row AND
        // the detail popup's temperatures. Only matters while a temperature shows.
        private void GpuTempUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            if (GpuContentCombo.SelectedIndex != 1 && GpuContentCombo.SelectedIndex != 2) return;
            _gpuTaskbarContentChanged?.Invoke(GpuContentCombo.SelectedIndex, PickedGpuTempIndex(),
                GpuTempUnitCombo.SelectedIndex == 1);
        }

        // 网络 → 适配器 combo: the ""-tagged first item is 自动; every other item's Tag is
        // the adapter's NetworkInterface.Id. The display name rides along so the picker can
        // show a （未连接） placeholder for a missing pick later.
        private void NetAdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            var item = NetAdapterCombo.SelectedItem as ComboBoxItem;
            string id = item?.Tag as string ?? "";
            _netAdapterChanged?.Invoke(id, id.Length == 0 ? null : item.Content as string);
        }

        // The 公网 IP switch: a discrete action, so it reports immediately (same as the
        // Clash switch) — NetInfoSampler's poll thread stops BOTH the HTTP lookups and
        // the 公网延迟 ICMP probe, and drops the cached address on off.
        private void PublicIpSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _publicIpLookupChanged?.Invoke(PublicIpSwitch.IsOn);
        }

        // The Clash/Mihomo switch: a discrete action, so it reports immediately (the text
        // boxes debounce instead), and the endpoint inputs follow its state.
        private void ClashSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            SetClashInputsEnabled(ClashSwitch.IsOn);
            _clashApiChanged?.Invoke(ClashSwitch.IsOn, ClashAddressBox.Text.Trim(), ClashSecretBox.Text.Trim());
        }

        private void SetClashInputsEnabled(bool on)
        {
            ClashAddressBox.IsEnabled = on;
            ClashSecretBox.IsEnabled = on;
            ClashTestButton.IsEnabled = on;
            if (!on) ClashTestResult.Text = "";
        }

        // Both Clash/Mihomo boxes funnel here per keystroke; the actual report is
        // debounced 500ms so a yaml write + sampler retarget doesn't fire per character.
        private void ClashApiBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded) return;
            if (_clashDebounce == null)
            {
                _clashDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _clashDebounce.Tick += (s2, e2) =>
                {
                    _clashDebounce.Stop();
                    _clashApiChanged?.Invoke(ClashSwitch.IsOn, ClashAddressBox.Text.Trim(), ClashSecretBox.Text.Trim());
                };
            }
            _clashDebounce.Stop();
            _clashDebounce.Start();
        }

        // 测试连接: probes the CURRENT box contents (empty address = the 127.0.0.1:9090
        // default — the same value the poller would use) via GET /version, off the UI
        // thread. The result line is colored per outcome (iNKORE scheme brushes).
        private async void ClashTestButton_Click(object sender, RoutedEventArgs e)
        {
            string address = ClashAddressBox.Text.Trim();
            string secret = ClashSecretBox.Text.Trim();
            ClashTestButton.IsEnabled = false;
            ClashTestResult.Text = Loc.T("settings.sampling.net.clash.testing");
            ClashTestResult.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
            var (ok, detail) = await Task.Run(() => ClashSampler.TestConnection(address, secret));
            ClashTestResult.Text = detail;
            ClashTestResult.Foreground = (Brush)FindResource(ok
                ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
            ClashTestButton.IsEnabled = true;
        }

        // 关于 page links point at THIS fork's repo (user request 2026-11-18: 「關於」改成我的).
        private const string GitHubUrl = "https://github.com/BloodMoon912/TaskMonitor";
        private const string AcknowledgementsUrl = "https://github.com/BloodMoon912/TaskMonitor#参考--致谢--开源许可";

        // The nav pane's 类别 switch. Only ever flips the four pages' Visibility —
        // deliberately without the _loaded guard: it carries no change callback to
        /// <summary>字體大小 (right-click menu → 字體大小): a LayoutTransform zoom plus a matching
        /// window size. The shell is a fixed 1040×720 frame, so scaling only the content would
        /// clip it — both the transform and Width/Height have to move together.</summary>
        private void ApplyUiScale()
        {
            double scale = Typography.Scale;
            if (scale <= 0 || Math.Abs(scale - 1.0) < 0.01) return;
            if (Content is FrameworkElement root)
                root.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
            Width = 1040 * scale;
            Height = 720 * scale;
        }

        // App, and it must also run for the selection IsSelected="True" establishes
        // in XAML (the pages' XAML defaults already match, so the early fire during
        // InitializeComponent is a no-op there — the null check only covers it firing
        // before the pages themselves are parsed).
        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (GeneralPage == null) return;
            object item = args.SelectedItem;
            GeneralPage.Visibility = item == NavGeneral ? Visibility.Visible : Visibility.Collapsed;
            AppearancePage.Visibility = item == NavAppearance ? Visibility.Visible : Visibility.Collapsed;
            SamplingPage.Visibility = item == NavSampling ? Visibility.Visible : Visibility.Collapsed;
            AboutPage.Visibility = item == NavAbout ? Visibility.Visible : Visibility.Collapsed;
        }

        // The About page's live CPU waveform. Driven by App's SnapshotChanged pump (the
        // same push model as the detail windows — this window holds no timer): a no-op
        // unless the About page is actually showing, and the whole band collapses while
        // CPU sampling is off (the snapshot's history then stays empty — a flat line at
        // zero would be a lie). Points map oldest→newest left→right, 2px inset so the
        // 100% peak never clips.
        internal void RefreshAbout()
        {
            if (AboutPage.Visibility != Visibility.Visible) return;
            var s = _snapshotProvider?.Invoke();
            var history = s?.CpuHistory;
            double w = LiveWave.ActualWidth, h = LiveWave.ActualHeight;
            if (history == null || history.Length < 2 || w < 10 || h < 10)
            {
                LiveBand.Visibility = Visibility.Collapsed;
                return;
            }
            LiveBand.Visibility = Visibility.Visible;
            LiveCpuText.Text = $"{s.CpuPercent:0}%";

            int n = history.Length;
            var pts = new Point[n];
            double step = w / (n - 1);
            for (int i = 0; i < n; i++)
            {
                double v = history[i];
                if (v < 0) v = 0; else if (v > 100) v = 100;
                pts[i] = new Point(i * step, h - 2 - v / 100.0 * (h - 4));
            }
            LiveLine.Points = new PointCollection(pts);

            // The soft area under the curve (the logo's chart motif) — one StreamGeometry,
            // frozen so the render thread can share it.
            var fill = new StreamGeometry();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                ctx.PolyLineTo(pts, true, false);
                ctx.LineTo(new Point(w, h), true, false);
            }
            fill.Freeze();
            LiveFill.Data = fill;

            Canvas.SetLeft(LiveDot, pts[n - 1].X - LiveDot.Width / 2);
            Canvas.SetTop(LiveDot, pts[n - 1].Y - LiveDot.Height / 2);
        }

        private void GitHubCard_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

        private void AcknowledgementsCard_Click(object sender, RoutedEventArgs e) => OpenUrl(AcknowledgementsUrl);

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { System.Diagnostics.Process.Start(url); }
            catch { /* no browser handler / malformed url — ignore */ }
        }
    }
}
