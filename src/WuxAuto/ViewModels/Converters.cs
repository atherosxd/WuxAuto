using System.Globalization;
using System.Windows;
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

/// <summary>Visible for a non-empty string, collapsed for null or empty (<see cref="Invert"/>: the other way round).</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; init; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string { Length: > 0 } != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public static class BoolConverters
{
    public static IValueConverter Inverse { get; } = new InverseBoolConverter();

    public static IValueConverter EnumEquals { get; } = new EnumEqualsConverter();

    public static IValueConverter NotEmptyToVisibility { get; } = new NotEmptyToVisibilityConverter();

    public static IValueConverter EmptyToVisibility { get; } = new NotEmptyToVisibilityConverter { Invert = true };
}

/// <summary>Enum choices for the combo boxes.</summary>
public static class OptionLists
{
    public static RunMode[] RunModes { get; } =
    {
        RunMode.AutoLaby,
        RunMode.AutoLod,
        RunMode.AutoWar,
        RunMode.AutoBlossom,
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
