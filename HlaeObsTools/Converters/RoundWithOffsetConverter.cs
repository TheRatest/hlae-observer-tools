using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

public class RoundWithOffsetConverter : IValueConverter
{
    public double Offset { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double width)
        {
            var adjusted = width + Offset;
            if (adjusted < 0)
                adjusted = 0;
            return Math.Round(adjusted, MidpointRounding.AwayFromZero);
        }

        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
