using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace task_monitor
{
    /// <summary>
    /// The ONLY type in the app that touches LibreHardwareMonitor types. Costura resolves
    /// the embedded assembly on the first instruction that loads this type — i.e. the first
    /// time <see cref="SensorPack"/> decides it has a demand — so the pack costs nothing
    /// (no assemblies, no threads, no CPU) until a consumer appears.
    ///
    /// Scope is CPU + 主板 + 磁盘 + 电池. GPU is deliberately EXCLUDED: LHM's NVIDIA node costs
    /// 93.5 ms per Update() on this machine (39 sensors incl. per-engine D3D and 12VHPWR
    /// pins) while the GPU metrics already come from DXCore (temp/usage/VRAM) and NVML
    /// (power/clocks/fans, sub-microsecond) — enabling GPU here would be pure waste.
    /// </summary>
    internal sealed class LhmWorker : ISensorWorker
    {
        private Computer _computer;

        private readonly List<IHardware> _units = new List<IHardware>(8);
        private IHardware _cpu;

        public bool Open()
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsMotherboardEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,      // 磁盘温度 (needs elevation; the app always runs elevated)
                IsControllerEnabled = false,  // fan controllers are the most fragile part of LHM
                IsGpuEnabled = false,         // see the class comment
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
            };
            _computer.Open();

            foreach (IHardware hw in _computer.Hardware)
            {
                _units.Add(hw);
                foreach (IHardware sub in hw.SubHardware) _units.Add(sub);
                if (hw.HardwareType == HardwareType.Cpu) _cpu = hw;
            }
            return _units.Count > 0;
        }

        public SensorReading Read()
        {
            if (_units.Count == 0) return default(SensorReading);

            for (int i = 0; i < _units.Count; i++) _units[i].Update();

            double packageTemp = double.NaN;
            double hottestTemp = double.NaN;
            var entries = new List<SensorEntry>(24);

            for (int i = 0; i < _units.Count; i++)
            {
                IHardware hw = _units[i];
                string group = GroupOf(hw.HardwareType);
                if (group == null) continue;      // not a category we publish

                foreach (ISensor s in hw.Sensors)
                {
                    if (!s.Value.HasValue) continue;
                    double v = s.Value.Value;
                    string unit = UnitOf(s.SensorType);
                    if (unit == null) continue;

                    // Per-core rows are noise at this size (16 cores × 4 types) and duplicate
                    // what the CPU page's own core chart already shows; only the aggregate
                    // sensors are published. EXCEPTION: Zen4/5 expose no aggregate SVI2 core
                    // voltage at all (LHM skips it for model 0x61/0x44), so one per-core VID
                    // is kept as the only voltage reading the CPU can offer.
                    if (s.Name.StartsWith("Core #", StringComparison.Ordinal))
                    {
                        if (group == "CPU" && s.SensorType == SensorType.Voltage
                            && s.Name.StartsWith("Core #1 ", StringComparison.Ordinal)
                            && v > 0 && v <= 3.5)
                        {
                            entries.Add(new SensorEntry
                            {
                                Group = "CPU",
                                Name = "核心电压（VID）",
                                Unit = "V",
                                Value = v,
                            });
                        }
                        continue;
                    }

                    // CPU temperature: the package sensor becomes the headline (Tctl/Tdie on
                    // AMD, Package on Intel); it is not repeated in the list.
                    if (group == "CPU" && s.SensorType == SensorType.Temperature)
                    {
                        bool isPackage = s.Name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0
                                      || s.Name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0
                                      || s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isPackage && (double.IsNaN(packageTemp) || v > packageTemp)) packageTemp = v;
                        if (double.IsNaN(hottestTemp) || v > hottestTemp) hottestTemp = v;
                        continue;
                    }

                    if (!Keep(group, s)) continue;

                    // Plausibility gates — a broken read must never reach the UI. A 0 W / 0 MHz
                    // / 0 RPM reading means "no access" (driver absent / unelevated), not a real
                    // zero, so it is dropped instead of displayed.
                    if ((unit == "W" || unit == "MHz" || unit == "RPM") && v <= 0) continue;
                    if (unit == "°C" && (v < 5 || v > 125)) continue;
                    if (unit == "W" && v > 1000) continue;
                    if (unit == "V" && (v <= 0 || v > 3.5)) continue;
                    if (unit == "RPM" && v > 20000) continue;

                    entries.Add(new SensorEntry
                    {
                        Group = group,
                        // Storage and motherboard sensors are per-DEVICE ("Temperature 1/2" on
                        // each drive, "System"/"CPU" on the board): the device name is what
                        // makes them readable, so it is always prefixed.
                        Name = group == "CPU" ? DisplayNameOf(group, s) : Qualify(hw.Name, s),
                        Unit = unit,
                        Value = v,
                    });
                }
            }

            double cpuTemp = !double.IsNaN(packageTemp) ? packageTemp : hottestTemp;
            // A 0 °C reading is "no access" (PawnIO absent / driver not loaded), never a real
            // temperature — same rule as the sensor gates below.
            if (!double.IsNaN(cpuTemp) && (cpuTemp < 5 || cpuTemp > 125)) cpuTemp = double.NaN;
            if (double.IsNaN(cpuTemp) && entries.Count == 0) return default(SensorReading);

            return new SensorReading
            {
                Valid = true,
                CpuCelsius = cpuTemp,
                CpuSource = double.IsNaN(cpuTemp) ? null
                          : (!double.IsNaN(packageTemp) ? "LHM(Tctl/Tdie)" : "LHM(max core)"),
                Entries = entries.Count > 0 ? entries.ToArray() : Array.Empty<SensorEntry>(),
            };
        }

        public void Dispose()
        {
            try
            {
                _computer?.Close();
            }
            catch
            {
                // LHM's teardown must never take the sampling thread down with it.
            }
            _computer = null;
            _cpu = null;
            _units.Clear();
        }

        private static string GroupOf(HardwareType t)
        {
            switch (t)
            {
                case HardwareType.Cpu: return "CPU";
                case HardwareType.Motherboard: return "主板";
                // The board's OWN sensor list is always empty — everything readable (fan RPM,
                // board temperatures, SuperIO voltages) lives on the SuperIO child node, which
                // HardwareType.SuperIO identifies. Without this case those 24 sensors were
                // dropped on the first line of the read loop, which is why this machine looked
                // like it had no fan at all while LHM was reading Fan #1 = 1298 RPM fine.
                case HardwareType.SuperIO: return "主板";
                case HardwareType.Storage: return "磁盘";
                case HardwareType.Battery: return "电池";
                default: return null;
            }
        }

        private static string UnitOf(SensorType t)
        {
            switch (t)
            {
                case SensorType.Temperature: return "°C";
                case SensorType.Power: return "W";
                case SensorType.Voltage: return "V";
                case SensorType.Fan: return "RPM";
                case SensorType.Control: return "%";
                case SensorType.Clock: return "MHz";
                case SensorType.Load: return "%";
                case SensorType.Level: return "%";
                case SensorType.Current: return "A";
                default: return null;
            }
        }

        // What each page actually wants to show. Load/Data/SmallData never reach the sensor
        // list (the pages already render their own load bars and memory figures).
        private static bool Keep(string group, ISensor s)
        {
            switch (group)
            {
                case "CPU":
                    switch (s.SensorType)
                    {
                        case SensorType.Power:
                            return Has(s, "package");
                        case SensorType.Voltage:
                            return Has(s, "svi2") || Has(s, "core");
                        case SensorType.Clock:
                            // NEVER published: the CPU summary row already shows the live
                            // clock, and a second, differently-derived number reads as a bug.
                            return false;
                        case SensorType.Fan:
                            return true;
                        default:
                            return false;
                    }
                case "主板":
                    return s.SensorType == SensorType.Temperature || s.SensorType == SensorType.Fan;
                case "磁盘":
                    // Warning/Critical are THRESHOLDS, not measurements, and they carry the same
                    // "温度" label once qualified — letting them through made the router's
                    // "composite wins" rule end on Critical (94 °C) and a cold idle SSD read as
                    // 94 °C. The thresholds are useful, but only as thresholds, which no page
                    // shows yet.
                    return s.SensorType == SensorType.Temperature
                        && !Has(s, "Warning") && !Has(s, "Critical");
                case "电池":
                    return s.SensorType == SensorType.Level
                        || s.SensorType == SensorType.Power
                        || s.SensorType == SensorType.Voltage
                        || s.SensorType == SensorType.Current;
                default:
                    return false;
            }
        }

        private static bool Has(ISensor s, string needle)
            => s.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>"Samsung SSD 990 PRO 2TB · 温度 1" — the device name plus a translated,
        /// de-duplicated sensor label. Multiple drives each expose several "Temperature N"
        /// sensors (composite vs. per-sensor), so the drive name is the only thing that tells
        /// the user whose temperature a row is.</summary>
        private static string Qualify(string deviceName, ISensor s)
        {
            string sensor = s.Name;
            if (s.SensorType == SensorType.Temperature)
            {
                int digit = sensor.Length > 0 && char.IsDigit(sensor[sensor.Length - 1])
                    ? sensor[sensor.Length - 1] - '0'
                    : -1;
                sensor = digit >= 0 ? $"温度 {digit}" : "温度";
            }
            else if (s.SensorType == SensorType.Fan) sensor = "风扇";
            else if (s.SensorType == SensorType.Level) sensor = "电量";

            return string.IsNullOrEmpty(deviceName) ? sensor : deviceName + " · " + sensor;
        }

        // A handful of CPU sensors get a meaningful Chinese label; everything else keeps the
        // vendor's own sensor name (they are hardware-specific and often already descriptive).
        private static string DisplayNameOf(string group, ISensor s)
        {
            if (group != "CPU") return s.Name;
            switch (s.SensorType)
            {
                case SensorType.Power:
                    return s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ? "封装功耗" : s.Name;
                case SensorType.Voltage:
                    return s.Name.IndexOf("SVI2", StringComparison.OrdinalIgnoreCase) >= 0
                        || s.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ? "核心电压" : s.Name;
                case SensorType.Clock:
                    // The CPU page's own summary row already prints the live clock (PDH
                    // % Processor Performance × base): publishing "平均频率" here as well was
                    // a duplicate row with a different number.
                    return "平均频率";
                default:
                    return s.Name;
            }
        }

    }
}
