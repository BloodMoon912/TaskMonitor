using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using YamlDotNet.Serialization;

namespace task_monitor
{
    /// <summary>
    /// UI localisation: one YAML catalogue per language, addressed by id.
    ///
    /// Design: every user-visible string has an id (<c>area.component.field</c>, e.g.
    /// <c>settings.appearance.compact.title</c>) and the text lives in
    /// <c>lang/&lt;code&gt;.yaml</c>, embedded into the assembly. XAML uses the
    /// <c>{loc:Tr id}</c> markup extension, C# calls <see cref="T"/>. Nothing is translated by
    /// matching the Chinese text on a control any more: the old visual-tree walk could not reach
    /// elements built at runtime (ComboBoxItem containers, DataTemplate rows, thread-produced
    /// names), so the strings it missed stayed Simplified. zh-CN is the base catalogue: it must
    /// define every id, and a missing translation falls back to it (see tools/i18n-verify.ps1).
    ///
    /// Scope: the surfaces a user actually reads (taskbar labels, the five detail pages, the
    /// right-click menu, the settings shell, and the chrome dialogs — consent, startup guide,
    /// update, already-running, crash). Logs stay Chinese on purpose: they are a troubleshooting
    /// channel, and translated logs make old issues unreadable.
    /// </summary>
    internal static partial class Loc
    {
        public enum Lang { ZhCn, ZhTw, En }

        private static Lang _current = Lang.ZhCn;

        /// <summary>The active language. ZhCn is the identity: every lookup falls through.</summary>
        public static Lang Current
        {
            get { return _current; }
            set
            {
                if (_current == value) return;
                _current = value;
                _byText = null;   // the reverse map resolves against the ACTIVE language
            }
        }

        public static string Code(Lang l) => l == Lang.ZhTw ? "zh-TW" : l == Lang.En ? "en" : "zh-CN";

        public static Lang Parse(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return Lang.ZhCn;
            switch (code.Trim().ToLowerInvariant())
            {
                case "zh-tw": case "zh-hant": case "zh-hk": return Lang.ZhTw;
                case "en": case "en-us": case "en-gb": return Lang.En;
                default: return Lang.ZhCn;
            }
        }

        // ---------- catalogue loading (embedded lang/<code>.yaml) ----------

        private static readonly Dictionary<string, Dictionary<string, string>> Catalogs =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, string> Catalog(Lang lang)
        {
            string code = Code(lang);
            Dictionary<string, string> table;
            if (Catalogs.TryGetValue(code, out table)) return table;
            table = LoadEmbedded(code);
            Catalogs[code] = table;
            return table;
        }

        /// <summary>Reads <c>lang/&lt;code&gt;.yaml</c> from this assembly's manifest resources.
        /// The resource name is matched by suffix so the project's RootNamespace can change
        /// without breaking the lookup (Costura keeps embedded resources available).</summary>
        private static Dictionary<string, string> LoadEmbedded(string code)
        {
            var empty = new Dictionary<string, string>(StringComparer.Ordinal);
            Assembly asm = typeof(Loc).Assembly;
            string suffix = ".lang." + code + ".yaml";
            string name = null;
            foreach (string candidate in asm.GetManifestResourceNames())
            {
                if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = candidate; break; }
            }
            if (name == null)
            {
                Logger.WarnOnce("loc.missing-resource." + code,
                    "缺少内嵌语言资源 lang/" + code + ".yaml — 该语言将回退到 zh-CN");
                return empty;
            }
            try
            {
                using (Stream stream = asm.GetManifestResourceStream(name))
                {
                    if (stream == null) return empty;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var deserializer = new DeserializerBuilder().Build();
                        var parsed = deserializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
                        if (parsed == null) return empty;
                        var table = new Dictionary<string, string>(parsed.Count, StringComparer.Ordinal);
                        foreach (var pair in parsed)
                        {
                            if (pair.Key == null || pair.Value == null) continue;
                            table[pair.Key] = pair.Value;
                        }
                        return table;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("解析语言资源失败 lang/" + code + ".yaml", ex);
                return empty;
            }
        }

        // ---------- lookup ----------

