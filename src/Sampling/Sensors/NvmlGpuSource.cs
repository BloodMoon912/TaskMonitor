using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// NVIDIA GPU extras (功耗 / 风叶 / 核心频率) straight from nvml.dll — the driver's own API,
    /// ~0.1 µs per call after a one-time 14 ms init, so it needs no on-demand machinery and no
    /// kernel driver of our own (nvml.dll ships with the NVIDIA driver).
    ///
    /// Why not LHM for these: LHM's NVIDIA node costs **93.5 ms per Update()** on this machine
    /// (39 sensors incl. per-engine D3D and 12VHPWR pins). Asking NVML directly is ~10^5 times
    /// cheaper for the same numbers. Capability-gated everywhere: a machine without an NVIDIA
    /// card (or without nvml.dll) simply contributes nothing.
    /// </summary>
    internal sealed class NvmlGpuSource
    {
        private const string Dll = "nvml.dll";
        private const uint NvmlClockGraphics = 0;   // NVML_CLOCK_GRAPHICS
        private const uint NvmlTemperatureGpu = 0;  // NVML_TEMPERATURE_GPU

        [DllImport(Dll, EntryPoint = "nvmlInit_v2")] private static extern int NvmlInit();
        [DllImport(Dll, EntryPoint = "nvmlShutdown")] private static extern int NvmlShutdown();
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")] private static extern int GetCount(out uint count);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int GetHandle(uint index, out IntPtr handle);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")] private static extern int GetPower(IntPtr h, out uint milliwatts);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeed")] private static extern int GetFanSpeed(IntPtr h, out uint percent);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetNumFans")] private static extern int GetNumFans(IntPtr h, out uint count);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeedRPM")] private static extern int GetFanSpeedRpm(IntPtr h, ref NvmlFanSpeedInfo info);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")] private static extern int GetClock(IntPtr h, uint clockType, out uint mhz);
        [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")] private static extern int GetTemperature(IntPtr h, uint sensorType, out uint celsius);

        private bool _unavailable;
        private bool _initialized;
        private IntPtr[] _devices = Array.Empty<IntPtr>();

        /// <summary>Reads the extra GPU sensors for every NVIDIA adapter. Never throws; an
        /// unavailable nvml.dll / driver permanently disables the source (one log line).</summary>
        public SensorEntry[] Read()
        {
            if (_unavailable) return Array.Empty<SensorEntry>();
            if (!_initialized && !TryInit()) return Array.Empty<SensorEntry>();

            var list = new List<SensorEntry>(_devices.Length * 3);
            for (int i = 0; i < _devices.Length; i++)
            {
                IntPtr h = _devices[i];
                string suffix = _devices.Length > 1 ? $" #{i}" : "";

                if (GetPower(h, out uint mw) == 0 && mw > 0 && mw < 1_000_000)
                    list.Add(new SensorEntry { Group = "GPU", Name = "功耗" + suffix, Unit = "W", Value = mw / 1000.0 });

                if (GetClock(h, NvmlClockGraphics, out uint mhz) == 0 && mhz > 0)
                    list.Add(new SensorEntry { Group = "GPU", Name = "核心频率" + suffix, Unit = "MHz", Value = mhz });

                // Fan: reported in RPM so the GPU page and the CPU page print the same unit —
                // "1200 RPM" next to "1293 RPM" is comparable, "47 %" next to "1293 RPM" is not.
                // Zero-Fan idle legitimately reads 0 RPM (that is a reading, not a failure), so
                // only a failed call (fanless card / driver refused) is dropped. A card that
                // exposes no RPM but does expose a percentage falls back to % rather than
                // pretending the sensor is missing.
                if (TryGetFanRpm(h, out uint rpm))
                    list.Add(new SensorEntry { Group = "GPU", Name = "风扇" + suffix, Unit = "RPM", Value = rpm });
                else if (GetFanSpeed(h, out uint pct) == 0 && pct <= 100)
                    list.Add(new SensorEntry { Group = "GPU", Name = "风扇" + suffix, Unit = "%", Value = pct });

                // Temperature is NOT published here on purpose: the GPU page's own header rows
                // already show it (DXCore AdapterTemperatureCelsius), and repeating it under
                // 硬件感测 was a pure duplicate.
            }
            return list.Count > 0 ? list.ToArray() : Array.Empty<SensorEntry>();
        }

        /// <summary>nvmlFanSpeedInfo_t v1: { version, fan, speed }. The driver validates Version
        /// as NVML_STRUCT_VERSION(FanSpeedInfo, 1) = sizeof(struct) | (1 &lt;&lt; 24) = 12 | 0x01000000;
        /// getting that wrong makes every call return NVML_ERROR_INVALID_ARGUMENT, which is easy to
        /// mistake for "this card has no RPM sensor".</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NvmlFanSpeedInfo
        {
            public uint Version;
            public uint Fan;
            public uint Speed;
        }

        /// <summary>Average RPM across the card's fans. False when the driver exposes no RPM at
        /// all (old driver, some laptop dGPUs), so the caller can fall back to the percentage
        /// instead of reporting the sensor as missing.</summary>
        private static bool TryGetFanRpm(IntPtr h, out uint rpm)
        {
            rpm = 0;
            if (GetNumFans(h, out uint count) != 0 || count == 0) return false;

            ulong sum = 0;
            uint used = 0;
            for (uint fan = 0; fan < count && fan < 8; fan++)
            {
                var info = new NvmlFanSpeedInfo { Version = 12 | (1u << 24), Fan = fan };
                if (GetFanSpeedRpm(h, ref info) != 0) continue;
                if (info.Speed > 20000) continue;   // sensor noise, not a fan speed
                sum += info.Speed;
                used++;
            }
            if (used == 0) return false;
            rpm = (uint)(sum / used);
            return true;
        }

        private bool TryInit()
        {
            _initialized = true;   // no retry storms: a failure latches for the process lifetime
            try
            {
                if (!File.Exists(Path.Combine(Environment.SystemDirectory, "nvml.dll")))
                {
                    _unavailable = true;
                    return false;
                }
                if (NvmlInit() != 0)
                {
                    _unavailable = true;
                    return false;
                }
                if (GetCount(out uint count) != 0 || count == 0)
                {
                    NvmlShutdown();
                    _unavailable = true;
                    return false;
                }
                var devices = new IntPtr[count];
                for (uint i = 0; i < count; i++)
                {
                    if (GetHandle(i, out IntPtr h) != 0) devices[i] = IntPtr.Zero;
                    else devices[i] = h;
                }
                _devices = devices;
                Logger.Info($"NVML 已启用：{count} 个 NVIDIA 适配器（功耗/频率/风扇）");
                return true;
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("nvml-init", "NVML 初始化失败——GPU 扩展感测不可用", ex);
                _unavailable = true;
                return false;
            }
        }
    }
}
