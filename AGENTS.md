# AGENTS.md

This file provides guidance to AI coding agents when working with code in this repository.

**This file is a MAP, not the manual** — rules and pointers only. The long unwindings live in
`docs/` (loaded only when you touch that area) and at the code site. See
[Maintaining this file](#maintaining-this-file) before adding anything here.

| When you touch… | Read |
|---|---|
| taskbar geometry, boot/explorer-restart, D2D/DXGI, samplers, theme, process rows, 悬浮模式/贴边隐藏/全屏时隐藏, crash & stall forensics | `docs/gotchas.md` |
| anything spanning files: thread ownership, cross-thread contract, detail/pin model, startup & elevation, project layout | `docs/architecture.md` |
| a 设置 item's wiring to its sampler | `docs/settings-plumbing.md` |
| any of the nine scroll views | `docs/scroll-stack.md` |

## 工程化原则

你应具备软件工程化的能力来完成这个项目：所有工作不单是为了完成某个需求，而是为了持久地完成一个工程。如果你的代码不够工程化，后续也会消耗更多精力让你重构。当前代码中不够工程化的部分要主动提出，而不是参照之前的风格继续编写。

## What this is

A Windows taskbar widget: a self-drawn overlay (default CPU/内存, 磁盘/GPU, 网络 ↑/↓; 外观 → 项目顺序
reorders and switches metrics on) that pops up a fluent acrylic detail window on click. WPF on .NET Framework 4.8. **No main window**; the only persistent UI is that overlay —
optionally detached into a free-floating card (设置 → 外观 → 悬浮模式, gotchas §36). Life is kept by
`ShutdownMode="OnExplicitShutdown"`; exit via the overlay's right-click menu.

## Build & run

```shell
dotnet build -c Debug          # only toolchain here — no VS MSBuild / nuget.exe
bin\Debug\net48\task_monitor.exe
```

SDK-style csproj, `net48`, `UseWPF=true`. No tests.

**A live exe locks the output**, so `dotnet build` fails at the copy step with `error
MSB3021`/`MSB3027` naming `task_monitor.exe (PID)` — that is NOT a compile failure. Stop the
instance first, and always use the sentinel, never `taskkill` (why: `docs/architecture.md`):

```shell
touch bin/Debug/net48/shutdown.sentinel   # 1s tick notices it and exits gracefully
dotnet build -c Debug
bin/Debug/net48/task_monitor.exe &
```

The app always runs **elevated and self-managed**; relaunching from an unelevated shell pops a UAC
prompt on the secure desktop — that one step is the user's. Elevation gate + mutex: `docs/architecture.md`.

**UI testing is the USER's job — all of it, not just screenshots.** Driving the real windows (UI Automation, clicks/drags), screen capture with pixel analysis, and every judgement about how it looks or feels. The agent's loop: implement → `dotnet build` → launch → verify from the
**log file and `settings.yaml`** (plus code reading) → hand the user a short checklist → wait for
their feedback. Do NOT write UI-test harnesses of your own; when something is only observable
through the UI, say so and ask.

**Git is READ-ONLY for the agent unless the user asks for a commit in that very turn.** No `git
commit` / `--amend` / `push` / `tag` / `reset` without an explicit request — do the work, leave it in
the working tree, and say it is uncommitted. "Finish the change" (including the docs-sync rule under
[Maintaining this file](#maintaining-this-file)) means *editing* the files, never committing them.
**Commit messages are always English** (subject and body), never Chinese — the history is read by
people who do not read Chinese.

**Editing files here: the shell is Windows PowerShell 5.1 and it is NOT text-safe.**
`Get-Content`/`Set-Content`/`Out-File`/`>` default to ANSI (GBK here) or UTF-16, so routing a source
file through them **double-encodes it and eats characters** — on 2026-09-22 it destroyed 125 lines of
`TaskbarWindow.cs`. **Read and write text ONLY with the UTF-8-safe file tools** (or .NET with explicit
UTF-8 — `[IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)` is byte-exact, the cmdlet defaults are
not); keep the shell for processes and paths. Snapshot before any bulk rewrite. A diff against
`git HEAD` reveals damage to **HEAD's** lines only, so "not in the archive" never proves "we never
wrote it".

## 发布 / CI/CD

**`origin` = GitHub，是唯一的真仓库；CNB 镜像与数字签名都可选**——`vars.CNB_REPO` / `secrets.CNB_TOKEN`
为空时 workflow 整段跳过，fork 的推送不会因缺 secret 变红。机制、凭据优先级与失败模式：
[`docs/release.md`](docs/release.md)。

发布 = 改 csproj 的 **两个** 属性（`<Version>` 四段数字给 AssemblyVersion/FileVersion；
`<InformationalVersion>` 带 `-BM` 后缀，关于页与产物名读它）+ `CHANGELOG.md` 写
`## v<InformationalVersion>` 段落（缺/空直接 fail），再 `git tag v<InformationalVersion> && git
push origin <tag>`：校验 tag（接受两者之一）→ `tools/pack.ps1`（Release 构建 + `dist/*-x64.exe`
+ `.sha256`）→ 有 `SIGN_PFX_BASE64` 才 `tools/sign.ps1 -Require` 签名并 verify → `gh release create`。
**签名材料永不进版控**，只经 secret 注入。CNB 是 Linux 节点跑不了 net48 WPF，构建放 GitHub
Windows runner；`publish-cnb-release.ps1` 三个坑（勿回退）：CNB API 必须显式
`Accept: application/json`；PUT/确认走 curl.exe（`System.Uri` 会解码 %2F 导致 500）；保持
**UTF-8 BOM**（PS 5.1 按 GBK 误读）。

## Architecture (the part that spans files)

**One process: WPF UI + one sampling STA + N overlay STAs.** `SamplingController` owns the only
`SystemSampler`; `TaskbarOverlayManager` creates one native `TaskbarWindow` per selected display,
each with its own `RenderState`, DirectN pipeline and `SetParent` target. Layout has 8 hit SLOTS (0 CPU使用率 1 記憶體
2 磁碟 3 GPU使用率 4 網路 5 CPU溫度 6 GPU溫度 7 GPU顯存) folded onto 5 detail columns;
floating mode stays a SINGLE window.
The WPF thread owns detail/settings/menu windows.

**Single source of truth:** controller publishes one snapshot; overlays redraw it independently,
popups read the controller. Sampling and per-overlay heartbeats/diagnostics must all stay live.

**Cross-thread contract** (Taskbar→UI callbacks, UI→taskbar `Set*`/`Request*` methods) and the
pinned-mode invariants: `docs/architecture.md`; the 设置→sampler chains: `docs/settings-plumbing.md`.

## Project layout (layered, under src/)

All source is under `src/`, grouped **by layer** (top-level `App.*` · `Logger` · `AppSettings` ·
`VersionInfo` · `StartupTask`, then `Sampling/` · `Interop/` · `UI/` with
`Common/` `Details/` `Charts/` `Settings/`); annotated tree: `docs/architecture.md`. Root-level rest
is build plumbing (csproj, slnx, bin/, obj/, FodyWeavers.xml); `assets/` holds `logo.*` + the README
banner. One `task_monitor` namespace — folders are physical only, so XAML `x:Class`/`xmlns:local`
never reference a folder.

**App.xaml under `src/`** needs the explicit `<Page Remove>` + `<ApplicationDefinition Include>`
pair in the csproj — it must stay, or the build loses `Main`.

**Adding a metric:** new `XxxSampler` (Sampling/) + `XxxDetailView` (UI/Details/), wired into
`SystemSampler.Sample` / `DetailWindow.ShowColumn` — plus a `Mask*` bit + settings.yaml key + a 采样
card for its sampling switch (SettingsExpander only if it has sub-settings — CPU/内存 are plain
cards), and one row in `docs/settings-plumbing.md`.

## Fluent 2 / iNKORE.UI.WPF.Modern design guidelines

All WPF UI targets **Fluent 2**; before adding any control/layout/icon/interaction, **first check
whether iNKORE.UI.WPF.Modern ships one**. Local checkouts (use these): `D:\ai-ref\UI.WPF.Modern`
(source + samples), `D:\ai-ref\Documentation`. NuGet is **0.10.2.1** and matches — beware drift.
Quirks, documented at each site:
TabControl's strip "+" and per-tab "×" default VISIBLE; plain switchers use
`TabControlPivotStyle`/`TabItemPivotStyle` (the default is an opaque TabView lookalike, wrong on
acrylic); the Pivot hit-test flaw needs `PivotNavButtonFix.Apply`.

**Window families — never mix iNKORE's `UseModernWindowStyle`/`SystemBackdropType` with
FluentWpfCore on the same window:** DetailWindow = borderless FluentWpfCore acrylic (Win10
carve-out: no material, `AllowsTransparency` layered window + self-drawn rounded tint card — §40);
SettingsWindow = iNKORE modern window + Mica; ConsentDialog = iNKORE modern
window (plain). iNKORE also styles the taskbar right-click menu
(standard `ContextMenu` via `SetResourceReference`, **by key**), not `MenuFlyout` (can't place at the
cursor; NREs unless owned).

## Non-obvious gotchas

The "don't regress" index. **Full unwinding of every entry — the incident, the measurements, the
mechanism — is [`docs/gotchas.md`](docs/gotchas.md), same numbering**; each rule's canonical
statement is a comment at the code site named there. Companions:
[`settings-plumbing.md`](docs/settings-plumbing.md), [`scroll-stack.md`](docs/scroll-stack.md).

**Taskbar / boot**

1. **Classical band is ours to restore** — `RestoreMinWindow` before ANY re-measure of our own, in `WM_DESTROY`, on an orientation flip, and on exit.
2. **Boot/explorer-restart:** `Start(target)` polls its own taskbar; ANY return → the manager re-resolves and re-enters after 2s; cancelled entries never revive; the WndProc delegate stays process-lifetime `static readonly`.
3. **Nothing escapes `WndProc`** — per-message try/catch → `CrashReporter`; an escaping managed exception is `0xC000041D`.
4. **Global crashes** = log file FIRST, then a code-only `CrashDialog`; a fatal report blocks the dying thread on it.
5. **`Logger`** — `logs/task_monitor-<date>.log`, 7-day retention, never throws; hot per-tick paths never log (`WarnOnce`). Keep the line truthful.
6. **The overlay must never steal focus** — full no-activate set, incl. `SWP_NOACTIVATE` on every `SetWindowPos`; sole exception: 悬浮模式 with 置顶显示 OFF (§36).
7. **`SetWindowPos(HWND_TOPMOST)` no-ops when not foreground** — activate first; `EnsureTopmost` verifies the exstyle bit.
8. **Acrylic** — FluentWpfCore `UseWindowComposition=True` on every WPF window, never hand-rolled Accent P/Invoke (carve-out: DetailWindow on Win10, §40); the native floating overlay has NO material (§36); the menu host needs `WS_EX_TOOLWINDOW`.
44. **竖直任务栏与任务栏族无关** — orientation comes ONLY from the target rect's aspect, never `classical`; always use the column grid + tray/top anchor. Thickness selects two-line strip / one-line column / multi-column (`ComputeVerticalLayout` against BAND width, never the rounded window).
45. **`SetParent` 嵌入任务栏偶发 err=87（跨线程瞬态）** — retry in place (5×250ms), judge by `GetAncestor(GA_PARENT)`; a failed embed DESTROYS the round (never fall through top-level in parent coords → 屏幕左上角滞留); drain the teardown's WM_QUIT; the tick holds the embed invariant.
46. **多显示器 = one SamplingController + N overlays** — persist monitor interface IDs, never indices/HWNDs; each selected target has its own STA/RenderState/taskbar and cancellable lifecycle, never its own sampler. DWrite = per-HWND ISOLATED factory rooted in RenderState; failed Start aborts cleanly. Detail data comes from controller, placement from CLICKED overlay; floating stays singleton. Full rules: gotchas §46.
47. **Native overlay STAs are PerMonitorV2; resize must reach the HWND** — compare X/Y + width/height; floating derives DPI/height independently of the primary band; docked hide/peek keeps home DPI (real drag/undock follows live); update region in the same pass. Back-buffer-only resize clips the card.

**Samplers / metrics**

9. **`NetSampler` never enumerates NICs per tick** (~275ms = ~97% of idle CPU).
10. **Live CPU speed = PDH `% Processor Performance` × base clock**, not `CallNtPowerInformation`.
11. **Per-process CPU/RAM/disk = ONE `NtQuerySystemInformation` walk**; the disk column is the 24H2+ trailer, NOT SRUM.
12. **Disk/GPU replicate Taskmgr exactly** (`IOCTL_DISK_PERFORMANCE` / DXCore COM); disk handles are opened and closed per query, never held. On Win10 fetch the base `IDXCoreAdapter` + all four attribute passes; capability gates — never OS-version checks — drive the fallbacks.
13. **Per-process GPU% is PDH, not DXCore** — encoding and aggregation at `ProcessGpuSampler.cs`; no elevation, unlike SRUM.
14. **Process-row tooltip = view-owned `Popup` on `MouseMove`**, never a row `ToolTip`.
15. **Per-process net = SRUM real-time API** (why we elevate). `SamplingController` owns the only registration; never unregister on overlay teardown. Failed SRUM/poller cleanup retries ≤3, then BLOCKS rebuild — never accumulate registrations or retire the callback root early.
16. **Wi-Fi `wlanapi` is on-demand only** — never on the per-tick path.
17. **Memory breakdown = `GlobalMemoryStatusEx` + NTQSI class-2/class-80**, not `GetPerformanceInfo`.
18. **Charts are hand-drawn WPF** — hover = `HitTest` + a real `Popup`.
19. **Scroll stack** — `Style="{DynamicResource {x:Type ScrollViewer}}"` on every scroller, or it silently falls back to the Aero bar.
20. **Idle GC at event points only** — no timer/burst, `WaitForPendingFinalizers`, or working-set trim (`TrimMemory`: 30 s limit, one plain `GC.Collect`).
21. **开机自启动 = a logon scheduled task, never the Run key** (XML-registered, not schtasks switches).
49. **CPU 温度感測包按需載入** — LHM 型別只允許出現在 `Sampling/Sensors/LhmWorker.cs`；別處引用（或放進欄位初始/啟動路徑）就會在啟動時解析內嵌組件，按需設計歸零（gotchas §49）。
50. **工作列欄寬 = 量測文字，不是常數** — `GroupWidths` 只是 floor；儲備最寬值（`100%`/`212°F`/`999.9 MB/s`）而非即時值（否則每秒重設視窗重嵌），量完由 tick resize、視窗寬取 ceil；同一裝置的使用率+溫度合成一個圖磚（一個標題、兩列數值、整欄可按、不畫中線，gotchas §50–51）。

**UI / theme / settings**

22. **深浅色 = `ThemeManager.Current.ApplicationTheme`**; never `GetActualTheme(window)` on DetailWindow; the native overlay tracks the system theme separately.
23. **Never `<StaticResource …/>`-alias a theme brush in `Window.Resources`** — it freezes the startup scheme.
24. **采样间隔 is runtime state** — rate samplers normalize over REAL elapsed time, never "1 tick = 1s".
25. **Sampling mask ≠ pinned-window click mask** — unpinning must not re-enable a sampling-disabled slot (toggle posts `WM_APP_SET_METRICS`; enable primes baselines).
26. **合并相同程序 merges BEFORE the top-8 cut**; svchost.exe is exempt.
27. **svchost→服务命名 runs AFTER the merge**; the hover 描述 is resolved lazily and cached per service.
28. **磁盘/GPU 显示方式** — the mode picks only the headline; every device is queried each tick, and a mode/index change clears the history.
29. **网络适配器** — the virtual-adapter filter does NOT apply to an explicit pin; absent/down → 自动, re-probed every 30 ticks.
30. **公网 IP off kills ALL public traffic** (lookups + the ICMP probe); the LAN gateway ping is unaffected.
31. **Clash/Mihomo** — `req.Proxy = null`; rows appended standalone and exempt from merging; `null` endpoint = poller asleep.
32. **DISPOSE back-buffer wrappers before `ResizeBuffers`** — they live on `RenderState`, never in locals.
33. **版本号是两个属性** — `<Version>` 四段数字（AssemblyVersion/FileVersion）、`<InformationalVersion>` 带 `-BM` 后缀（关于页 + 产物名）；tag 等于两者之一即可，发布细节见 `docs/release.md`。启动时的更新检测已整套移除（gotchas §33）。
34. **net48's `Run.Text` is not a dependency property** — a `{Binding}` on a `Run` throws at startup; set named Runs in code.

**Floating widget / rendering / forensics**

35. **A lost D3D device is recoverable, never a crash** — only the raw `EndDraw`/`Present`/`ResizeBuffers` HRESULTs can tell (DirectN's helpers lose the code as E_FAIL): latch `RenderState.DeviceLost`, rebuild in place, release DComp with `Marshal.FinalReleaseComObject`.
36. **悬浮模式始终单实例** — no `SetParent`; self-drawn rounded card, no material/frame; the drag moves the pair, the dodge separates it. Home uses independent validity (negative coords valid); ordinary clamp runs only when out-of-bounds is OFF. Read gotchas §36 first.
37. **A native crash is invisible to managed handlers** — `CrashTrace`'s VEH is the only record; don't add a WndProc handler for one. Crashless stalls → per-thread `CheckOverlayHealth`, trace slots and `SystemSampler.Timed`.
38. **贴边隐藏 — home owns dock + clip, slide owns position** — ≤8 DIP (左/右/上); home point selects monitor; clip region to home work area, unclip while pressed; re-hide only when the cursor is outside the visible clip. Ordinary clamp depends on out-of-bounds; slides/held buttons suspend reposition; slides never write home.
39. **全屏时隐藏 — probe owns VISIBILITY, never position** — per-tick, DWM extended-frame bounds (MAXIMIZED never counts); `SW_HIDE`/`SW_SHOWNOACTIVATE` only; tick, heartbeat and sampling keep running; held button defers hiding; dodge suppressed while hidden.
40. **Win10 的 DetailWindow 没有亚克力** — `MaterialMode=None` + `AllowsTransparency` 分层窗口 + `RootBorder` 自绘圆角卡片；`ShowColumn` 末尾补 `InvalidateVisual` + `SWP_FRAMECHANGED`。
41. **Prewarm 永不抛出，`_stopping` 时跳过；启动时在 mutex 之后清残留 sentinel**。
42. **经典任务栏(Win10)下覆盖层自画不透明底色** — 被 `WS_CLIPCHILDREN` 冻住的残影擦不掉、只能遮盖（`CardBrush` alpha 恒 1 全幅填充）；底色每 tick 采样 `SampleTaskbarBackdropColor`（3-tick 稳定期）；Win11 保持全透明。
43. **窗口死后必须退出消息循环；僵尸要自愈** — 裸 `WM_NCDESTROY` 也 teardown+quit；foreign calls 走有界 worker（`SetParent` inline）；entry 清理 worker/trace；watchdog 5s 唤醒、45s 重启。
48. **UAC/锁屏后 WPF 不渲染 = 多窗口 BitmapCache 缺陷** — 首个窗口前设 `ShadowAssist.UseBitmapCache=false`；触屏临时缓存也走此开关，禁止单独恢复。

## Useful external references

- iNKORE.UI.WPF.Modern: local checkouts above; github.com/iNKORE-NET/UI.WPF.Modern.
- **SRU real-time API:** reversed from `Taskmgr.exe`; `C:\Users\l\SRUM-RealTime-API.md` is canonical
  for `ProcessNetSampler` / `SrumInterop`.

## Maintaining this file

**Keep it in sync with the code — update it in the same change, not later.** Stale guidance is worse
than none; fix or delete any claim that no longer matches the code.

**每次更新本文件前，先检查它的体积，整个文件控制在 20 KB 以内。** 加入内容后将超过 20 KB 时，
要想清楚是否真的需要加入；必须加入的话，精简语序，并考虑是否需要优化（压缩或迁移到 docs/）已有内容。

**Route each fact to exactly one place, and point at it elsewhere** (a fact held in two prose copies
WILL drift — it already happened once, to the 合并相同程序 wording):

| The fact is… | Goes in |
|---|---|
| a rule an agent must not break, expressible in 1–2 lines | the gotcha index above |
| the reasoning, incident history, or multi-step mechanism behind that rule | `docs/gotchas.md` |
| the cross-file design (threads, contract, detail/pin model, startup, layout) | `docs/architecture.md` |
| how a 设置 item reaches its sampler | `docs/settings-plumbing.md` |
| the design of one module used from many places | a topic doc in `docs/` (e.g. `scroll-stack.md`) |
| derivation, calibration constants, offsets, reversed layouts | **the code site's own comment** — link to it, never copy it |
| what the code already says plainly (signature, field name, obvious branch) | **nowhere** |

New gotcha → a numbered line here + a matching `## N.` section in `docs/gotchas.md`.
