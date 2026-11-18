using System;

namespace task_monitor
{
    /// <summary>
    /// Routes the flat sensor list into the places the UI actually reads, so a value appears
    /// exactly once and on the object it belongs to:
    ///
    ///   CPU 封装功耗/核心电压 → <see cref="SystemSnapshot.CpuPowerWatts"/> / <c>CpuVoltageV</c>
    ///                          (the CPU page's summary rows — 温度 already has its own header)
    ///   GPU 功耗/风扇         → <see cref="GpuInfo.PowerText"/> / <c>FanText</c> per adapter
    ///                          (the GPU page's per-adapter tab content)
    ///   磁碟 温度             → <see cref="DiskInfo.TemperatureText"/> per drive
    ///                          (the disk page's per-disk tab content)
    ///
    /// Nothing is rendered as a free-floating "sensor block": a row that lives next to the
    /// thing it describes needs no device prefix and cannot be mistaken for another device.
    /// </summary>
    internal static class SensorRouter
    {
        public static void Apply(SystemSnapshot snapshot, SensorEntry[] packEntries, SensorEntry[] gpuEntries)
        {
            if (snapshot == null) return;

            // Reset first: the pack is on-demand, so a tick with no reading must clear the
            // previous one instead of leaving a stale number on the page.
            snapshot.CpuPowerWatts = double.NaN;
            snapshot.CpuVoltageV = double.NaN;
            var boardFans = new System.Text.StringBuilder();
            if (snapshot.Gpus != null)
                for (int i = 0; i < snapshot.Gpus.Count; i++)
                {
                    snapshot.Gpus[i].PowerText = "--";
                    snapshot.Gpus[i].FanText = "--";
                }
            if (snapshot.Disks != null)
                for (int i = 0; i < snapshot.Disks.Count; i++)
                    snapshot.Disks[i].TemperatureText = "--";

            if (packEntries != null)
            {
                for (int i = 0; i < packEntries.Length; i++)
                {
                    SensorEntry e = packEntries[i];
                    switch (e.Group)
                    {
                        case "CPU":
                            if (e.Name == "封装功耗") snapshot.CpuPowerWatts = e.Value;
                            else if (e.Name == "核心电压") snapshot.CpuVoltageV = e.Value;
                            else if (e.Name == "核心电压（VID）" && double.IsNaN(snapshot.CpuVoltageV))
                                snapshot.CpuVoltageV = e.Value;   // fallback: no SVI2 on Zen4/5
                            break;
                        case "磁盘":
                            RouteDisk(snapshot, e);
                            break;
                        case "主板":
                            // Only the fan speed has a home (the CPU page). The six unlabelled
                            // SuperIO temperature channels (Temperature #1..6) are deliberately
                            // dropped: they are not numbers a user can act on, and the CPU's own
                            // temperature already answers the question they were read for.
                            if (e.Unit != null && e.Unit.IndexOf("RPM", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // 0 RPM on this board means an empty fan HEADER, not a stopped fan
                                // (Fan #2/#3 read 0 with nothing plugged in), so it is dropped
                                // rather than printed as if the fan had stalled.
                                if (e.Value < 1) break;
                                if (boardFans.Length > 0) boardFans.Append(" / ");
                                boardFans.Append($"{e.Value:0} RPM");
                            }
                            break;
                    }
                }
            }

            if (gpuEntries != null && snapshot.Gpus != null)
            {
                for (int i = 0; i < gpuEntries.Length; i++)
                {
                    SensorEntry e = gpuEntries[i];
                    if (e.Group != "GPU") continue;
                    GpuInfo gpu = FindGpu(snapshot.Gpus, e.Name);
                    if (gpu == null) continue;
                    if (e.Name.StartsWith("功耗", StringComparison.Ordinal)) gpu.PowerText = $"{e.Value:0.#} W";
                    // The unit comes from the entry: RPM when the driver exposes a tachometer
                    // (same unit as the CPU fan), % only as the fallback for cards that do not.
                    else if (e.Name.StartsWith("风扇", StringComparison.Ordinal))
                        gpu.FanText = e.Unit != null && e.Unit.IndexOf("RPM", StringComparison.OrdinalIgnoreCase) >= 0
                            ? $"{e.Value:0} RPM"
                            : $"{e.Value:0} %";
                }
            }

            snapshot.MotherboardFanText = boardFans.ToString();
        }

        // "功耗" / "功耗 #1" → the adapter whose Index matches, or the first one for a
        // single-GPU machine (NVML numbers devices in driver order, which is the same order
        // DXCore enumerated them on every machine we measured; the suffix is only a hint).
        private static GpuInfo FindGpu(System.Collections.Generic.List<GpuInfo> gpus, string sensorName)
        {
            int wanted = -1;
            int hash = sensorName.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0 && int.TryParse(sensorName.Substring(hash + 2), out int parsed)) wanted = parsed;

            for (int i = 0; i < gpus.Count; i++)
                if (wanted < 0 || gpus[i].Index == wanted) return gpus[i];
            return null;
        }

        // LHM names the sensor "DEVICE · 温度"/"DEVICE · 温度 1"; the device string is matched
        // against DiskInfo.Name (vendor+model) both ways, because LHM sometimes appends a
        // bus/serial suffix ("Samsung SSD 990 PRO 2TB" vs "Samsung SSD 990 PRO 2TB (NVMe)").
        private static void RouteDisk(SystemSnapshot snapshot, SensorEntry e)
        {
            if (snapshot.Disks == null || snapshot.Disks.Count == 0) return;

            int split = e.Name.IndexOf(" · ", StringComparison.Ordinal);
            string device = split > 0 ? e.Name.Substring(0, split) : "";
            bool primary = split > 0 && e.Name.Substring(split + 3) == "温度";   // composite sensor

            DiskInfo disk = FindDisk(snapshot.Disks, device);
            if (disk == null) return;

            if (primary || disk.TemperatureText == "--")
                // ONE number per disk. The composite sensor wins; the extra measurement points
                // some NVMe drives expose (controller vs NAND) used to be appended as
                // "36°C（其他测点 35.9 / 45.9）", which looks informative and is not: the drive
                // is one device with one temperature the user cares about.
                disk.TemperatureText = $"{e.Value:0.#}°C";
        }

        private static DiskInfo FindDisk(System.Collections.Generic.List<DiskInfo> disks, string device)
        {
            if (string.IsNullOrEmpty(device))
                return disks.Count == 1 ? disks[0] : null;

            for (int i = 0; i < disks.Count; i++)
            {
                string name = disks[i].Name ?? "";
                if (name.Length == 0) continue;
                if (name.IndexOf(device, StringComparison.OrdinalIgnoreCase) >= 0
                    || device.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return disks[i];
            }
            return null;
        }
    }
}
