using System.Buffers.Binary;
using SatoriInsight.Models;
using SatoriInsight.Services.Recording;
using SatoriInsight.Services.Telemetry;
using SatoriInsight.ViewModels;
using Xunit;

namespace SatoriInsight.Tests;

public class ThreadLocalGcParserTests
{
    // Event 239's packed wire format from ClrEtwAll.man. PR #142 appends two
    // UInt64s to the original v0 record; it does not introduce a new version.
    private const string Original = "030007000000010000000000200000000000000008000000000000000000006ac840";
    private const string EscapedTotals = "00000100000000000080010000000000";

    [Theory]
    [InlineData(ThreadLocalOutcome.EscapedLimit, "Escape limit", false)]
    [InlineData(ThreadLocalOutcome.InsufficientProgress, "Insufficient progress", false)]
    [InlineData(ThreadLocalOutcome.SurvivalAbort, "Survival abort", true)]
    [InlineData(ThreadLocalOutcome.Completed, "Completed", true)]
    public async Task RequestOutcomesPreserveDiagnosticsAndCollectionCounts(ThreadLocalOutcome outcome, string reason, bool indexed)
    {
        // Count zero also occurs when the 32-bit collection index wraps.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)3);
        writer.Write(0u);
        writer.Write((uint)outcome);
        writer.Write(0u); // Window limit
        writer.Write(1u); // Retry succeeded
        writer.Write(1u); // Tracking productive
        foreach (var field in new ulong[] { 0x1_4000_0000, 64, 4096, 100, 200, 400, 300, 16, 32 })
        {
            writer.Write(field);
        }

        writer.Write(indexed ? 12500d : 0d);
        var collection = Assert.IsType<GcEvent>(ThreadLocalGcParser.Parse(stream.ToArray(), 0, 2, 123));
        Assert.Equal(indexed ? "#0" : "—", collection.NumberLabel);
        Assert.Equal(reason, collection.Reason);
        Assert.Equal(indexed ? 0.0125 : 0, collection.DurationMilliseconds);
        var request = new ThreadLocalRequestInfo(outcome, ThreadLocalTrigger.WindowLimit, ThreadLocalRetry.Succeeded,
            true, 0x1_4000_0000, 64, 4096, 100, 200);
        Assert.Equal(new ThreadLocalGcInfo(3, 123, outcome == ThreadLocalOutcome.Completed, 400, 300,
            indexed ? 12500 : 0, 16, 32, request), collection.ThreadLocal);

        var buffer = new LiveTelemetryBuffer();
        buffer.ThreadLocalCollection(collection);
        var frame = buffer.Read(3);
        Assert.Equal(indexed ? 1 : 0, frame.GcCounts!.Gen0);
        Assert.Equal(collection, Assert.Single(frame.Events));
        var session = new SavedSession(SessionStorage.CurrentSchemaVersion, "local-eventpipe", null, 3, [],
            [collection], null, frame.Health, [], [], [], null, frame.GcCounts, [], []);
        await using var vm = new MainViewModel();
        await vm.ImportSessionAsync(SessionStorage.Write(session));
        vm.SelectPeriod(1, 3);
        vm.SelectedEvent = Assert.Single(vm.EventLog);
        Assert.Equal(request, vm.SelectedEvent.ThreadLocal!.Request);
        Assert.Equal(indexed ? "1" : "0", vm.Gen0CountLabel);
        Assert.Equal("—", vm.EventPause);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothV0FormatsKeepTheOriginalCollectionFields(bool extended)
    {
        var payload = Convert.FromHexString(Original + (extended ? EscapedTotals : ""));
        var collection = ThreadLocalGcParser.Parse(payload, 0, 2, 123);
        Assert.NotNull(collection);
        Assert.Equal(7, collection.Number);
        Assert.Equal(2, collection.EndSeconds);
        Assert.Equal(0, collection.Generation);
        Assert.Equal("Collected", collection.Reason);
        Assert.Equal(0.0125, collection.DurationMilliseconds);
        Assert.Equal(0, collection.PauseMilliseconds);
        Assert.Equal(0, collection.SuspendMilliseconds);
        var local = collection.ThreadLocal!;
        Assert.Equal(3, local.ClrInstanceId);
        Assert.Equal(123, local.ThreadId);
        Assert.True(local.Collected);
        Assert.Equal(2097152UL, local.OccupancyBefore);
        Assert.Equal(524288UL, local.OccupancyAfter);
        Assert.Equal(12500, local.DurationNanoseconds);
        Assert.Equal(extended, local.HasEscapedBytes);
        Assert.Equal(extended ? 65536UL : (ulong?)null, local.EscapedBytesBefore);
        Assert.Equal(extended ? 98304UL : (ulong?)null, local.EscapedBytesAfter);
    }

    [Fact]
    public void AShortEventInAReusedBufferDoesNotInheritEscapedTotals()
    {
        var buffer = Convert.FromHexString(Original + EscapedTotals);
        Assert.True(ThreadLocalGcParser.Parse(buffer, 0, 1, 123)!.ThreadLocal!.HasEscapedBytes);
        Convert.FromHexString(Original).CopyTo(buffer, 0);
        var legacy = ThreadLocalGcParser.Parse(buffer.AsSpan(0, ThreadLocalGcParser.PayloadSize), 0, 2, 123)!;
        Assert.Null(legacy.ThreadLocal!.EscapedBytesBefore);
        Assert.Null(legacy.ThreadLocal.EscapedBytesAfter);
    }

    [Fact]
    public void ZeroAndLargeEscapedTotalsAreAvailableUnsignedValuesEvenWhenSkipped()
    {
        var payload = Convert.FromHexString(Original + EscapedTotals);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(34), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(42), ulong.MaxValue);
        var collection = ThreadLocalGcParser.Parse(payload, 0, 1, 123)!;
        Assert.Equal("Skipped", collection.Reason);
        Assert.True(collection.ThreadLocal!.HasEscapedBytes);
        Assert.Equal(0UL, collection.ThreadLocal.EscapedBytesBefore);
        Assert.Equal(ulong.MaxValue, collection.ThreadLocal.EscapedBytesAfter);
        Assert.Equal("0 B", collection.ThreadLocal.EscapedBeforeLabel);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 33)]
    [InlineData(0, 35)]
    [InlineData(0, 42)]
    [InlineData(0, 49)]
    [InlineData(0, 51)]
    [InlineData(0, 101)]
    [InlineData(0, 103)]
    [InlineData(1, 102)]
    [InlineData(1, 34)]
    [InlineData(1, 50)]
    public void TruncatedUnknownAndFutureFormatsAreRejected(int version, int length)
    {
        Assert.False(ThreadLocalGcParser.IsSupportedPayload(version, length));
        Assert.Null(ThreadLocalGcParser.Parse(new byte[length], version, 1, 123));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothFormatsRejectInvalidBooleanDurationAndEventContext(bool extended)
    {
        var payload = Convert.FromHexString(Original + (extended ? EscapedTotals : ""));
        Assert.Null(ThreadLocalGcParser.Parse(payload, 0, double.NaN, 123));
        Assert.Null(ThreadLocalGcParser.Parse(payload, 0, -1, 123));
        Assert.Null(ThreadLocalGcParser.Parse(payload, 0, 1, -1));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6), 2);
        Assert.Null(ThreadLocalGcParser.Parse(payload, 0, 1, 123));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6), 1);

        foreach (var duration in new[] { -1d, double.NaN, double.PositiveInfinity })
        {
            BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(26), duration);
            Assert.Null(ThreadLocalGcParser.Parse(payload, 0, 1, 123));
        }
    }

    [Fact]
    public void OnlyTheRuntimeProviderAndThreadLocalEventIdMatch()
    {
        Assert.True(ThreadLocalGcParser.Matches(ThreadLocalGcParser.ProviderGuid, 239));
        Assert.False(ThreadLocalGcParser.Matches(ThreadLocalGcParser.TaskGuid, 239));
        Assert.False(ThreadLocalGcParser.Matches(ThreadLocalGcParser.ProviderGuid, 238));
    }
}