        /// <summary>Translates one id. Missing id → the zh-CN text → the id itself (logged once
        /// per id in DEBUG builds, so a typo in XAML is visible instead of silent).</summary>
        public static string T(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            if (_current != Lang.ZhCn)
            {
                string translated;
                if (Catalog(_current).TryGetValue(id, out translated) && !string.IsNullOrEmpty(translated))
                    return translated;
                // Not an id in this catalogue: older call sites pass the zh-CN TEXT ("内存") as
                // the key. Resolve it through the reverse map instead of echoing Simplified back.
                string mapped;
                if (ByTextMap().TryGetValue(id, out mapped))
                {
                    if (Catalog(_current).TryGetValue(mapped, out translated) && !string.IsNullOrEmpty(translated))
                        return translated;
                }
            }
            return ZhCn(id);
        }

        /// <summary>Translates one id and formats it with <paramref name="args"/> — for entries
        /// that carry numbers ("{0} / {1}"). A pattern whose braces do not match the arguments
        /// comes back unformatted rather than throwing inside a sampling tick.</summary>
        public static string F(string id, params object[] args)
        {
            string pattern = T(id);
            if (args == null || args.Length == 0) return pattern;
            try { return string.Format(pattern, args); }
            catch (FormatException) { return pattern; }
        }

        /// <summary>True when <paramref name="text"/> is a zh-CN catalogue VALUE, i.e. it is
        /// already base-language copy rather than an id (pre-migration call sites).</summary>
        private static bool IsBaseText(string text)
        {
            if (_baseTexts == null)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in Catalog(Lang.ZhCn))
                    if (!string.IsNullOrEmpty(pair.Value)) set.Add(pair.Value);
                _baseTexts = set;
            }
            return _baseTexts.Contains(text);
        }

        private static HashSet<string> _baseTexts;

        /// <summary>The base-language text of an id (also the fallback for the other languages).
        /// Never throws: an unknown id comes back unchanged and is reported once.</summary>
        public static string ZhCn(string id)
        {
            string text;
            if (Catalog(Lang.ZhCn).TryGetValue(id, out text) && !string.IsNullOrEmpty(text)) return text;
            // Already base-language copy — nothing to translate, and NOT a missing id, so it must
            // not log (that would bury real typos under thousands of false positives).
            if (IsBaseText(id)) return id;
#if DEBUG
            Logger.WarnOnce("loc.missing-id." + id, "缺少本地化 id: " + id);
#endif
            return id;
        }

        /// <summary>User-defined overlay labels (settings.yaml <c>overlayLabels</c>): key = the
        /// built-in label — the zh-CN text ("内存") in files written before the id migration, the
        /// id itself afterwards — value = what to draw instead. Wins over the built-in
        /// translation, so a user can call it "VRAM 用量" on their own bar. Only the taskbar
        /// overlay consults this (the detail pages stay on the catalogue).</summary>
        public static Dictionary<string, string> Overrides;

        /// <summary>Overlay label: user override (by id, then by legacy zh-CN text) →
        /// language catalogue → the id.</summary>
        public static string L(string id)
        {
            if (Overrides != null)
            {
                string custom;
                if (Overrides.TryGetValue(id, out custom) && !string.IsNullOrWhiteSpace(custom))
                    return custom;
                string zh = ZhCn(id);
                if (!string.Equals(zh, id, StringComparison.Ordinal)
                    && Overrides.TryGetValue(zh, out custom) && !string.IsNullOrWhiteSpace(custom))
                    return custom;
            }
            return T(id);
        }

        private static Dictionary<string, string> _byText;

        /// <summary>Translates a string that carries no id — the only remaining source is text
        /// produced by code we do not translate at the origin (process-tag chips, adapter kinds,
        /// sensor nicknames). Resolves the zh-CN text back to its id, then translates that.
        /// Ambiguous texts (one zh-CN text shared by two ids) are left untranslated instead of
        /// guessing, and unknown text passes through.</summary>
        public static string ByText(string text)
        {
            if (string.IsNullOrEmpty(text) || _current == Lang.ZhCn) return text;
            Dictionary<string, string> map = ByTextMap();
            string id;
            return map.TryGetValue(text, out id) ? T(id) : text;
        }

        private static Dictionary<string, string> ByTextMap()
        {
            if (_byText != null) return _byText;
            var byText = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguous = new List<string>();
            foreach (var pair in Catalog(Lang.ZhCn))
            {
                if (string.IsNullOrEmpty(pair.Value)) continue;
                string existing;
                if (byText.TryGetValue(pair.Value, out existing))
                {
                    if (!string.Equals(existing, pair.Key, StringComparison.Ordinal)) ambiguous.Add(pair.Value);
                    continue;
                }
                byText[pair.Value] = pair.Key;
            }
            foreach (string text in ambiguous) byText.Remove(text);
            _byText = byText;
            return byText;
        }
    }
}
