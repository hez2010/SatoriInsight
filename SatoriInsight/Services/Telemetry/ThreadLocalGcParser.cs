using System.Buffers.Binary;
using SatoriInsight.Models;

namespace SatoriInsight.Services.Telemetry;

/// <summary>Decodes the packed EventPipe payloads of Satori runtime event 239/v0.</summary>
public static class ThreadLocalGcParser
{
    // Task GUID describes the payload's GC task; EventPipe routes by the CLR provider GUID.
    public static readonly Guid ProviderGuid = new("e13c0d23-ccbc-4e12-931b-d9cc2eee27e4");
    public static readonly Guid TaskGuid = new("044973cd-251f-4dff-a3e9-9d6307286b05");
    public const int EventId = 239;
    public const int PayloadSize = 34;
    public const int ExtendedPayloadSize = 50;
    public const int RequestPayloadSize = 102;

    public static bool Matches(Guid provider, int eventId) => provider == ProviderGuid && eventId == EventId;

    // All three schemas use version zero. The request schema has its own field order.
    public static bool IsSupportedPayload(int version, int length) =>
        version == 0 && length is PayloadSize or ExtendedPayloadSize or RequestPayloadSize;

    public static GcEvent? Parse(ReadOnlySpan<byte> payload, int version, double endSeconds, int threadId)
    {
        if (!IsSupportedPayload(version, payload.Length) || !double.IsFinite(endSeconds) || endSeconds < 0 || threadId < 0)
        {
            return null;
        }

        var runtime = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]);

        if (payload.Length == RequestPayloadSize)
        {
            return ParseRequest(payload, runtime, count, endSeconds, threadId);
        }

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

    private static GcEvent? ParseRequest(ReadOnlySpan<byte> payload, ushort runtime, uint count, double endSeconds, int threadId)
    {
        var outcome = (ThreadLocalOutcome)BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]);
        var trigger = (ThreadLocalTrigger)BinaryPrimitives.ReadUInt32LittleEndian(payload[10..]);
        var retry = (ThreadLocalRetry)BinaryPrimitives.ReadUInt32LittleEndian(payload[14..]);
        var productive = BinaryPrimitives.ReadUInt32LittleEndian(payload[18..]);
        var durationNs = BinaryPrimitives.ReadDoubleLittleEndian(payload[94..]);

        if (!Enum.IsDefined(outcome) || !Enum.IsDefined(trigger) || !Enum.IsDefined(retry)
            || productive > 1 || !double.IsFinite(durationNs) || durationNs < 0)
        {
            return null;
        }

        var request = new ThreadLocalRequestInfo(outcome, trigger, retry, productive == 1,
            BinaryPrimitives.ReadUInt64LittleEndian(payload[22..]), BinaryPrimitives.ReadUInt64LittleEndian(payload[30..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[38..]), BinaryPrimitives.ReadUInt64LittleEndian(payload[46..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[54..]));
        var local = new ThreadLocalGcInfo(runtime, threadId, outcome == ThreadLocalOutcome.Completed,
            BinaryPrimitives.ReadUInt64LittleEndian(payload[62..]), BinaryPrimitives.ReadUInt64LittleEndian(payload[70..]), durationNs,
            BinaryPrimitives.ReadUInt64LittleEndian(payload[78..]), BinaryPrimitives.ReadUInt64LittleEndian(payload[86..]), request);
        return new(count, endSeconds, 0, "ThreadLocal", request.OutcomeLabel, durationNs / 1_000_000, 0, 0, local);
    }
}
