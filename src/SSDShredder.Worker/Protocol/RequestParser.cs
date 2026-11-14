using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace SSDShredder.Worker.Protocol;

public sealed record WireRequest(string Id, string Op, string? Target, bool Confirmed, string? SelectionToken);

public enum ParseStatus
{
    /// <summary>Well-formed request.</summary>
    Ok,

    /// <summary>The frame carries a trustworthy request ID but breaks the schema: answer with code 2.</summary>
    Reject,

    /// <summary>No trustworthy request ID: close the connection without executing anything.</summary>
    Fatal,
}

public readonly record struct ParseResult(ParseStatus Status, WireRequest? Request, string? Id, string? Error)
{
    public static ParseResult Fatal(string error) => new(ParseStatus.Fatal, null, null, error);

    public static ParseResult Reject(string id, string error) => new(ParseStatus.Reject, null, id, error);
}

/// <summary>Strict parser for one v1 request frame (the bytes of a line, without the LF).</summary>
public static class RequestParser
{
    private enum Kind { String, Number, True, False, Null, Other }

    private readonly record struct Value(Kind Kind, string? Text);

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "v", "id", "op", "target", "confirmed", "selectionToken",
    };

    public static ParseResult Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length > Wire.MaxFrameBytes) return ParseResult.Fatal("frame too large");
        if (!Utf8.IsValid(frame)) return ParseResult.Fatal("invalid UTF-8");

        var props = new Dictionary<string, Value>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(frame, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 8,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return ParseResult.Fatal("frame is not a JSON object");
            }

            while (true)
            {
                if (!reader.Read()) return ParseResult.Fatal("truncated object");
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return ParseResult.Fatal("malformed object");

                string name = reader.GetString()!;
                if (!reader.Read()) return ParseResult.Fatal("truncated object");

                Value value;
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        value = new Value(Kind.String, reader.GetString());
                        break;
                    case JsonTokenType.Number:
                        value = new Value(Kind.Number, Encoding.ASCII.GetString(reader.ValueSpan));
                        break;
                    case JsonTokenType.True:
                        value = new Value(Kind.True, null);
                        break;
                    case JsonTokenType.False:
                        value = new Value(Kind.False, null);
                        break;
                    case JsonTokenType.Null:
                        value = new Value(Kind.Null, null);
                        break;
                    default:
                        reader.Skip();
                        value = new Value(Kind.Other, null);
                        break;
                }

                if (!props.TryAdd(name, value)) return ParseResult.Fatal("duplicate key");
            }

            if (reader.Read()) return ParseResult.Fatal("trailing data");
        }
        catch (JsonException)
        {
            return ParseResult.Fatal("malformed JSON");
        }
        catch (InvalidOperationException)
        {
            return ParseResult.Fatal("malformed JSON string");
        }

        if (!props.TryGetValue("id", out Value idValue) || idValue.Kind != Kind.String || !Validation.IsCanonicalUuid(idValue.Text))
        {
            return ParseResult.Fatal("no trustworthy request id");
        }
        string id = idValue.Text!;

        foreach (string key in props.Keys)
        {
            if (!AllowedKeys.Contains(key)) return ParseResult.Reject(id, "unknown field");
        }

        if (!props.TryGetValue("v", out Value v) || v.Kind != Kind.Number || v.Text != "1")
        {
            return ParseResult.Reject(id, "unsupported protocol version");
        }
        if (!props.TryGetValue("op", out Value op) || op.Kind != Kind.String || !Ops.IsKnown(op.Text!))
        {
            return ParseResult.Reject(id, "unknown op");
        }
        if (!props.TryGetValue("target", out Value target) || target.Kind is not (Kind.String or Kind.Null))
        {
            return ParseResult.Reject(id, "target must be a string or null");
        }
        if (!props.TryGetValue("confirmed", out Value confirmedValue) || confirmedValue.Kind is not (Kind.True or Kind.False))
        {
            return ParseResult.Reject(id, "confirmed must be a boolean");
        }
        string? token = null;
        if (props.TryGetValue("selectionToken", out Value tokenValue))
        {
            if (tokenValue.Kind != Kind.String) return ParseResult.Reject(id, "selectionToken must be a string");
            token = tokenValue.Text;
        }

        var request = new WireRequest(id, op.Text!, target.Text, confirmedValue.Kind == Kind.True, token);
        string? error = ValidateSemantics(request);
        return error is null
            ? new ParseResult(ParseStatus.Ok, request, id, null)
            : ParseResult.Reject(id, error);
    }

    private static string? ValidateSemantics(WireRequest r)
    {
        if (r.SelectionToken is not null)
        {
            if (r.Op != Ops.Shred) return "selectionToken is only valid for shred";
            if (!Validation.IsLowerHex32(r.SelectionToken)) return "selectionToken must be 32 lowercase hex characters";
        }

        switch (r.Op)
        {
            case Ops.Inspect:
                if (!Validation.IsAbsoluteLocalFilePath(r.Target)) return "target must be an absolute local file path";
                if (r.Confirmed) return "inspect does not accept confirmed=true";
                return null;
            case Ops.Shred:
                if (!Validation.IsAbsoluteLocalFilePath(r.Target)) return "target must be an absolute local file path";
                if (!r.Confirmed) return "shred requires confirmed=true";
                return null;
            case Ops.VaultInit:
                if (r.Target is not null) return "vault-init takes a null target";
                if (r.Confirmed) return "vault-init does not accept confirmed=true";
                return null;
            case Ops.VaultStatus:
                if (!Validation.IsCanonicalUuid(r.Target)) return "target must be a vault UUID";
                if (r.Confirmed) return "vault-status does not accept confirmed=true";
                return null;
            case Ops.VaultClose:
                if (!Validation.IsCanonicalUuid(r.Target)) return "target must be a vault UUID";
                if (!r.Confirmed) return "vault-close requires confirmed=true";
                return null;
            case Ops.ProbeVolume:
                if (!Validation.IsVolumeGuidPath(r.Target)) return "target must be a canonical volume GUID path";
                if (r.Confirmed) return "probe-volume does not accept confirmed=true";
                return null;
            default:
                return "unknown op";
        }
    }
}
