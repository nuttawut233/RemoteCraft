using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace RemoteCraft.Net;

public sealed record Peer(string Id, string Name, string User, IPAddress Address, int Port, DateTime Seen);

/// <summary>
/// LAN discovery: every client announces its desk id + listening port by UDP multicast and broadcast,
/// so a desk id can be resolved to an address without any server.
/// </summary>
public sealed class Discovery : IDisposable
{
    static readonly IPAddress Group = IPAddress.Parse("239.255.71.71");
    public const int UdpPort = 7170;
    static readonly TimeSpan Expiry = TimeSpan.FromSeconds(12);

    readonly ConcurrentDictionary<string, Peer> _peers = new();
    readonly Func<(string id, string name, int port)> _self;
    UdpClient _udp;
    Thread _thread;
    System.Threading.Timer _timer;
    volatile bool _stopped;

    public event Action PeersChanged;
    public bool Announce { get; set; } = true;

    public Discovery(Func<(string id, string name, int port)> self) => _self = self;

    public IReadOnlyList<Peer> Peers => _peers.Values.Where(p => DateTime.UtcNow - p.Seen < Expiry).OrderBy(p => p.Name).ToList();

    public bool IsOnline(string id) => id != null && _peers.TryGetValue(id, out var p) && DateTime.UtcNow - p.Seen < Expiry;

    public void Start()
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
            _udp.EnableBroadcast = true;
            _udp.MulticastLoopback = true;
            foreach (var local in LocalAddresses())
                try { _udp.JoinMulticastGroup(Group, local); } catch { }
            try { _udp.JoinMulticastGroup(Group); } catch { }
        }
        catch { _udp = null; return; }

        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Discovery" };
        _thread.Start();
        _timer = new System.Threading.Timer(_ => Tick(), null, 200, 3000);
    }

    static IEnumerable<IPAddress> LocalAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) yield return ua.Address;
        }
    }

    void Tick()
    {
        if (Announce) SendAnnounce();
        bool changed = false;
        foreach (var kv in _peers)
            if (DateTime.UtcNow - kv.Value.Seen > Expiry * 2 && _peers.TryRemove(kv.Key, out _)) changed = true;
        if (changed) PeersChanged?.Invoke();
    }

    void SendAnnounce()
    {
        var (id, name, port) = _self();
        if (port <= 0) return;
        Send(new { t = "a", id, n = name, u = Environment.UserName, p = port });
    }

    void Send(object msg)
    {
        if (_udp == null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        try { _udp.Send(bytes, bytes.Length, new IPEndPoint(Group, UdpPort)); } catch { }
        try { _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort)); } catch { }
    }

    void ReceiveLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stopped)
        {
            try
            {
                var data = _udp.Receive(ref any);
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                var t = root.GetProperty("t").GetString();
                var id = root.GetProperty("id").GetString();
                var (selfId, _, _) = _self();
                if (t == "q")
                {
                    if (id == selfId && Announce) SendAnnounce();
                    continue;
                }
                if (t != "a" || id == selfId) continue;
                var peer = new Peer(id, root.GetProperty("n").GetString(), root.GetProperty("u").GetString(),
                    any.Address, root.GetProperty("p").GetInt32(), DateTime.UtcNow);
                bool isNew = !_peers.TryGetValue(id, out var old) || DateTime.UtcNow - old.Seen > Expiry
                             || old.Name != peer.Name || !old.Address.Equals(peer.Address);
                _peers[id] = peer;
                if (isNew) PeersChanged?.Invoke();
            }
            catch when (!_stopped) { Thread.Sleep(50); }
            catch { break; }
        }
    }

    /// <summary>Resolves a desk id on the LAN, asking the network if it isn't cached.</summary>
    public Peer Resolve(string id, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        if (_peers.TryGetValue(id, out var p) && DateTime.UtcNow - p.Seen < Expiry) return p;
        Send(new { t = "q", id });
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            if (_peers.TryGetValue(id, out p) && DateTime.UtcNow - p.Seen < TimeSpan.FromSeconds(2)) return p;
        }
        return null;
    }

    public void Dispose()
    {
        _stopped = true;
        _timer?.Dispose();
        try { _udp?.Close(); } catch { }
    }
}
