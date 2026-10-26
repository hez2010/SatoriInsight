using System.ComponentModel;
using SatoriInsight.Models;
using SatoriInsight.Services.Heap;

namespace SatoriInsight.ViewModels;

public sealed partial class MainViewModel
{
    private readonly Stack<HeapObjectInfo> _objectHistory = new();
    private CancellationTokenSource? _rootSearchCancellation;
    private Task _rootSearchTask = Task.CompletedTask;
    private bool _isFindingRoot;
    private bool _isRegionInspection;
    private string _rootPathStatus = "Select an object to see its root path.";

    public bool HasSelectedObject => SelectedObject is not null;

    public bool HasHeapFilter => SelectedType is not null || _objectRegion is not null || !string.IsNullOrEmpty(TypeSearch);

    public bool CanGoBackToObject => _objectHistory.Count > 0;

    public bool IsFindingRoot => _isFindingRoot;

    public string RootPathStatus => _rootPathStatus;

    public bool IsRegionInspection
    {
        get => _isRegionInspection;
        private set => Set(ref _isRegionInspection, value);
    }

    public void ShowObjectInspection()
    {
        if (HasObjectCapture)
        {
            IsRegionInspection = false;
        }
    }

    public void GoBackToObject()
    {
        if (_objectHistory.TryPop(out var previous))
        {
            InspectObject(previous);
            Notify(nameof(CanGoBackToObject));
        }
    }

    private void CapturedRegionMap_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RegionMapViewModel.SelectedRegion))
        {
            return;
        }

        var region = CapturedRegionMap.SelectedRegion;
        if (_objectRegion == region?.Id)
        {
            return;
        }

        _objectRegion = region?.Id;
        _objectHistory.Clear();
        var typeName = SelectedType?.Name;
        Notify(nameof(Types));
        SelectedType = ScopedTypes.FirstOrDefault(type => type.Name == typeName);
        NotifyObjects();
        IsRegionInspection = region is not null || !HasObjectCapture;
    }

    private void StartRootSearch()
    {
        _rootSearchCancellation?.Cancel();
        _rootSearchCancellation = null;
        _rootPathStatus = "Select an object to see its root path.";
        _isFindingRoot = false;

        if (!_disposed && SelectedSnapshot is { MetadataOnly: false } profile && SelectedObject is { } obj)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _rootSearchCancellation = cancellation;
            _isFindingRoot = true;
            _rootPathStatus = "Finding root path…";
            _rootSearchTask = FindSelectedRootAsync(profile, obj, cancellation);
        }

        Notify(nameof(IsFindingRoot), nameof(RootPathStatus));
    }

    private async Task FindSelectedRootAsync(HeapProfile profile, HeapObjectInfo obj, CancellationTokenSource cancellation)
    {
        try
        {
            // Avoid starting a graph walk for every row passed during keyboard navigation.
            await Task.Delay(100, cancellation.Token);
            var path = await Task.Run(() => HeapAnalysis.FindRootPath(profile, obj.Address, cancellation.Token), cancellation.Token);

            if (ReferenceEquals(_rootSearchCancellation, cancellation) && SelectedSnapshot == profile && SelectedObject == obj)
            {
                _rootPath = path;
                _rootPathStatus = path.Length > 0 ? $"{path.Length:N0} objects in root path" : "No root path found in this capture.";
                Notify(nameof(RootPath), nameof(RootPathStatus));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_rootSearchCancellation, cancellation))
            {
                _rootPathStatus = $"Root path unavailable: {exception.Message}";
                Notify(nameof(RootPathStatus));
            }
        }
        finally
        {
            if (ReferenceEquals(_rootSearchCancellation, cancellation))
            {
                _rootSearchCancellation = null;
                _isFindingRoot = false;
                Notify(nameof(IsFindingRoot));
            }

            cancellation.Dispose();
        }
    }
}
