using System.Text.Encodings.Web;
using System.Text.Json;

namespace SSDShredder.Worker.Protocol;

/// <summary>Receives complete NDJSON lines (including the LF).</summary>
public interface IFrameSink
{
    void Write(ReadOnlySpan<byte> line);
}

public sealed class StreamFrameSink : IFrameSink
{
    private readonly Stream _stream;
    private readonly object _gate = new();

    public StreamFrameSink(Stream stream) => _stream = stream;

    public void Write(ReadOnlySpan<byte> line)
    {
        lock (_gate)
        {
            _stream.Write(line);
            _stream.Flush();
        }
    }
}

public interface IEvidence
{
    void WriteTo(Utf8JsonWriter w);
}

/// <summary>
/// Emits the events of one request: progress events, then exactly one terminal result.
/// Transport failures never abort the operation that is reporting.
/// </summary>
public sealed class RequestEmitter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IFrameSink _sink;
    private readonly object _gate = new();
    private int _seq;

    public RequestEmitter(IFrameSink sink, string id)
    {
        _sink = sink;
        Id = id;
    }

    public string Id { get; }

    /// <summary>True once the terminal result has been produced.</summary>
    public bool Completed { get; private set; }

    public ExitCode? ResultCode { get; private set; }

    /// <summary>True if any event could not be delivered.</summary>
    public bool TransportLost { get; private set; }

    public void Progress(string phase)
    {
        lock (_gate)
        {
            if (Completed) return;
            Send(w =>
            {
                w.WriteString("type", "progress");
                w.WriteString("phase", phase);
            });
        }
    }

    public ExitCode Result(ExitCode code, IEvidence? evidence)
    {
        lock (_gate)
        {
            if (Completed) throw new InvalidOperationException("A terminal result was already emitted.");
            Completed = true;
            ResultCode = code;
            Send(w =>
            {
                w.WriteString("type", "result");
                w.WriteNumber("code", (int)code);
                w.WriteString("state", Wire.StateName(code));
                w.WritePropertyName("evidence");
                if (evidence is null) w.WriteNullValue();
                else
                {
                    w.WriteStartObject();
                    evidence.WriteTo(w);
                    w.WriteEndObject();
                }
            });
            return code;
        }
    }

    private void Send(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Wire.Version);
            w.WriteString("id", Id);
            w.WriteNumber("seq", ++_seq);
            body(w);
            w.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');
        try
        {
            _sink.Write(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            TransportLost = true;
        }
    }
}

internal static class JsonWriterExtensions
{
    public static void WriteNullable(this Utf8JsonWriter w, string name, long? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteNumber(name, value.Value);
    }

    public static void WriteNullable(this Utf8JsonWriter w, string name, int? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteNumber(name, value.Value);
    }

    public static void WriteNullable(this Utf8JsonWriter w, string name, bool? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteBoolean(name, value.Value);
    }
}

public sealed record InspectEvidence(
    bool Eligible, string Reason, string? Bus, long? CoveredBytes, long? TailBytes, string? SelectionToken = null) : IEvidence
{
    public void WriteTo(Utf8JsonWriter w)
    {
        w.WriteBoolean("eligible", Eligible);
        w.WriteString("reason", Reason);
        w.WriteString("bus", Bus);
        w.WriteNullable("coveredBytes", CoveredBytes);
        w.WriteNullable("tailBytes", TailBytes);
        if (SelectionToken is not null) w.WriteString("selectionToken", SelectionToken);
    }
}

public sealed record ShredEvidence(
    int? RangesRequested, int? RangesProcessed, long? CoveredBytes, long? TailBytes, string Entry) : IEvidence
{
    public const string EntryPreserved = "preserved";
    public const string EntryRemoved = "removed";
    public const string EntryUnknown = "unknown";

    public void WriteTo(Utf8JsonWriter w)
    {
        w.WriteNullable("rangesRequested", RangesRequested);
        w.WriteNullable("rangesProcessed", RangesProcessed);
        w.WriteNullable("coveredBytes", CoveredBytes);
        w.WriteNullable("tailBytes", TailBytes);
        w.WriteString("entry", Entry);
    }
}

public sealed record VaultReadyFacts(
    string Conversion, bool ProtectionOn, int[] ProtectorTypes, bool AutoUnlock, int EncryptionMethod);

public sealed record VaultEvidence(
    string? VaultId, string Phase, string? Mount, bool? LockedObserved, VaultReadyFacts? Ready = null) : IEvidence
{
    public void WriteTo(Utf8JsonWriter w)
    {
        w.WriteString("vaultId", VaultId);
        w.WriteString("phase", Phase);
        w.WriteString("mount", Mount);
        w.WriteNullable("lockedObserved", LockedObserved);
        if (Ready is not null)
        {
            w.WriteString("conversion", Ready.Conversion);
            w.WriteBoolean("protectionOn", Ready.ProtectionOn);
            w.WriteStartArray("protectorTypes");
            foreach (int t in Ready.ProtectorTypes) w.WriteNumberValue(t);
            w.WriteEndArray();
            w.WriteBoolean("autoUnlock", Ready.AutoUnlock);
            w.WriteNumber("encryptionMethod", Ready.EncryptionMethod);
        }
    }
}

public sealed record ProbeEvidence(
    string VolumeGuid, int? DeviceCount, string? Bus, bool? TrimEnabled, bool? SeekPenalty) : IEvidence
{
    public void WriteTo(Utf8JsonWriter w)
    {
        w.WriteString("volumeGuid", VolumeGuid);
        w.WriteNullable("deviceCount", DeviceCount);
        w.WriteString("bus", Bus);
        w.WriteNullable("trimEnabled", TrimEnabled);
        w.WriteNullable("seekPenalty", SeekPenalty);
    }
}
