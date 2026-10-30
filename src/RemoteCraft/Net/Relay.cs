using System.Net.Sockets;
using System.Text;

namespace RemoteCraft.Net;

public static class Endpoint
{
    public static (string host, int port) Parse(string s, int defaultPort)
    {
        s = s.Trim();
        if (s.StartsWith('['))
        {
            int end = s.IndexOf(']');
            var h = s[1..end];
            return (h, end + 2 < s.Length && s[end + 1] == ':' ? int.Parse(s[(end + 2)..]) : defaultPort);
        }
        int colon = s.LastIndexOf(':');
        if (colon > 0 && s.IndexOf(':') == colon && int.TryParse(s[(colon + 1)..], out var port))
            return (s[..colon], port);
        return (s, defaultPort);
    }

    public static Stream Connect(string host, int port, int timeoutMs = 6000)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            if (!client.ConnectAsync(host, port).Wait(timeoutMs))
                throw new IOException($"Timed out connecting to {host}:{port}.");
        }
        catch (AggregateException ae)
        {
            client.Dispose();
            throw new IOException(ae.InnerException?.Message ?? ae.Message);
        }
        catch { client.Dispose(); throw; }
        return client.GetStream();
    }

    public static string ReadLine(Stream s, int max = 256)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < max)
        {
            if (s.Read(one, 0, 1) != 1) throw new IOException("Connection closed.");
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
        throw new IOException("Line too long.");
    }

    public static void WriteLine(Stream s, string line)
    {
        var b = Encoding.ASCII.GetBytes(line + "\n");
        s.Write(b);
        s.Flush();
    }
}

/// <summary>
/// Keeps this desk registered at a RemoteCraft relay so it can be reached by id from outside the LAN.
/// The relay only pairs sockets; the session itself stays end-to-end encrypted.
/// </summary>
public sealed class RelayLink : IDisposable
{
    public const int DefaultPort = 7180;

    readonly string _server;
    readonly string _id;
    readonly Action<Stream, string> _onIncoming;
    volatile bool _stopped;
    Stream _control;

    public string Status { get; private set; } = "Connecting to relay…";
    public bool Online { get; private set; }
    public event Action StatusChanged;

    public RelayLink(string server, string id, Action<Stream, string> onIncoming)
    {
        _server = server;
        _id = id;
        _onIncoming = onIncoming;
        new Thread(Run) { IsBackground = true, Name = "RelayLink" }.Start();
    }

    void SetStatus(string s, bool online)
    {
        Status = s;
        Online = online;
        StatusChanged?.Invoke();
    }

    void Run()
    {
        while (!_stopped)
        {
            try
            {
                var (host, port) = Endpoint.Parse(_server, DefaultPort);
                _control = Endpoint.Connect(host, port);
                Endpoint.WriteLine(_control, "REG " + _id);
                var reply = Endpoint.ReadLine(_control);
                if (reply != "REGISTERED") throw new IOException(reply);
                SetStatus($"Relay {_server} online", true);

                var ping = new System.Threading.Timer(_ => { try { Endpoint.WriteLine(_control, "PING"); } catch { } }, null, 25000, 25000);
                try
                {
                    while (!_stopped)
                    {
                        var line = Endpoint.ReadLine(_control);
                        if (line.StartsWith("CALL "))
                        {
                            var token = line[5..];
                            Task.Run(() => Answer(host, port, token));
                        }
                    }
                }
                finally { ping.Dispose(); }
            }
            catch (Exception ex)
            {
                if (_stopped) break;
                SetStatus("Relay unreachable: " + ex.Message, false);
            }
            try { _control?.Dispose(); } catch { }
            for (int i = 0; i < 50 && !_stopped; i++) Thread.Sleep(100);
        }
    }

    void Answer(string host, int port, string token)
    {
        try
        {
            var s = Endpoint.Connect(host, port);
            Endpoint.WriteLine(s, "ANSWER " + token);
            _onIncoming(s, "relay");
        }
        catch { }
    }

    /// <summary>Asks the relay to pair us with the desk <paramref name="id"/>.</summary>
    public static Stream Connect(string server, string id)
    {
        var (host, port) = Endpoint.Parse(server, DefaultPort);
        var s = Endpoint.Connect(host, port);
        Endpoint.WriteLine(s, "CONNECT " + id);
        var reply = Endpoint.ReadLine(s);
        if (reply == "OK") return s;
        s.Dispose();
        throw new IOException(reply == "OFFLINE" ? "The remote desk is offline (not registered at the relay)." : "Relay: " + reply);
    }

    public void Dispose()
    {
        _stopped = true;
        try { _control?.Dispose(); } catch { }
    }
}

public static class Connector
{
    /// <summary>
    /// Opens a raw stream to a desk. Desk ids are looked up on the LAN first, then through the relay;
    /// anything else is treated as host[:port].
    /// </summary>
    public static (Stream stream, string via) Open(string address, Discovery discovery, Settings settings)
    {
        var addr = address.Trim();
        if (DeskId.IsId(addr))
        {
            var id = DeskId.Normalize(addr);
            var peer = discovery?.Resolve(id, 1500);
            if (peer != null)
            {
                try { return (Endpoint.Connect(peer.Address.ToString(), peer.Port), "LAN " + peer.Address); }
                catch when (!string.IsNullOrWhiteSpace(settings.RelayServer)) { }
            }
            if (!string.IsNullOrWhiteSpace(settings.RelayServer))
                return (RelayLink.Connect(settings.RelayServer, id), "relay");
            throw new IOException("Desk " + DeskId.Format(id) + " was not found on the local network. " +
                                  "To reach desks outside the LAN, set a relay server in Settings → Connection, or connect by IP address.");
        }
        var (host, port) = Endpoint.Parse(addr, 7171);
        return (Endpoint.Connect(host, port), "direct " + host);
    }
}
