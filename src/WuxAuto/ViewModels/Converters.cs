using System.Globalization;
using System.Windows.Data;
using WuxAuto.Core;

namespace WuxAuto.ViewModels;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool flag ? !flag : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool flag ? !flag : true;
}

/// <summary>An enum value against ConverterParameter (its name): for radio-button groups.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

public static class BoolConverters
{
    public static IValueConverter Inverse { get; } = new InverseBoolConverter();

    public static IValueConverter EnumEquals { get; } = new EnumEqualsConverter();
}

/// <summary>Enum choices for the combo boxes.</summary>
public static class OptionLists
{
    public static RunMode[] RunModes { get; } =
    {
        RunMode.AutoLaby,
        RunMode.AutoLod,
    };

    public static InputMode[] InputModes { get; } =
    {
        InputMode.ForegroundSendInput,
        InputMode.BackgroundMessages,
    };

    public static RotationMode[] RotationModes { get; } =
    {
        RotationMode.Key,
        RotationMode.MouseDrag,
    };

    public static GateDetectionMode[] GateModes { get; } =
    {
        GateDetectionMode.Delay,
        GateDetectionMode.Template,
        GateDetectionMode.Text,
    };

    public static CompassMode[] CompassModes { get; } =
    {
        CompassMode.Auto,
        CompassMode.Template,
        CompassMode.Brightness,
    };
}
