using System;
using System.Globalization;
using Avalonia.Data.Converters;
using System.Collections.Generic;
using System.Linq;

namespace HlaeObsTools.Converters;

public class ProgressBarWidthConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 &&
            values[0] is double progress &&
            values[1] is double totalWidth)
        {
            return progress * totalWidth;
        }

        return 0.0;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
