using System.Net.Sockets;
using System.Security.Cryptography;
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

/// <summary>Relay protocol 2: the relay greets with a nonce; clients answer with a key proof.</summary>
static class RelayAuth
{
    /// <summary>Connects, reads the greeting and returns the stream plus the proof to send ("-" without a key).</summary>
    public static (Stream stream, string proof) Open(string host, int port, string relayKey)
    {
        var s = Endpoint.Connect(host, port);
        try
        {
            s.ReadTimeout = 10000;
            string greeting;
            try { greeting = Endpoint.ReadLine(s); }
            catch (IOException) { throw new IOException("The relay did not answer. It may be an older RemoteCraft.Relay; update it to version 2."); }
            s.ReadTimeout = Timeout.Infinite;
            var parts = greeting.Split(' ');
            if (parts.Length < 3 || parts[0] != "RCRELAY") throw new IOException("Not a RemoteCraft relay.");
            var proof = string.IsNullOrEmpty(relayKey)
                ? "-"
                : Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(relayKey), Encoding.ASCII.GetBytes(parts[2])));
            return (s, proof);
        }
        catch { s.Dispose(); throw; }
    }

    public static string Explain(string reply) => reply switch
    {
        "DENIED" => "The relay refused the relay key. Check Settings → Connection → Relay key.",
        "IDTAKEN" => "Another desk already owns this id at the relay.",
        "BUSY" => "The relay is limiting requests from this address; try again in a minute.",
        "OFFLINE" => "The remote desk is offline (not registered at the relay).",
        _ => "Relay: " + reply,
    };
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
    readonly string _key;
    readonly string _owner;
    readonly Action<Stream, string> _onIncoming;
    volatile bool _stopped;
    Stream _control;

    public string Status { get; private set; } = "Connecting to relay…";
    public bool Online { get; private set; }
    public event Action StatusChanged;

    /// <param name="key">Relay access key, or null for an open relay.</param>
    /// <param name="ownerSecret">This desk's secret; the relay stores only its hash and lets no other desk take the id.</param>
    public RelayLink(string server, string id, string key, string ownerSecret, Action<Stream, string> onIncoming)
    {
        _server = server;
        _id = id;
        _key = key;
        _owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerSecret)));
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
                var (control, proof) = RelayAuth.Open(host, port, _key);
                _control = control;
                Endpoint.WriteLine(_control, $"REG {_id} {proof} {_owner}");
                var reply = Endpoint.ReadLine(_control);
                if (reply != "REGISTERED") throw new IOException(RelayAuth.Explain(reply));
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
            var (s, _) = RelayAuth.Open(host, port, null); // the one-time token is the credential
            Endpoint.WriteLine(s, "ANSWER " + token);
            _onIncoming(s, "relay");
        }
        catch { }
    }

    /// <summary>Asks the relay to pair us with the desk <paramref name="id"/>.</summary>
    public static Stream Connect(string server, string key, string id)
    {
        var (host, port) = Endpoint.Parse(server, DefaultPort);
        var (s, proof) = RelayAuth.Open(host, port, key);
        Endpoint.WriteLine(s, $"CONNECT {id} {proof}");
        var reply = Endpoint.ReadLine(s);
        if (reply == "OK") return s;
        s.Dispose();
        throw new IOException(RelayAuth.Explain(reply));
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
                return (RelayLink.Connect(settings.RelayServer, settings.GetRelayKey(), id), "relay");
            throw new IOException("Desk " + DeskId.Format(id) + " was not found on the local network. " +
                                  "To reach desks outside the LAN, set a relay server in Settings → Connection, or connect by IP address.");
        }
        var (host, port) = Endpoint.Parse(addr, 7171);
        return (Endpoint.Connect(host, port), "direct " + host);
    }
}
