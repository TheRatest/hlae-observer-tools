using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

public sealed class NullToBoolConverter : IValueConverter
{
    public bool TrueWhenNotNull { get; set; } = true;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null;
        return TrueWhenNotNull ? !isNull : isNull;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
