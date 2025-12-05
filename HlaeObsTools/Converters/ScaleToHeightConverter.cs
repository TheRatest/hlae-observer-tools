using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

public class ScaleToHeightConverter : IValueConverter
{
    public double BaseWidth { get; set; } = 260;
    public double AspectRatio { get; set; } = 16.0 / 9.0;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double scale)
        {
            var width = BaseWidth * scale;
            return width / AspectRatio;
        }

        return BaseWidth / AspectRatio;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
