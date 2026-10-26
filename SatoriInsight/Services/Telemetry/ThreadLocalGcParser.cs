using System.Buffers.Binary;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

/// <summary>Runtime event 239/v0: GC task 1, opcode 239. All fields are fixed-width.</summary>
public static class ThreadLocalGcParser
{
    // Task GUID describes the payload's GC task; EventPipe routes by the CLR provider GUID.
    public static readonly Guid ProviderGuid = new("e13c0d23-ccbc-4e12-931b-d9cc2eee27e4");
    public static readonly Guid TaskGuid = new("044973cd-251f-4dff-a3e9-9d6307286b05");
    public const int EventId = 239;
    public const int PayloadSize = 34;

    public static bool Matches(Guid provider, int eventId) => provider == ProviderGuid && eventId == EventId;

    public static GcEvent? Parse(ReadOnlySpan<byte> payload, int version, double endSeconds, int threadId)
    {
        if (version != 0 || payload.Length != PayloadSize || !double.IsFinite(endSeconds) || endSeconds < 0 || threadId < 0)
        {
            return null;
        }

        var runtime = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]);
        var collected = BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]); // win:Boolean is a 4-byte BOOL.
        var before = BinaryPrimitives.ReadUInt64LittleEndian(payload[10..]);
        var after = BinaryPrimitives.ReadUInt64LittleEndian(payload[18..]);
        var durationNs = BinaryPrimitives.ReadDoubleLittleEndian(payload[26..]);

        if (collected > 1 || !double.IsFinite(durationNs) || durationNs < 0)
        {
            return null;
        }

        return new(
            count, endSeconds, 0, "ThreadLocal", collected == 1 ? "Collected" : "Skipped", durationNs / 1_000_000, 0, 0,
            new(runtime, threadId, collected == 1, before, after, durationNs));
    }
}
