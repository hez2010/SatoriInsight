using System.Buffers.Binary;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

/// <summary>Runtime event 239/v0, including the escaped-byte extension added in Satori PR #142.</summary>
public static class ThreadLocalGcParser
{
    // Task GUID describes the payload's GC task; EventPipe routes by the CLR provider GUID.
    public static readonly Guid ProviderGuid = new("e13c0d23-ccbc-4e12-931b-d9cc2eee27e4");
    public static readonly Guid TaskGuid = new("044973cd-251f-4dff-a3e9-9d6307286b05");
    public const int EventId = 239;
    public const int PayloadSize = 34;
    public const int ExtendedPayloadSize = 50;

    public static bool Matches(Guid provider, int eventId) => provider == ProviderGuid && eventId == EventId;

    // Upstream appended two UInt64 fields without changing the event version.
    public static bool IsSupportedPayload(int version, int length) =>
        version == 0 && length is PayloadSize or ExtendedPayloadSize;

    public static GcEvent? Parse(ReadOnlySpan<byte> payload, int version, double endSeconds, int threadId)
    {
        if (!IsSupportedPayload(version, payload.Length) || !double.IsFinite(endSeconds) || endSeconds < 0 || threadId < 0)
        {
            return null;
        }

        var runtime = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]);
        var collected = BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]); // win:Boolean is a 4-byte BOOL.
        var before = BinaryPrimitives.ReadUInt64LittleEndian(payload[10..]);
        var after = BinaryPrimitives.ReadUInt64LittleEndian(payload[18..]);
        var durationNs = BinaryPrimitives.ReadDoubleLittleEndian(payload[26..]);
        ulong? escapedBefore = payload.Length == ExtendedPayloadSize ? BinaryPrimitives.ReadUInt64LittleEndian(payload[34..]) : null;
        ulong? escapedAfter = payload.Length == ExtendedPayloadSize ? BinaryPrimitives.ReadUInt64LittleEndian(payload[42..]) : null;

        if (collected > 1 || !double.IsFinite(durationNs) || durationNs < 0)
        {
            return null;
        }

        return new(
            count, endSeconds, 0, "ThreadLocal", collected == 1 ? "Collected" : "Skipped", durationNs / 1_000_000, 0, 0,
            new(runtime, threadId, collected == 1, before, after, durationNs, escapedBefore, escapedAfter));
    }
}
