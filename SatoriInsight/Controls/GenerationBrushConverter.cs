using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SatoriInsight.Models;

namespace SatoriInsight.Controls;

public sealed class GenerationBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(GenerationPalette.GetColor(value is HeapGeneration generation ? generation : HeapGeneration.Unknown));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
