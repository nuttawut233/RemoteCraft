using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

// RemoteCraft relay: pairs a viewer with a registered desk and forwards bytes both ways.
// It never sees plaintext; sessions are end-to-end encrypted by the clients.
//
//   desk   → relay: "REG <id>\n"       ← "REGISTERED\n", later "CALL <token>\n" per incoming viewer
//   desk   → relay: "ANSWER <token>\n" (new socket)  → piped to the waiting viewer
//   viewer → relay: "CONNECT <id>\n"   ← "OK\n" (then piped) | "OFFLINE\n"
//
// Usage: RemoteCraft.Relay [port]   (default 7180)

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 7180;
var desks = new ConcurrentDictionary<string, Desk>();
var pending = new ConcurrentDictionary<string, TaskCompletionSource<NetworkStream>>();

var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Log($"RemoteCraft relay listening on port {port}");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    client.NoDelay = true;
    _ = Task.Run(() => Handle(client));
}

async Task Handle(TcpClient client)
{
    var remote = client.Client.RemoteEndPoint?.ToString();
    var stream = client.GetStream();
    try
    {
        var line = await ReadLine(stream, TimeSpan.FromSeconds(15));
        var parts = line.Split(' ', 2);
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        switch (parts[0])
        {
            case "REG":
                await Register(arg, client, stream, remote);
                return;
            case "CONNECT":
                await Call(arg, client, stream, remote);
                return;
            case "ANSWER":
                if (pending.TryRemove(arg, out var tcs)) { tcs.TrySetResult(stream); return; } // piping is done by the caller task
                break;
        }
    }
    catch (Exception ex) { Log($"{remote}: {ex.Message}"); }
    client.Dispose();
}

async Task Register(string id, TcpClient client, NetworkStream stream, string remote)
{
    if (id.Length is < 6 or > 12 || !id.All(char.IsDigit)) { await Write(stream, "BADID"); client.Dispose(); return; }
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
        client.Dispose();
        return;
    }
    var token = Guid.NewGuid().ToString("N");
    var tcs = new TaskCompletionSource<NetworkStream>(TaskCreationOptions.RunContinuationsAsynchronously);
    pending[token] = tcs;
    try
    {
        await desk.Send("CALL " + token);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(15000));
        if (done != tcs.Task) { await Write(stream, "OFFLINE"); client.Dispose(); return; }
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
        client.Dispose();
    }
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
