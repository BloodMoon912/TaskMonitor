using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// The safety net behind the id-based catalogue: a visual-tree pass that runs when a window
    /// or page has been built and translates whatever still carries plain base-language text.
    ///
    /// Why it survives next to <c>{loc:Tr}</c> and <see cref="Loc.T"/>: several surfaces are
    /// assembled in XAML we cannot annotate per attribute (the settings shell's cards, the
    /// theme's DataTemplate rows) or are generated at run time by iNKORE's controls. Marking the
    /// strings at the source is the plan; this pass catches the remainder so a forgotten string
    /// degrades to "still Chinese" instead of "half translated". It only ever rewrites a value
    /// that <see cref="Loc.ByText"/> can resolve, and it never touches a bound property — a
    /// BindingExpression owns that value and overwriting it would break the binding outright.
    /// </summary>
    internal static partial class Loc
    {
        // Runs at most once per element tree root: windows fire Loaded again on re-show, and the
        // pass is O(nodes) reflection work we do not want repeating on every tab switch.
        private static readonly ConditionalWeakTable<DependencyObject, object> AlreadyApplied =
            new ConditionalWeakTable<DependencyObject, object>();

        private static readonly object Marker = new object();

        /// <summary>Translate <paramref name="root"/> and its descendants once.</summary>
        public static void ApplyOnce(DependencyObject root)
        {
            if (root == null) return;
            if (AlreadyApplied.TryGetValue(root, out _)) return;
            AlreadyApplied.Add(root, Marker);
            Apply(root);
        }

        /// <summary>Translate <paramref name="root"/> and its descendants, unconditionally.</summary>
        public static void Apply(DependencyObject root)
        {
            if (root == null) return;
            // zh-CN is the base language: every rewrite would be an identity, and an identity
            // write can still clobber whitespace or a binding, so skip the whole walk.
            if (Current == Lang.ZhCn) return;
            try
            {
                Walk(root, 0);
            }
            catch (Exception ex)
            {
                Logger.Warn("本地化视觉树遍历失败", ex);
            }
        }

        private static void Walk(DependencyObject node, int depth)
        {
            if (node == null || depth > 64) return;
            TranslateElement(node);

            // ItemsControl.Items is walked as well: iNKORE builds several rows as plain objects
            // that are not in the visual tree yet at Loaded time.
            var itemsControl = node as ItemsControl;
            if (itemsControl != null)
            {
                int count = 0;
                try { count = itemsControl.Items.Count; }
                catch (Exception) { count = 0; }
                for (int i = 0; i < count && i < 512; i++)
                {
                    var item = itemsControl.Items[i] as DependencyObject;
                    if (item != null) Walk(item, depth + 1);
                }
            }

            int children = 0;
            try { children = VisualTreeHelper.GetChildrenCount(node); }
            catch (Exception) { children = 0; }
            for (int i = 0; i < children; i++)
            {
                DependencyObject child = null;
                try { child = VisualTreeHelper.GetChild(node, i); }
                catch (Exception) { child = null; }
                Walk(child, depth + 1);
            }
        }

        /// <summary>Dependency properties that hold user-visible strings. Matched by NAME up the
        /// element's type chain, so one list covers WPF (Text/Content/Header/ToolTip) and iNKORE
        /// (Title/Message/OnContent/OffContent/ItemHeader/PlaceholderText) without a hard
        /// reference to those types.</summary>
        private static readonly string[] TextPropertyNames =
        {
            "Text", "Content", "Header", "Title", "Message", "OnContent", "OffContent",
            "ItemHeader", "PlaceholderText", "Description", "ToolTip", "ActionIconToolTip",
        };

        private static readonly Dictionary<Type, List<DependencyProperty>> PropertyCache =
            new Dictionary<Type, List<DependencyProperty>>();

        private static List<DependencyProperty> ResolveProperties(Type type)
        {
            List<DependencyProperty> found;
            if (PropertyCache.TryGetValue(type, out found)) return found;
            found = new List<DependencyProperty>(2);
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var name in TextPropertyNames)
                {
                    var field = t.GetField(name + "Property",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.FlattenHierarchy);
                    var dp = field == null ? null : field.GetValue(null) as DependencyProperty;
                    if (dp != null) found.Add(dp);
                }
            }
            PropertyCache[type] = found;
            return found;
        }

        private static void TranslateElement(DependencyObject node)
        {
            if (!(node is FrameworkElement) && !(node is FrameworkContentElement)) return;

            // Run/AccessText carry their text on a plain DP and are not FrameworkElements.
            var run = node as Run;
            if (run != null) { run.Text = ByText(run.Text); return; }
            var access = node as AccessText;
            if (access != null) { access.Text = ByText(access.Text); return; }

            foreach (var dp in ResolveProperties(node.GetType()))
            {
                object current;
                try
                {
                    if (!node.CheckAccess() && !node.Dispatcher.CheckAccess()) continue;
                    current = node.ReadLocalValue(dp);
                }
                catch (Exception) { continue; }

                // A local value of BindingExpression (or any expression) means a binding owns
                // this property — SetValue would replace the binding, so leave it alone.
                if (current == DependencyProperty.UnsetValue || current is BindingExpression) continue;
                var text = current as string;
                if (string.IsNullOrEmpty(text)) continue;

                string translated = ByText(text);
                if (string.Equals(translated, text, StringComparison.Ordinal)) continue;
                try { node.SetValue(dp, translated); }
                catch (Exception) { /* read-only or type mismatch: nothing to do */ }
            }

            // ToolTip set as an object (a ToolTip control) holds its text on its Content child,
            // which the walk reaches anyway; a plain string ToolTip is covered by the list above.
            var frameworkElement = node as FrameworkElement;
            if (frameworkElement != null)
            {
                var toolTipText = frameworkElement.ToolTip as string;
                if (!string.IsNullOrEmpty(toolTipText))
                {
                    string translated = ByText(toolTipText);
                    if (!string.Equals(translated, toolTipText, StringComparison.Ordinal))
                        frameworkElement.ToolTip = translated;
                }
            }
        }
    }
}
