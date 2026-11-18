using System;
using System.Threading;
using System.Threading.Tasks;

namespace task_monitor
{
    /// <summary>One published hardware sensor (功耗/電壓/風扇/頻率/主板溫度/磁碟溫度…).</summary>
    internal struct SensorEntry
    {
        public string Group;    // "CPU" / "主板" / "磁盘" / "电池" / "GPU"
        public string Name;     // vendor sensor name, or a Chinese label for the common CPU ones
        public string Unit;     // "W" / "V" / "RPM" / "MHz" / "°C" / "%"
        public double Value;

        public string Display => $"{Value:0.#} {Unit}";
    }

    /// <summary>One pack read: the CPU temperature headline plus every other sensor.</summary>
    internal struct SensorReading
    {
        public bool Valid;
        public double CpuCelsius;          // NaN when the CPU exposes no usable temperature
        public string CpuSource;           // "LHM(Tctl/Tdie)" etc.
        public SensorEntry[] Entries;
    }

    /// <summary>The seam the pack talks to. Implemented by <see cref="LhmWorker"/>; declared
    /// here so the pack can hold it without pulling LibreHardwareMonitorLib into memory.</summary>
    internal interface ISensorWorker : IDisposable
    {
        bool Open();
        SensorReading Read();
    }

    /// <summary>
    /// On-demand hardware sensor pack: owns the LibreHardwareMonitor worker and runs it only
    /// while something actually wants a reading.
    ///
    /// Lifecycle (the cost model this exists for):
    ///   idle        no demand → no worker, no assemblies loaded, zero cost
    ///   warming     first demand → `new LhmWorker()` (Costura resolves the embedded DLLs,
    ///               ~9 ms) then Computer.Open() on this (sampling) thread, ~110 ms once
    ///   active      a read every 2 s while the demand keeps being refreshed
    ///   stopped     no demand for the grace window → Computer.Close(), worker dropped
    ///
    /// The demand is a TTL, not a boolean: UI callers refresh it on every tick they need the
    /// values (a detail window's per-tick Refresh). When the window closes the refreshes stop,
    /// the TTL lapses, and the pack unloads itself — nothing has to remember to switch it off.
    /// A failed open is remembered for <see cref="RetryOpenMs"/> so a machine without PawnIO
    /// never retries on the tick path.
    /// </summary>
    internal sealed class SensorPack
    {
        private const int ReadIntervalMs = 2000;      // hardware sensors have inertia; 2 s is plenty
        private const int DemandTtlMs = 120000;       // a consumer that stops refreshing unloads after 2 min
        private const int RetryOpenMs = 60000;        // a failed open is retried at most once a minute

        private volatile ISensorWorker _worker;
        private volatile bool _opening;
        private long _lastReadTick;
        private long _retryOpenAfterTick;
        private SensorReading _reading;
        // int, not long: a 32-bit field cannot tear under a racy read on any platform, and
        // the ~24.9-day TickCount wrap only ever ends a demand window early (harmless).
        private int _demandUntilTick;

        /// <summary>设置 → 采样项目 → CPU → 温度 (null = on). Off means never load the pack.</summary>
        public volatile bool Enabled = true;

        /// <summary>Refreshes the demand window. Safe from any thread (UI included) — the
        /// sampling thread only ever READS the field.</summary>
        public void Demand()
        {
            Volatile.Write(ref _demandUntilTick, Environment.TickCount + DemandTtlMs);
        }

        /// <summary>True when the pack currently holds an open LHM worker (log/telemetry).</summary>
        public bool IsActive => _worker != null;

