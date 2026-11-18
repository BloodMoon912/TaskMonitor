using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace task_monitor
{
    /// <summary>
    /// Minute-bucket history for the five headline metrics plus the temperature/VRAM sensors.
    ///
    /// Shape (deliberately small — the product's whole selling point is a near-zero footprint):
    ///   · one bucket per clock minute, holding avg/max/min/count per metric;
    ///   · the last 24 h live in memory (1440 buckets × 9 metrics ≈ 100 KB);
    ///   · completed buckets are appended to <exe>\history\yyyy-MM-dd.csv every 5 minutes
    ///     (never on the sampling tick itself — the write runs on a pool thread);
    ///   · files older than 30 days are deleted on startup and after each flush.
    ///
    /// The live buckets are what the history window charts; the CSV is what survives a restart
    /// and can be opened in Excel.
    /// </summary>
    internal sealed class HistoryStore
    {
        public const int MetricCount = 9;
        public const int Cpu = 0, Ram = 1, Disk = 2, Gpu = 3, GpuTemp = 4, GpuVram = 5, CpuTemp = 6, NetUp = 7, NetDown = 8;

        public static readonly string[] MetricNames =
        {
            "CPU %", "記憶體 %", "磁碟 %", "GPU %", "GPU 溫度", "GPU 顯存 %", "CPU 溫度", "網路 上行", "網路 下行",
        };

        private struct Bucket
        {
            public DateTime Minute;
            public double[] Sum;
            public double[] Max;
            public double[] Min;
            public int[] Count;
            public bool Filled;
        }

        private const int RingCapacity = 24 * 60;
        private const int FlushIntervalMs = 5 * 60 * 1000;
        private const int RetentionDays = 30;

        /// <summary>Hard ceiling on how many day files one <see cref="SeriesFromFiles"/> call may
        /// open — a caller asking for a year must not turn into a year of disk reads.</summary>
        private const int MaxFilesPerRead = 30;

        private readonly object _sync = new object();
        private readonly List<Bucket> _ring = new List<Bucket>(RingCapacity + 1);
        private readonly List<Bucket> _pending = new List<Bucket>();   // completed, not yet on disk
        private Bucket _current;
        private DateTime _currentMinute = DateTime.MinValue;
        private long _lastFlushTicks;
        private bool _writing;

        public string Directory { get; }

        public HistoryStore(string baseDirectory)
        {
            Directory = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "history");
        }

        /// <summary>Records one sample. Values that are NaN (unavailable) are skipped — a
        /// metric that only becomes available later must not drag its average to 0.</summary>
        public void Record(double[] values)
        {
            if (values == null || values.Length != MetricCount) return;
            DateTime minute = Truncate(DateTime.Now);

            lock (_sync)
            {
                if (minute != _currentMinute)
                {
                    if (_current.Filled) Complete(_current);
                    _current = NewBucket(minute);
                    _currentMinute = minute;
                }

                for (int i = 0; i < MetricCount; i++)
                {
                    double v = values[i];
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    _current.Sum[i] += v;
                    if (v > _current.Max[i]) _current.Max[i] = v;
                    if (v < _current.Min[i]) _current.Min[i] = v;
                    _current.Count[i]++;
                }
                _current.Filled = true;
            }

            MaybeFlush();
        }

        /// <summary>The averaged series for one metric over the last <paramref name="minutes"/>,
        /// resampled to at most <paramref name="points"/> values (null = no data for that slot).</summary>
        public double?[] Series(int metric, int minutes, int points)
        {
            if (metric < 0 || metric >= MetricCount || minutes <= 0 || points <= 0) return new double?[0];

            var result = new double?[points];
            DateTime from = Truncate(DateTime.Now).AddMinutes(-minutes);

            lock (_sync)
            {
                var buckets = new List<Bucket>(_ring.Count + 1);
                foreach (var b in _ring) if (b.Minute >= from) buckets.Add(b);
                if (_current.Filled && _current.Minute >= from) buckets.Add(_current);
                if (buckets.Count == 0) return result;

                buckets.Sort((a, b) => a.Minute.CompareTo(b.Minute));
                double perPoint = (double)minutes / points;
                DateTime origin = Truncate(DateTime.Now).AddMinutes(-minutes + 1);

                for (int i = 0; i < points; i++)
                {
                    DateTime slotStart = origin.AddMinutes(i * perPoint);
                    DateTime slotEnd = origin.AddMinutes((i + 1) * perPoint);
                    double sum = 0;
                    int count = 0;
                    foreach (var b in buckets)
                    {
                        if (b.Minute < slotStart || b.Minute >= slotEnd) continue;
                        if (b.Count[metric] == 0) continue;
                        sum += b.Sum[metric] / b.Count[metric];
                        count++;
                    }
                    if (count > 0) result[i] = sum / count;
                }
            }
            return result;
        }

        /// <summary>Stats over the same window: average / max / min of the averaged buckets.</summary>
        public bool TryGetStats(int metric, int minutes, out double average, out double max, out double min, out int samples)
        {
            average = max = min = double.NaN;
            samples = 0;
            if (metric < 0 || metric >= MetricCount) return false;
            DateTime from = Truncate(DateTime.Now).AddMinutes(-minutes);

            lock (_sync)
            {
                double sum = 0;
                int n = 0;
                foreach (var b in _ring)
                {
                    if (b.Minute < from || b.Count[metric] == 0) continue;
                    double v = b.Sum[metric] / b.Count[metric];
                    sum += v;
                    n++;
                    if (double.IsNaN(max) || v > max) max = v;
                    if (double.IsNaN(min) || v < min) min = v;
                }
                if (n == 0) return false;
                average = sum / n;
                samples = n;
                return true;
            }
        }

        /// <summary>
        /// The averaged series for one metric over the last <paramref name="minutes"/>, read back
        /// from <c>history\yyyy-MM-dd.csv</c> — the only way to answer a range the 24 h ring
        /// cannot (the window's 7-day mode).
        ///
        /// Contract:
        ///   · only the day files the range actually needs are opened, at most
        ///     <see cref="MaxFilesPerRead"/> of them; a missing (purged) day is a hole in the
        ///     curve, not an error;
        ///   · TODAY'S FILE IS NEVER READ — today is served from the ring instead. That is what
        ///     keeps a minute from being counted twice, and it is also why the ring merge below
        ///     starts at today's 00:00 (after midnight the ring still holds yesterday, which is
        ///     already on disk);
        ///   · a row that does not parse (header, half-written trailing line, hand-edited file)
        ///     is skipped, never thrown: one bad line must not blank the chart;
        ///   · the minute values falling into each of the <paramref name="points"/> bins are
        ///     averaged, exactly like <see cref="Series(int,int,int)"/> does for memory.
        ///
        /// Runs on the history window's 5 s refresh (UI thread) — file I/O is deliberately NOT
        /// part of the sampling path: <see cref="Record(double[])"/> never touches the disk.
        /// </summary>
        public double?[] SeriesFromFiles(int metric, int minutes, int points)
        {
            if (metric < 0 || metric >= MetricCount || minutes <= 0 || points <= 0) return new double?[0];

            var result = new double?[points];
            var sum = new double[points];
            var count = new int[points];

            DateTime now = Truncate(DateTime.Now);
            DateTime origin = now.AddMinutes(-minutes + 1);
            DateTime today = now.Date;
            double perPoint = (double)minutes / points;

            // Past days only; `day >= firstDay` is the range and `day < today` is the "never read
            // today's file" rule. Walked BACKWARD so the file cap keeps the newest days when a
            // caller ever asks for more than 30.
            DateTime firstDay = origin.Date;
            int filesRead = 0;
            for (DateTime day = today.AddDays(-1); day >= firstDay && filesRead < MaxFilesPerRead; day = day.AddDays(-1))
            {
                string path = Path.Combine(Directory,
                    day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");
                if (!File.Exists(path)) continue;            // never flushed / already purged
                filesRead++;
                ReadDayFile(path, metric, origin, perPoint, sum, count);
            }

            // Today: memory only. Today's own CSV would duplicate these minutes.
            lock (_sync)
            {
                foreach (var b in _ring) if (b.Minute >= today) AddBucket(b, metric, origin, perPoint, sum, count);
                if (_current.Filled && _current.Minute >= today) AddBucket(_current, metric, origin, perPoint, sum, count);
            }

            for (int i = 0; i < points; i++) if (count[i] > 0) result[i] = sum[i] / count[i];
            return result;
        }

        /// <summary>Streams one day file line by line (no regex, no Split allocation per row) and
        /// folds the requested metric into the bin accumulators.</summary>
        private static void ReadDayFile(string path, int metric, DateTime origin, double perPoint,
                                        double[] sum, int[] count)
        {
            try
            {
                // FileShare.ReadWrite: the flush thread may be appending to this very file.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        // The header ("minute,CPU %,…") fails the minute parse and is skipped here.
                        if (!TryParseMinute(line, out DateTime minute)) continue;
                        if (!TryParseCsvField(line, metric + 1, out double value)) continue;   // empty = no data
                        int bin = BinIndex(minute, origin, perPoint, count.Length);
                        if (bin < 0) continue;
                        sum[bin] += value;
                        count[bin]++;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("history-read", "監控歷史讀檔失敗——該日資料略過（不影響取樣）", ex);
            }
        }

        private static void AddBucket(Bucket bucket, int metric, DateTime origin, double perPoint,
                                      double[] sum, int[] count)
        {
            if (bucket.Count[metric] == 0) return;
            int bin = BinIndex(bucket.Minute, origin, perPoint, count.Length);
            if (bin < 0) return;
            sum[bin] += bucket.Sum[metric] / bucket.Count[metric];
            count[bin]++;
        }

        /// <summary>Which of the <paramref name="points"/> bins a minute belongs to; -1 when the
        /// minute falls outside the window (the same slot geometry as <see cref="Series(int,int,int)"/>).</summary>
        private static int BinIndex(DateTime minute, DateTime origin, double perPoint, int points)
        {
            double index = Math.Floor((minute - origin).TotalMinutes / perPoint);
            if (index < 0 || index >= points) return -1;
            return (int)index;
        }

        // ---------- CSV field parsing (no regex — this runs over every row of up to 30 files) ----------

        /// <summary>Parses the leading <c>yyyy-MM-dd HH:mm</c> column. Returns false for the header,
        /// for a half-written trailing line and for anything else that is not our timestamp.</summary>
        private static bool TryParseMinute(string s, out DateTime minute)
        {
            minute = default;
            if (s.Length < 16) return false;
            if (s[4] != '-' || s[7] != '-' || (s[10] != ' ' && s[10] != 'T') || s[13] != ':') return false;
            if (!TryParseDigits(s, 0, 4, out int year)) return false;
            if (!TryParseDigits(s, 5, 2, out int month)) return false;
            if (!TryParseDigits(s, 8, 2, out int day)) return false;
            if (!TryParseDigits(s, 11, 2, out int hour)) return false;
            if (!TryParseDigits(s, 14, 2, out int minuteOfHour)) return false;

            if (year < 1 || year > 9999 || month < 1 || month > 12) return false;
            if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
            if (hour > 23 || minuteOfHour > 59) return false;

            minute = new DateTime(year, month, day, hour, minuteOfHour, 0, DateTimeKind.Unspecified);
            return true;
        }

        private static bool TryParseDigits(string s, int start, int length, out int value)
        {
            value = 0;
            int end = start + length;
            if (end > s.Length) return false;
            for (int i = start; i < end; i++)
            {
                int d = s[i] - '0';
                if (d < 0 || d > 9) return false;
                value = value * 10 + d;
            }
            return true;
        }

        /// <summary>Reads field <paramref name="field"/> (0 = minute) out of one CSV row.
        /// An empty field returns false — that is how "no data for this metric in this minute" is
        /// written, and it must stay indistinguishable from a missing value, not become 0.</summary>
        private static bool TryParseCsvField(string s, int field, out double value)
        {
            value = double.NaN;
            int index = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != ',') continue;
                if (index == field) return TryParseNumber(s, start, i, out value);
                index++;
                start = i + 1;
            }
            return index == field && TryParseNumber(s, start, s.Length, out value);
        }

        /// <summary>Invariant number parse for one field. The writer emits "F2" (digits and one
        /// '.'), which is what ~all rows are, so that shape is parsed by hand; anything else falls
        /// back to <see cref="double.TryParse(string, NumberStyles, IFormatProvider, out double)"/>
        /// so an Excel-edited cell ("1E+06", "-2.5", a stray space) still reads correctly.</summary>
        private static bool TryParseNumber(string s, int start, int end, out double value)
        {
            value = double.NaN;
            while (start < end && (s[start] == ' ' || s[start] == '\t')) start++;
            while (end > start && (s[end - 1] == ' ' || s[end - 1] == '\t')) end--;
            if (end <= start) return false;                   // empty column = no data

            if (TryParsePlainNumber(s, start, end, out value)) return true;
            return double.TryParse(s.Substring(start, end - start), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParsePlainNumber(string s, int start, int end, out double value)
        {
            value = 0;
            double scale = 1;
            bool fraction = false;
            for (int i = start; i < end; i++)
            {
                char c = s[i];
                if (c == '.')
                {
                    if (fraction) return false;               // "1.2.3"
                    fraction = true;
                    continue;
                }
                int d = c - '0';
                if (d < 0 || d > 9) return false;             // sign / exponent / junk → fallback
                if (fraction) scale *= 10;
                value = value * 10 + d;
            }
            if (fraction) value /= scale;
            return true;
        }

        // ---------- persistence ----------

        private void MaybeFlush()
        {
            long now = Environment.TickCount;
            if (_lastFlushTicks != 0 && now - _lastFlushTicks < FlushIntervalMs) return;
            if (_writing) return;
            _writing = true;

            List<Bucket> batch;
            lock (_sync)
            {
                _lastFlushTicks = now;
                if (_pending.Count == 0) { _writing = false; return; }
                batch = new List<Bucket>(_pending);
                _pending.Clear();
            }

            // Never on the sampling tick: the CSV append is a pool-thread task.
            Task.Run(() =>
            {
                try
                {
                    WriteBatch(batch);
                }
                catch (Exception ex)
                {
                    Logger.WarnOnce("history-flush", "監控歷史寫檔失敗——本批次捨棄（不影響取樣）", ex);
                }
                finally
                {
                    _writing = false;
                }
            });
        }

        /// <summary>Synchronous flush for shutdown: whatever completed minutes are still only
        /// in memory land on disk before the process dies (the periodic flush runs every 5
        /// minutes, so without this up to 4 minutes of buckets could be lost per exit).</summary>
        public void FlushNow()
        {
            List<Bucket> batch;
            lock (_sync)
            {
                if (_pending.Count == 0) return;
                batch = new List<Bucket>(_pending);
                _pending.Clear();
            }
            try
            {
                WriteBatch(batch);
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("history-flush-exit", "監控歷史：退出時寫檔失敗", ex);
            }
        }

        private void WriteBatch(List<Bucket> batch)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var byDay = new Dictionary<string, List<Bucket>>();
            foreach (var b in batch)
            {
                string file = b.Minute.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
                if (!byDay.TryGetValue(file, out var list)) byDay[file] = list = new List<Bucket>();
                list.Add(b);
            }

            foreach (var kv in byDay)
            {
                string path = Path.Combine(Directory, kv.Key);
                var sb = new StringBuilder();
                if (!File.Exists(path))
                {
                    sb.Append("minute");
                    foreach (string n in MetricNames) sb.Append(',').Append(n);
                    sb.AppendLine();
                }
                foreach (var b in kv.Value)
                {
                    sb.Append(b.Minute.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                    for (int i = 0; i < MetricCount; i++)
                    {
                        sb.Append(',');
                        if (b.Count[i] > 0) sb.Append((b.Sum[i] / b.Count[i]).ToString("F2", CultureInfo.InvariantCulture));
                    }
                    sb.AppendLine();
                }
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
        }

        /// <summary>Drops history files older than the retention window. Cheap (a directory
        /// listing per call) and only run at startup and after a flush.</summary>
        public void PurgeOldFiles()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return;
                DateTime cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
                foreach (string path in System.IO.Directory.GetFiles(Directory, "*.csv"))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out DateTime day) && day < cutoff)
                        File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("history-purge", "監控歷史清理舊檔失敗", ex);
            }
        }

        // ---------- helpers ----------

        private static DateTime Truncate(DateTime t) => new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);

        private static Bucket NewBucket(DateTime minute)
        {
            var b = new Bucket
            {
                Minute = minute,
                Sum = new double[MetricCount],
                Max = new double[MetricCount],
                Min = new double[MetricCount],
                Count = new int[MetricCount],
            };
            for (int i = 0; i < MetricCount; i++) b.Min[i] = double.MaxValue;
            return b;
        }

        private void Complete(Bucket bucket)
        {
            _ring.Add(bucket);
            if (_ring.Count > RingCapacity) _ring.RemoveAt(0);
            _pending.Add(bucket);
            if (_pending.Count > 24 * 60) _pending.RemoveAt(0);   // bounded even if disk is full
        }
    }
}
