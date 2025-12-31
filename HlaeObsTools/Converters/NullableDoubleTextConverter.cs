using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

public sealed class NullableDoubleTextConverter : IValueConverter
{
    public string Format { get; set; } = "0.###";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return string.Empty;

        if (value is double d)
            return d.ToString(Format, culture);

        return value?.ToString() ?? string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
            return null;

        if (value is double d)
            return d;

        if (value is string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return null;

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
