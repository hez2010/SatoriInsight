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
        context.FillRectangle(RegionLayoutPalette.Unknown, new Rect(Bounds.Size));

        if (region is null || region.EndAddress <= region.StartAddress)
        {
            return;
        }

        var length = (double)(region.EndAddress - region.StartAddress);
        using var clip = context.PushClip(new Rect(Bounds.Size));

        if (region.Header is { } header)
        {
            context.FillRectangle(RegionLayoutPalette.Uncommitted, new Rect(Bounds.Size));
            DrawRange(region.StartAddress, region.CommittedBytes, RegionLayoutPalette.Committed);
            DrawRange(region.StartAddress, (long)(header.UsedEnd - region.StartAddress), RegionLayoutPalette.Used);
            return;
        }

        if (region.UsedRangeBytes is { } used)
        {
            DrawRange(region.StartAddress, used, RegionLayoutPalette.Used);
            return;
        }

        foreach (var obj in Objects ?? [])
        {
            DrawRange(obj.Address, obj.Size, RegionLayoutPalette.Objects);
        }

        foreach (var hole in Holes ?? [])
        {
            DrawRange(hole.Address, hole.Size, RegionLayoutPalette.Free);
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
