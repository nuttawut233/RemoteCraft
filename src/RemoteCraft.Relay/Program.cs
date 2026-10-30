using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// RemoteCraft relay: pairs a viewer with a registered desk and forwards bytes both ways.
// It never sees plaintext; sessions are end-to-end encrypted by the clients.
//
//   relay  → any:   "RCRELAY 2 <nonce>\n"                      (sent first on every connection)
//   desk   → relay: "REG <id> <proof> <owner>\n"   ← "REGISTERED\n", later "CALL <token>\n" per incoming viewer
//   desk   → relay: "ANSWER <token>\n" (new socket)            → piped to the waiting viewer
//   viewer → relay: "CONNECT <id> <proof>\n"       ← "OK\n" (then piped) | "OFFLINE\n"
//   errors:         "DENIED\n" (wrong relay key)  "IDTAKEN\n" (id owned by another desk)  "BUSY\n" (limits)
//
//   proof = hex(HMAC-SHA256(relay key, nonce)), or "-" when the relay has no key.
//   owner = hex(SHA-256(desk secret)); the first desk to register an id owns it (saved in relay-desks.json).
//
// Usage: RemoteCraft.Relay [--port 7180] [--key SECRET] [--data DIR]
//   Environment: RCRELAY_PORT, RCRELAY_KEY, RCRELAY_DATA. A bare number as the first argument is the port.

var opts = ParseArgs(args);
int port = int.TryParse(opts.GetValueOrDefault("port") ?? Environment.GetEnvironmentVariable("RCRELAY_PORT"), out var p) ? p : 7180;
var relayKey = opts.GetValueOrDefault("key") ?? Environment.GetEnvironmentVariable("RCRELAY_KEY");
if (string.IsNullOrEmpty(relayKey)) relayKey = null;
var dataDir = opts.GetValueOrDefault("data") ?? Environment.GetEnvironmentVariable("RCRELAY_DATA") ?? AppContext.BaseDirectory;
Directory.CreateDirectory(dataDir);
var ownersFile = Path.Combine(dataDir, "relay-desks.json");

const int MaxConnectionsPerIp = 40;
const int MaxCallsPerMinutePerIp = 20;
const int MaxAuthFailuresPerMinutePerIp = 10;

var desks = new ConcurrentDictionary<string, Desk>();
var pending = new ConcurrentDictionary<string, TaskCompletionSource<NetworkStream>>();
var owners = LoadOwners();
var perIp = new ConcurrentDictionary<string, int>();
var calls = new ConcurrentDictionary<string, Queue<DateTime>>();
var authFailures = new ConcurrentDictionary<string, Queue<DateTime>>();

TcpListener listener;
try
{
    listener = new TcpListener(IPAddress.IPv6Any, port);
    listener.Server.DualMode = true; // IPv4 and IPv6
    listener.Start();
}
catch (SocketException)
{
    listener = new TcpListener(IPAddress.Any, port); // hosts with IPv6 disabled
    listener.Start();
}
Log($"RemoteCraft relay 2 listening on port {port}" + (relayKey != null ? " (relay key required)" : " (open: no relay key set — use --key on a public server)"));
Log($"desk ownership file: {ownersFile}");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    client.NoDelay = true;
    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    _ = Task.Run(() => Handle(client));
}

async Task Handle(TcpClient client)
{
    var ep = client.Client.RemoteEndPoint as IPEndPoint;
    var ip = ep?.Address.IsIPv4MappedToIPv6 == true ? ep.Address.MapToIPv4().ToString() : ep?.Address.ToString() ?? "?";
    var remote = ep?.ToString();
    if (perIp.AddOrUpdate(ip, 1, (_, n) => n + 1) > MaxConnectionsPerIp)
    {
        perIp.AddOrUpdate(ip, 0, (_, n) => n - 1);
        client.Dispose();
        return;
    }
    var stream = client.GetStream();
    bool handedOff = false;
    try
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await Write(stream, "RCRELAY 2 " + nonce);
        var line = await ReadLine(stream, TimeSpan.FromSeconds(15));
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string Arg(int i) => parts.Length > i ? parts[i] : "";
        switch (Arg(0))
        {
            case "REG":
                if (!await CheckKey(stream, nonce, Arg(2), ip)) break;
                await Register(Arg(1), Arg(3), client, stream, remote);
                return;
            case "CONNECT":
                if (!await CheckKey(stream, nonce, Arg(2), ip)) break;
                if (!RateOk(calls, ip, MaxCallsPerMinutePerIp)) { await Write(stream, "BUSY"); break; }
                await Call(Arg(1), client, stream, remote);
                return;
            case "ANSWER":
                if (pending.TryRemove(Arg(1), out var tcs))
                {
                    handedOff = true;
                    tcs.TrySetResult(stream); // piping is done by the caller task
                    return;
                }
                break;
        }
    }
    catch (Exception ex) { Log($"{remote}: {ex.Message}"); }
    finally
    {
        perIp.AddOrUpdate(ip, 0, (_, n) => n - 1);
        if (!handedOff) client.Dispose();
    }
}

