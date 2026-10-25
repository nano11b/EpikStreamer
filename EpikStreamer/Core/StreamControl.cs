using System.Globalization;
using NetMQ;
using NetMQ.Sockets;
namespace EpikStreamer.Core;

/// <summary>Sends runtime commands (volume, overlay visibility) to the running encoder's azmq/zmq filters.</summary>
public sealed class StreamControl : IDisposable
{
    readonly string _endpoint; RequestSocket? _s; readonly object _lk = new();
    public StreamControl(int port) => _endpoint = $"tcp://127.0.0.1:{port}";

    /// <summary>Returns ffmpeg's reply ("0 Success" on success) or null on timeout/failure.</summary>
    public string? Send(string target, string command, string arg)
    {
        lock (_lk)
        {
            try
            {
                _s ??= Connect();
                if (!_s.TrySendFrame(TimeSpan.FromSeconds(1), $"{target} {command} {arg}")) { Reset(); return null; }
                if (_s.TryReceiveFrameString(TimeSpan.FromSeconds(2), out var r)) return r;
                Reset(); return null;   // REQ socket is stuck after a missed reply => recreate
            }
            catch { Reset(); return null; }
        }
    }
    public bool SetVolume(string name, double v) => Send("volume@" + name, "volume", v.ToString("0.###", CultureInfo.InvariantCulture))?.StartsWith("0 ") == true;

    RequestSocket Connect() { var s = new RequestSocket(); s.Options.Linger = TimeSpan.Zero; s.Connect(_endpoint); return s; }
    void Reset() { try { _s?.Dispose(); } catch { } _s = null; }
    public void Dispose() { lock (_lk) Reset(); }

    public static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
}
