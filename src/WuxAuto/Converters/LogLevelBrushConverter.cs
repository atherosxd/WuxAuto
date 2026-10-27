using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WuxAuto.Core;

namespace WuxAuto.Converters;

/// <summary>Maps a <see cref="LogLevel"/> to the log pane's text brush (frozen, shared).</summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    private static readonly Brush TraceBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0x8A, 0x9E)));
    private static readonly Brush InfoBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD5, 0xDB, 0xE6)));
    private static readonly Brush SuccessBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x54, 0xD6, 0x8A)));
    private static readonly Brush WarningBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x4D)));
    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x6D, 0x6D)));

    public static Brush For(LogLevel level) => level switch
    {
        LogLevel.Trace => TraceBrush,
        LogLevel.Success => SuccessBrush,
        LogLevel.Warning => WarningBrush,
        LogLevel.Error => ErrorBrush,
        _ => InfoBrush,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => For(value is LogLevel level ? level : LogLevel.Info);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