async Task<bool> CheckKey(NetworkStream stream, string nonce, string proof, string ip)
{
    if (relayKey == null) return true;
    if (!RateOk(authFailures, ip, MaxAuthFailuresPerMinutePerIp, count: false)) { await Write(stream, "BUSY"); return false; }
    var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(relayKey), Encoding.ASCII.GetBytes(nonce));
    byte[] given;
    try { given = Convert.FromHexString(proof); } catch { given = Array.Empty<byte>(); }
    if (given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected)) return true;
    RateOk(authFailures, ip, MaxAuthFailuresPerMinutePerIp); // record the failure
    Log($"{ip}: wrong relay key");
    await Write(stream, "DENIED");
    return false;
}

/// <summary>Sliding one-minute window; with count=false it only checks.</summary>
static bool RateOk(ConcurrentDictionary<string, Queue<DateTime>> map, string ip, int limit, bool count = true)
{
    var q = map.GetOrAdd(ip, _ => new Queue<DateTime>());
    lock (q)
    {
        while (q.Count > 0 && DateTime.UtcNow - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();
        if (q.Count >= limit) return false;
        if (count) q.Enqueue(DateTime.UtcNow);
        return true;
    }
}

async Task Register(string id, string owner, TcpClient client, NetworkStream stream, string remote)
{
    if (id.Length is < 6 or > 12 || !id.All(char.IsDigit)) { await Write(stream, "BADID"); return; }
    if (owner.Length != 64 || !owner.All(Uri.IsHexDigit)) { await Write(stream, "BADOWNER"); return; }
    owner = owner.ToUpperInvariant();
    lock (owners)
    {
        if (owners.TryGetValue(id, out var known) && known != owner) owner = null;
        else if (known == null) { owners[id] = owner; SaveOwners(); }
    }
    if (owner == null)
    {
        Log($"desk {id}: refused registration from {remote} (id owned by another desk)");
        await Write(stream, "IDTAKEN");
        return;
    }
    var desk = new Desk(client, stream);
    if (desks.TryGetValue(id, out var old)) old.Client.Dispose();
    desks[id] = desk;
    await Write(stream, "REGISTERED");
    Log($"desk {id} registered from {remote}  ({desks.Count} online)");
    try
    {
        // Keep reading PINGs until the desk goes away.
        while (true) await ReadLine(stream, TimeSpan.FromSeconds(90));
    }
    catch { }
    finally
    {
        desks.TryRemove(new KeyValuePair<string, Desk>(id, desk));
        client.Dispose();
        Log($"desk {id} went offline  ({desks.Count} online)");
    }
}

async Task Call(string id, TcpClient client, NetworkStream stream, string remote)
{
    if (!desks.TryGetValue(id, out var desk))
    {
        await Write(stream, "OFFLINE");
        return;
    }
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    var tcs = new TaskCompletionSource<NetworkStream>(TaskCreationOptions.RunContinuationsAsynchronously);
    pending[token] = tcs;
    try
    {
        await desk.Send("CALL " + token);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(15000));
        if (done != tcs.Task) { await Write(stream, "OFFLINE"); return; }
        var deskStream = tcs.Task.Result;
        await Write(stream, "OK");
        Log($"session {remote} → desk {id}");
        await Task.WhenAny(stream.CopyToAsync(deskStream), deskStream.CopyToAsync(stream));
        deskStream.Dispose();
        Log($"session {remote} → desk {id} ended");
    }
    finally
    {
        pending.TryRemove(token, out _);
    }
}

Dictionary<string, string> LoadOwners()
{
    try
    {
        if (File.Exists(ownersFile))
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ownersFile)) ?? new();
    }
    catch (Exception ex) { Log("could not read " + ownersFile + ": " + ex.Message); }
    return new();
}

void SaveOwners()
{
    try
    {
        var tmp = ownersFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(owners));
        File.Move(tmp, ownersFile, true);
    }
    catch (Exception ex) { Log("could not write " + ownersFile + ": " + ex.Message); }
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--") && i + 1 < args.Length) d[args[i][2..]] = args[++i];
        else if (i == 0 && int.TryParse(args[i], out _)) d["port"] = args[i];
    }
    return d;
}

static async Task<string> ReadLine(NetworkStream s, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    var sb = new StringBuilder();
    var one = new byte[1];
    while (sb.Length < 256)
    {
        if (await s.ReadAsync(one, cts.Token) != 1) throw new IOException("closed");
        if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
        sb.Append((char)one[0]);
    }
    throw new IOException("line too long");
}

static Task Write(NetworkStream s, string line) => s.WriteAsync(Encoding.ASCII.GetBytes(line + "\n")).AsTask();

static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

sealed class Desk(TcpClient client, NetworkStream stream)
{
    readonly SemaphoreSlim _lock = new(1, 1);
    public TcpClient Client => client;

    public async Task Send(string line)
    {
        await _lock.WaitAsync();
        try { await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n")); }
        finally { _lock.Release(); }
    }
}
