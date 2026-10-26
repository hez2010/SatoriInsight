using Avalonia.Media;
using SatoriInsight.Models;

namespace SatoriInsight.Controls;

public static class GenerationPalette
{
    private static readonly IReadOnlyDictionary<HeapGeneration, Color> Colors = new Dictionary<HeapGeneration, Color>
    {
        [HeapGeneration.Gen0] = Color.Parse("#35D694"),
        [HeapGeneration.Gen1] = Color.Parse("#3EA6FF"),
        [HeapGeneration.Gen2] = Color.Parse("#AD82FF"),
        [HeapGeneration.Loh] = Color.Parse("#FF9B42"),
        [HeapGeneration.Poh] = Color.Parse("#F568D4"),
        [HeapGeneration.Frozen] = Color.Parse("#E8EEF5"),
        [HeapGeneration.Mixed] = Color.Parse("#EEE05A"),
        [HeapGeneration.Unknown] = Color.Parse("#929EAE"),
        [HeapGeneration.Gen3] = Color.Parse("#FF6678"),
        [HeapGeneration.Unassigned] = Color.Parse("#3C485B")
    };

    public static Color Gen0Color => GetColor(HeapGeneration.Gen0);

    public static Color Gen1Color => GetColor(HeapGeneration.Gen1);

    public static Color Gen2Color => GetColor(HeapGeneration.Gen2);

    public static Color GetColor(HeapGeneration generation) => Colors.GetValueOrDefault(generation, Colors[HeapGeneration.Unknown]);
}
