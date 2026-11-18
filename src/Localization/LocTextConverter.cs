using System;
using System.Globalization;
using System.Windows.Data;

namespace task_monitor.Localization
{
    /// <summary>
    /// Translates a string that carries no id. The only remaining case is text produced where we
    /// cannot translate at the origin (e.g. <c>ProcessInfo.TagText</c>, whose 服务 / 服务组 chips
    /// are built inside the sampler) — the value is matched back to its id through the zh-CN
    /// catalogue and returned in the active language.
    ///
    /// <para>Stateless, so a single instance per XAML file is enough:
    /// <c>&lt;loc:LocTextConverter x:Key="LocText" /&gt;</c> and
    /// <c>Text="{Binding TagText, Converter={StaticResource LocText}}"</c>. Unknown text passes
    /// through unchanged, so this can never hide a value.</para>
    /// </summary>
    [ValueConversion(typeof(string), typeof(string))]
    public sealed class LocTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string text ? Loc.ByText(text) : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException("LocTextConverter is one-way.");
    }
}
