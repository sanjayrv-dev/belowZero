using System.Text;
using System.Text.Json;
using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker.Tests;

internal sealed class CapturingSink : IFrameSink
{
    private readonly object _gate = new();
    public List<string> Lines { get; } = new();

    public void Write(ReadOnlySpan<byte> line)
    {
        string text = Encoding.UTF8.GetString(line);
        Assert.EndsWith("\n", text);
        lock (_gate) Lines.Add(text.TrimEnd('\n'));
    }

    public JsonElement Json(int index) => JsonDocument.Parse(Lines[index]).RootElement;

    public JsonElement Last => Json(Lines.Count - 1);

    public IEnumerable<string> Phases() =>
        Lines.Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == "progress")
            .Select(e => e.GetProperty("phase").GetString()!);
}

internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ssdshredder-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Frames
{
    public const string Id1 = "ba09878a-81f9-48f4-832a-bef3818e2ddd";
    public const string Id2 = "549556c4-3919-4ac7-bba8-e88d46ba6095";
    public const string Id3 = "31f4fcad-b324-4058-a522-da3c7c443523";

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    public static string Request(string id, string op, string? target, bool confirmed, string? token = null)
    {
        string t = target is null ? "null" : JsonSerializer.Serialize(target);
        string tok = token is null ? "" : $",\"selectionToken\":\"{token}\"";
        return $"{{\"v\":1,\"id\":\"{id}\",\"op\":\"{op}\",\"target\":{t},\"confirmed\":{(confirmed ? "true" : "false")}{tok}}}";
    }
}
