using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

public sealed class DoubleTextConverter : IValueConverter
{
    public double DefaultValue { get; set; }
    public string Format { get; set; } = "0.###";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
            return d.ToString(Format, culture);

        return value?.ToString() ?? DefaultValue.ToString(Format, culture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
            return d;

        if (value is string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return DefaultValue;

            if (double.TryParse(s, NumberStyles.Float, culture, out var parsed))
                return parsed;

            if (!ReferenceEquals(culture, CultureInfo.InvariantCulture) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return parsed;

            return AvaloniaProperty.UnsetValue;
        }

        return AvaloniaProperty.UnsetValue;
    }
}

