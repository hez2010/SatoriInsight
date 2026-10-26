using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SatoriInsight.Models;

namespace SatoriInsight.Controls;

public sealed class FragmentStrip : Control
{
    public static readonly StyledProperty<HeapRegion?> RegionProperty = AvaloniaProperty.Register<FragmentStrip, HeapRegion?>(nameof(Region));
    public static readonly StyledProperty<IReadOnlyList<FreeHole>?> HolesProperty = AvaloniaProperty.Register<FragmentStrip, IReadOnlyList<FreeHole>?>(nameof(Holes));
    public static readonly StyledProperty<IReadOnlyList<HeapObjectInfo>?> ObjectsProperty = AvaloniaProperty.Register<FragmentStrip, IReadOnlyList<HeapObjectInfo>?>(nameof(Objects));

    public HeapRegion? Region
    {
        get => GetValue(RegionProperty);
        set => SetValue(RegionProperty, value);
    }

    public IReadOnlyList<FreeHole>? Holes
    {
        get => GetValue(HolesProperty);
        set => SetValue(HolesProperty, value);
    }

    public IReadOnlyList<HeapObjectInfo>? Objects
    {
        get => GetValue(ObjectsProperty);
        set => SetValue(ObjectsProperty, value);
    }

    static FragmentStrip() => AffectsRender<FragmentStrip>(RegionProperty, HolesProperty, ObjectsProperty);

    public override void Render(DrawingContext context)
    {
        var region = Region;
        context.FillRectangle(Brush.Parse("#39465B"), new Rect(Bounds.Size));

        if (region is null || region.EndAddress <= region.StartAddress)
        {
            return;
        }

        var length = (double)(region.EndAddress - region.StartAddress);
        using var clip = context.PushClip(new Rect(Bounds.Size));

        if (region.Header is { } header)
        {
            context.FillRectangle(Brush.Parse("#202734"), new Rect(Bounds.Size));
            DrawRange(region.StartAddress, region.CommittedBytes, Brush.Parse("#596B84"));
            DrawRange(region.StartAddress, (long)(header.UsedEnd - region.StartAddress), Brush.Parse("#69B7DF"));
            return;
        }

        if (region.UsedRangeBytes is { } used)
        {
            DrawRange(region.StartAddress, used, Brush.Parse("#69B7DF"));
            return;
        }

        var objects = Brush.Parse("#73D5B0");
        var holes = Brush.Parse("#E6B56E");

        foreach (var obj in Objects ?? [])
        {
            DrawRange(obj.Address, obj.Size, objects);
        }

        foreach (var hole in Holes ?? [])
        {
            DrawRange(hole.Address, hole.Size, holes);
        }

        void DrawRange(ulong address, long size, IBrush brush)
        {
            if (address < region.StartAddress || address >= region.EndAddress || size <= 0)
            {
                return;
            }

            context.FillRectangle(brush, new Rect(
                (address - region.StartAddress) / length * Bounds.Width, 0, Math.Max(1, size / length * Bounds.Width), Bounds.Height));
        }
    }
}
