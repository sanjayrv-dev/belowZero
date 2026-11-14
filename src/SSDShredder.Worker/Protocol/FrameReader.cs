namespace SSDShredder.Worker.Protocol;

public enum FrameStatus
{
    Frame,

    /// <summary>Clean end of stream between frames.</summary>
    End,

    /// <summary>A line exceeded <see cref="Wire.MaxFrameBytes"/> before its LF.</summary>
    Oversized,

    /// <summary>The stream ended in the middle of a line.</summary>
    Truncated,
}

/// <summary>Splits a byte stream into LF-terminated frames with a hard size limit.</summary>
public sealed class FrameReader
{
    private readonly Stream _stream;
    private readonly byte[] _chunk = new byte[4096];
    private int _pos;
    private int _len;

    public FrameReader(Stream stream) => _stream = stream;

    public FrameStatus Read(out byte[] frame)
    {
        frame = [];
        using var acc = new MemoryStream();
        while (true)
        {
            if (_pos == _len)
            {
                _len = _stream.Read(_chunk, 0, _chunk.Length);
                _pos = 0;
                if (_len == 0) return acc.Length == 0 ? FrameStatus.End : FrameStatus.Truncated;
            }

            int lf = Array.IndexOf(_chunk, (byte)'\n', _pos, _len - _pos);
            int take = (lf >= 0 ? lf : _len) - _pos;
            if (acc.Length + take > Wire.MaxFrameBytes) return FrameStatus.Oversized;
            acc.Write(_chunk, _pos, take);
            _pos += take;
            if (lf >= 0)
            {
                _pos++;
                frame = acc.ToArray();
                return FrameStatus.Frame;
            }
        }
    }
}
