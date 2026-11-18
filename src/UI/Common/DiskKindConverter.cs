using System;
using System.Globalization;
using System.Windows.Data;

namespace task_monitor
{
    /// <summary>
    /// XAML bridge for the disk detail view: maps a <see cref="DiskKind"/> to its display
    /// string (SSD / HDD / USB / SD / SCM / 未知) in the per-disk tab. The bare acronyms are
    /// identical in every language; only the two spelled-out kinds go through the catalogue
    /// (<c>unit.disk-kind.*</c>), so English reads "SD card" instead of "SD 卡".
    /// </summary>
    public sealed class DiskKindConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is DiskKind kind
                ? kind switch
                {
                    DiskKind.Ssd => "SSD",
                    DiskKind.Hdd => "HDD",
                    DiskKind.Scm => "SCM",
                    DiskKind.Usb => "USB",
                    DiskKind.Sd => Loc.T("unit.disk-kind.sd-card"),
                    _ => Loc.T("unit.disk-kind.unknown"),
                }
                : Loc.T("unit.disk-kind.unknown");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
