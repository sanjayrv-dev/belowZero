using System.Security.Cryptography;
using System.Text;
using SSDShredder.Worker.CoreAdapter;

namespace SSDShredder.Worker.Operations;

public enum TakeStatus
{
    Ok,

    /// <summary>No selection is held on this connection.</summary>
    None,
    Expired,
    Mismatch,
}

/// <summary>
/// Binds one held file to a single-use token for one connection. The selection is released
/// on completion, timeout, a failed attempt, or when the connection goes away.
/// </summary>
public sealed class SelectionTokenStore : IDisposable
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly object _gate = new();
    private Selection? _current;

    private sealed record Selection(string Path, string Token, IHeldFile Held, DateTimeOffset Expires, ITimer Timer);

    public SelectionTokenStore(TimeProvider? time = null, TimeSpan? lifetime = null)
    {
        _time = time ?? TimeProvider.System;
        _lifetime = lifetime ?? DefaultLifetime;
    }

    public bool HasSelection { get { lock (_gate) return _current is not null; } }

    /// <summary>Holds the file and returns its 32-character lowercase hex token.</summary>
    public string Hold(string path, IHeldFile held)
    {
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            ReleaseLocked();
            ITimer timer = _time.CreateTimer(_ => Sweep(), null, _lifetime, Timeout.InfiniteTimeSpan);
            _current = new Selection(path, token, held, _time.GetUtcNow() + _lifetime, timer);
        }
        return token;
    }

    /// <summary>
    /// Consumes the selection. On Ok the caller owns the held file. Every other outcome
    /// releases the selection.
    /// </summary>
    public TakeStatus Take(string path, string token, out IHeldFile? held)
    {
        held = null;
        lock (_gate)
        {
            Selection? s = _current;
            if (s is null) return TakeStatus.None;

            if (_time.GetUtcNow() >= s.Expires)
            {
                ReleaseLocked();
                return TakeStatus.Expired;
            }

            bool tokenOk = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(s.Token), Encoding.ASCII.GetBytes(token));
            if (!tokenOk || !string.Equals(s.Path, path, StringComparison.Ordinal))
            {
                ReleaseLocked();
                return TakeStatus.Mismatch;
            }

            s.Timer.Dispose();
            _current = null;
            held = s.Held;
            return TakeStatus.Ok;
        }
    }

    /// <summary>Releases the selection if its lifetime has passed.</summary>
    public void Sweep()
    {
        lock (_gate)
        {
            if (_current is not null && _time.GetUtcNow() >= _current.Expires) ReleaseLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate) ReleaseLocked();
    }

    private void ReleaseLocked()
    {
        Selection? s = _current;
        if (s is null) return;
        _current = null;
        s.Timer.Dispose();
        s.Held.Dispose();
    }
}