        /// <summary>Called once per sampling tick. Returns the latest reading (possibly stale
        /// up to <see cref="ReadIntervalMs"/>) and handles open/close/backoff.</summary>
        public SensorReading Tick()
        {
            if (!Enabled)
            {
                CloseWorker();
                return default(SensorReading);
            }

            int now = Environment.TickCount;
            int demandUntil = Volatile.Read(ref _demandUntilTick);
            if (demandUntil < now)
            {
                // No consumer any more: release the hardware handles and the object graph.
                CloseWorker();
                return default(SensorReading);
            }

            if (_worker == null)
            {
                // The open is ASYNC on purpose: Computer.Open() costs 0.1–0.5 s with storage
                // enumeration and must never stall a sampling tick (the tick would be logged
                // as a slow step and the other four metrics would arrive late). The first
                // one or two ticks after a demand simply carry no sensor values.
                if (_opening) return default(SensorReading);
                if (_retryOpenAfterTick > now) return default(SensorReading);
                _opening = true;
                Task.Run(OpenWorkerInBackground);
                return default(SensorReading);
            }

            if (now - _lastReadTick < ReadIntervalMs && _reading.Valid) return _reading;

            _lastReadTick = now;
            try
            {
                _reading = _worker.Read();
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("sensor-pack-read", "硬件感測讀取失敗——暫停 1 分鐘後重試", ex);
                _reading = default(SensorReading);
                CloseWorker();
                _retryOpenAfterTick = (long)now + RetryOpenMs;
            }
            return _reading;
        }

        // The ONLY place that names LhmWorker: JIT-ing this method is what loads
        // LibreHardwareMonitorLib, so it must stay off the startup/tick idle path. Runs on a
        // pool thread (see the async open above), never on the sampling STA.
        /// <summary>
        /// One INFO line describing what the pack actually exposes (groups, counts, and every
        /// fan sensor by name). 風扇 is the row users ask about most and the one a board may
        /// simply not publish: Gigabyte SuperIO often has no fan channel, and AM5 CPUs report
        /// none through LHM either. Without this line "no fan row" cannot be distinguished from
        /// "we failed to route it", so it states what WAS enumerated.
        /// </summary>
        private static void LogPackContents(ISensorWorker worker)
        {
            try
            {
                SensorReading? maybe = worker?.Read();
                if (!maybe.HasValue) { Logger.Info("感測包內容：未回讀到任何值"); return; }
                var reading = maybe.Value;
                if (reading.Entries == null || reading.Entries.Length == 0)
                {
                    Logger.Info("感測包內容：0 個感測器");
                    return;
                }

                var groups = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
                var fans = new System.Collections.Generic.List<string>();
                foreach (var e in reading.Entries)
                {
                    string group = e.Group ?? "?";
                    groups.TryGetValue(group, out int n);
                    groups[group] = n + 1;
                    if (!string.IsNullOrEmpty(e.Unit) && e.Unit.IndexOf("RPM", StringComparison.OrdinalIgnoreCase) >= 0)
                        fans.Add(group + "/" + e.Name);
                }

                var sb = new System.Text.StringBuilder();
                foreach (var kv in groups) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
                Logger.Info($"感測包內容：{reading.Entries.Length} 個感測器；{sb.ToString().Trim()}");
                Logger.Info(fans.Count > 0
                    ? $"感測包風扇：{fans.Count} 個（{string.Join("、", fans)}）"
                    : "感測包風扇：0 個——本機主板/CPU 未透過 LibreHardwareMonitor 回報風扇轉速");
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("sensor-pack-contents", "感測包內容記錄失敗", ex);
            }
        }

        private void OpenWorkerInBackground()
        {
            try
            {
                var worker = new LhmWorker();
                if (!worker.Open())
                {
                    worker.Dispose();
                    Logger.WarnOnce("sensor-pack-open", "硬件感測包无法打开（缺少 PawnIO 驱动或传感器不支持）");
                    _retryOpenAfterTick = (long)Environment.TickCount + RetryOpenMs;
                    return;
                }
                _lastReadTick = 0;
                _reading = default(SensorReading);
                _worker = worker;   // volatile publish — Tick() starts using it on the next tick
                Logger.Info("硬件感測包已按需载入（LibreHardwareMonitor + PawnIO）");
                LogPackContents(worker);
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("sensor-pack-open-ex", "硬件感測包载入异常——将降级为无可显示感测值", ex);
                _retryOpenAfterTick = (long)Environment.TickCount + RetryOpenMs;
            }
            finally
            {
                _opening = false;
            }
        }

        private void CloseWorker()
        {
            if (_worker == null) return;
            _worker.Dispose();
            _worker = null;
            _reading = default(SensorReading);
            _lastReadTick = 0;
            Logger.Info("硬件感測包已释放（无使用需求）");
        }
    }
}
