using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using SatoriInsight.Controls;
using SatoriInsight.Models;

namespace SatoriInsight.ViewModels;

public sealed class RegionMapViewModel : INotifyPropertyChanged
{
    private HeapProfile? _snapshot;
    private IReadOnlyList<HeapRegion> _regions = [];
    private HeapRegion? _selectedRegion;
    private HeapObjectInfo[] _regionObjects = [];
    private FreeHole[] _regionHoles = [];
    private int _colorIndex;
    private double _cellSize = 20;
    private string _sourceLabel = "0 regions";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string[] ColorOptions { get; } = Enum.GetNames<RegionColorMode>();

    public IReadOnlyList<HeapRegion> Regions => _regions;

    public bool HasObjectCapture => _snapshot is { MetadataOnly: false };

    public string MapSourceLabel => _sourceLabel;

    public int ColorIndex
    {
        get => _colorIndex;
        set
        {
            if (Set(ref _colorIndex, Math.Clamp(value, 0, 3)))
            {
                Notify(nameof(ColorMode), nameof(ColorLegend), nameof(ColorGradient), nameof(HasColorGradient));
            }
        }
    }

    public RegionColorMode ColorMode => (RegionColorMode)ColorIndex;

    public IReadOnlyList<RegionLegendItem> ColorLegend => RegionPalette.GetLegend(ColorMode);

    public IBrush? ColorGradient => RegionPalette.GetGradient(ColorMode);

    public bool HasColorGradient => ColorGradient is not null;

    public double CellSize
    {
        get => _cellSize;
        set => Set(ref _cellSize, Math.Clamp(value, 4, 36));
    }

    public HeapRegion? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (Set(ref _selectedRegion, value))
            {
                UpdateRegionDetails();
            }
        }
    }

    public void UpdateLive(IReadOnlyList<HeapRegion> regions, RegionMapSampleInfo? sample = null)
    {
        _sourceLabel = $"{regions.Count:N0} regions";

        if (sample is not null)
        {
            _sourceLabel += sample.Error is not null
                ? " · Refresh failed; showing last available data"
                : sample.Complete ? "" : " · Some regions could not be updated";
        }

        SetRegions(regions, resetSelection: false);
    }

    public void SetSnapshot(HeapProfile? snapshot)
    {
        if (ReferenceEquals(_snapshot, snapshot))
        {
            return;
        }

        _snapshot = snapshot;
        var regions = snapshot?.Regions ?? [];
        _sourceLabel = $"{regions.Length:N0} captured regions";
        SetRegions(regions, resetSelection: true);
        Notify(nameof(HasObjectCapture));
    }

    private void SetRegions(IReadOnlyList<HeapRegion> regions, bool resetSelection)
    {
        _regions = regions;
        var selected = resetSelection
            ? _snapshot is { MetadataOnly: true } ? regions.FirstOrDefault() : null
            : regions.FirstOrDefault(region => region.Id == SelectedRegion?.Id) ?? regions.FirstOrDefault();
        var changed = Set(ref _selectedRegion, selected, nameof(SelectedRegion));

        if (changed || resetSelection)
        {
            UpdateRegionDetails();
        }

        Notify(nameof(Regions), nameof(MapSourceLabel));
    }

    private void UpdateRegionDetails()
    {
        _regionObjects = _snapshot?.Objects.Where(obj => obj.RegionId == SelectedRegion?.Id).OrderBy(obj => obj.Address).ToArray() ?? [];
        _regionHoles = _snapshot?.Holes.Where(hole => hole.RegionId == SelectedRegion?.Id).OrderByDescending(hole => hole.Size).ToArray() ?? [];
        Notify(nameof(RegionSummary), nameof(RegionHoles), nameof(RegionLayoutObjects), nameof(RegionLayoutLegend));
    }

    public string RegionSummary
    {
        get
        {
            if (SelectedRegion is not { } region)
            {
                return "Select a region to inspect.";
            }

            var lines = new List<string>
            {
                $"{region.Label} · {region.Kind} · {region.GenerationLabel}",
                $"0x{region.StartAddress:X} – 0x{region.EndAddress:X}"
            };

            if (region.Header is { } header)
            {
                lines.Add($"Page: 0x{header.PageAddress:X} – 0x{header.PageEnd:X}");
                lines.Add($"Queue: {header.Queue?.ToString() ?? "None"} · reusable for: {header.ReusableFor}");
                lines.Add($"Committed: {Format.Bytes(region.CommittedBytes)} · uncommitted: {Format.Bytes(region.ReservedBytes)}");
                lines.Add($"Used range: {Format.Bytes((long)(header.UsedEnd - region.StartAddress))}");
                lines.Add($"Reported occupancy: {header.OccupancyLabel} · reported objects: {header.ObjectCountLabel}");
                lines.Add($"Pinned objects present: {header.HasPinnedObjects}");
                lines.Add("Free space and object liveness unavailable.");
            }
            else if (region.UsedRangeBytes is { } used)
            {
                lines.Add($"Used range: {Format.Bytes(used)} · reserved: {Format.Bytes(region.ReservedBytes)}");
                lines.Add("Object counts and free space unavailable.");
            }
            else if (_snapshot is null)
            {
                lines.Add($"Size: {Format.Bytes(region.ReservedBytes)}");
                lines.Add("Occupancy and free space unavailable.");
            }
            else
            {
                lines.Add($"Committed: {Format.Bytes(region.CommittedBytes)} · reserved (uncommitted): {Format.Bytes(region.ReservedBytes)}");
                lines.Add($"Objects: {region.ObjectCount:N0} · object bytes: {Format.Bytes(region.ObjectBytes)}");
                lines.Add($"Free holes: {Format.Bytes(region.FreeBytes)} · fragmentation: {region.Fragmentation:P1}");
                lines.Add($"Largest hole: {Format.Bytes(region.LargestHole)} · pinned roots: {region.PinnedObjects}");
                lines.Add($"Unparsed address bytes: {Format.Bytes(region.UnparsedBytes)}");
                lines.Add(region.DetailsKnown ? "" : "Partial capture; totals may be incomplete.");
            }

            return string.Join("\n", lines);
        }
    }

    public IReadOnlyList<FreeHole> RegionHoles => _regionHoles;

    public IReadOnlyList<HeapObjectInfo> RegionLayoutObjects => _regionObjects;

    public IReadOnlyList<RegionLayoutLegendItem> RegionLayoutLegend => RegionLayoutPalette.GetLegend(SelectedRegion, HasObjectCapture);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new(name));
        return true;
    }

    private void Notify(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new(name));
        }
    }
}
