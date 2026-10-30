using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteCraft.Net;

namespace RemoteCraft.Host;

/// <summary>Accepts incoming sessions (direct TCP or relay) and authenticates them.</summary>
public sealed class HostServer : IDisposable
{
    TcpListener _listener;
    volatile bool _stopped;
    readonly List<DateTime> _failures = new();

    public int Port { get; private set; }
    public string ListenError { get; private set; }
    public readonly List<HostSession> Sessions = new();

    /// <summary>Raised on the UI thread for every session that passed the handshake.</summary>
    public event Action<HostSession> IncomingSession;

    public void Start(int preferredPort)
    {
        for (int p = preferredPort; p < preferredPort + 20; p++)
        {
            try
            {
                var l = new TcpListener(System.Net.IPAddress.Any, p);
                l.Start();
                _listener = l;
                Port = p;
                break;
            }
            catch (SocketException ex) { ListenError = ex.Message; }
        }
        if (_listener == null) return;
        ListenError = null;
        new Thread(AcceptLoop) { IsBackground = true, Name = "HostAccept" }.Start();
    }

    void AcceptLoop()
    {
        while (!_stopped)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                client.NoDelay = true;
                var via = "direct " + (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address;
                Task.Run(() => HandleIncoming(client.GetStream(), via));
            }
            catch when (!_stopped) { Thread.Sleep(100); }
            catch { break; }
        }
    }

    public void HandleIncoming(Stream stream, string via)
    {
        SecureChannel ch = null;
        try
        {
            if (stream.CanTimeout) stream.ReadTimeout = 20000;
            ch = SecureChannel.Establish(stream, true);
            var st = Settings.Current;
            ch.SendJson(MsgType.ServerInfo, new ServerInfo
            {
                Id = st.DeskId,
                Name = st.DisplayName,
                User = Environment.UserName,
                Os = Environment.OSVersion.VersionString,
                Salt = st.HasPassword ? st.PasswordSalt : null,
                Version = Program.Version,
            });
            var m = ch.Receive();
            if (m.Type != MsgType.Hello) throw new IOException("Expected hello.");
            var hello = m.Json<HelloMsg>();
            if (stream.CanTimeout) stream.ReadTimeout = Timeout.Infinite;

            var session = new HostSession(this, ch, hello, via);
            lock (Sessions) Sessions.Add(session);

            if (hello.Proof != null && VerifyProof(ch, hello.Proof))
                session.Accept(st.UnattendedPerms);
            else if (!st.AllowRequests && !st.HasPassword)
            {
                session.Reject("This desk does not accept incoming sessions.");
                return;
            }
            else
            {
                if (hello.Proof != null)
                    ch.SendJson(MsgType.AuthResult, new AuthResult { Ok = false, Retry = true, Reason = "The saved password was not accepted." });
                ch.Send(MsgType.Waiting, new[] { (byte)(st.AllowRequests ? 1 : 0) });
            }
            new Thread(session.Run) { IsBackground = true, Name = "HostSession" }.Start();
            Ui.Post(() => IncomingSession?.Invoke(session));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Incoming failed: " + ex.Message);
            if (ch != null) ch.Dispose(); else stream.Dispose();
        }
    }

    /// <summary>Checks a password proof; after 5 failures in 5 minutes all proofs are refused for a minute.</summary>
    public bool VerifyProof(SecureChannel ch, string proofHex)
    {
        var st = Settings.Current;
        lock (_failures)
        {
            _failures.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(5));
            if (_failures.Count >= 5 && DateTime.UtcNow - _failures[^1] < TimeSpan.FromMinutes(1)) return false;
        }
        if (!st.HasPassword || string.IsNullOrEmpty(proofHex)) return false;
        bool ok;
        try
        {
            var expected = Auth.Proof(Convert.FromBase64String(st.PasswordKey), ch.Transcript);
            ok = CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(proofHex));
        }
        catch { ok = false; }
        if (!ok) lock (_failures) _failures.Add(DateTime.UtcNow);
        return ok;
    }

    internal void Remove(HostSession s)
    {
        lock (Sessions) Sessions.Remove(s);
    }

    public void Dispose()
    {
        _stopped = true;
        try { _listener?.Stop(); } catch { }
        HostSession[] all;
        lock (Sessions) all = Sessions.ToArray();
        foreach (var s in all) s.Close("The remote desk closed RemoteCraft.");
    }
}

public sealed class HostSession
{
    readonly HostServer _server;
    readonly AutoResetEvent _ack = new(false);
    readonly Dictionary<int, FileStream> _uploads = new();
    volatile bool _fullRefresh = true;
    volatile int _monitor;
    volatile int _quality = 70, _fps = 20;
    volatile int _boundsX, _boundsY, _boundsW = 1, _boundsH = 1;

    public SecureChannel Channel { get; }
    public HelloMsg Client { get; }
    public string Via { get; }
    public Permissions Perms { get; private set; } = new();
    public bool Accepted { get; private set; }
    public bool Closed { get; private set; }
    public DateTime Started { get; private set; }
    public string CloseReason { get; private set; }

    /// <summary>UI-thread events.</summary>
    public event Action StateChanged;
    public event Action<string> ChatReceived;

    public HostSession(HostServer server, SecureChannel ch, HelloMsg hello, string via)
    {
        _server = server;
        Channel = ch;
        Client = hello;
        Via = via;
        ClipboardHub.TextChanged += OnLocalClipboard;
    }

    public bool IsFileOnly => Client.Mode == "files";

    public void Accept(Permissions perms)
    {
        if (Accepted || Closed) return;
        Perms = perms.Clone();
        Accepted = true;
        Started = DateTime.Now;
        var screens = Monitors.GetAll();
        _monitor = Math.Max(0, screens.FindIndex(s => s.Primary));
        Channel.TrySend(MsgType.AuthResult, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AuthResult
        {
            Ok = true,
            Permissions = Perms,
            Monitor = _monitor,
            Monitors = screens.Select(s => new MonitorInfo
            {
                Name = s.Name,
                X = s.Bounds.X, Y = s.Bounds.Y, W = s.Bounds.Width, H = s.Bounds.Height, Primary = s.Primary,
            }).ToList(),
        }));
        if (!IsFileOnly) new Thread(StreamLoop) { IsBackground = true, Name = "HostStream" }.Start();
        Ui.Post(() => StateChanged?.Invoke());
    }

    public void Reject(string reason)
    {
        Channel.TrySend(MsgType.AuthResult, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AuthResult { Ok = false, Reason = reason }));
        Close(reason);
    }

    public void SetPermissions(Permissions p)
    {
        Perms = p.Clone();
        if (Accepted) Channel.TrySend(MsgType.Permissions, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Perms));
    }

    public void SendChat(string text) => Channel.TrySend(MsgType.Chat, System.Text.Encoding.UTF8.GetBytes(text));

    void OnLocalClipboard(string text)
    {
        if (Accepted && !Closed && Perms.Clipboard)
            Task.Run(() => Channel.TrySend(MsgType.Clipboard, System.Text.Encoding.UTF8.GetBytes(text)));
    }

    public void Run()
    {
        Native.SetThreadDpiAwarenessContext(Native.DPI_PER_MONITOR_AWARE_V2); // input in physical pixels
        try
        {
            while (!Closed) Handle(Channel.Receive());
        }
        catch (Exception ex) when (ex is not DisconnectException) { Close("Connection lost."); }
        catch (DisconnectException) { Close("The remote side ended the session."); }
    }

    sealed class DisconnectException : Exception { }

    void Handle(NetMessage m)
    {
        if (m.Type == MsgType.Disconnect) throw new DisconnectException();
        if (m.Type == MsgType.Ping) { Channel.TrySend(MsgType.Pong, m.Payload); return; }
        if (!Accepted)
        {
            if (m.Type == MsgType.AuthPassword)
            {
                if (_server.VerifyProof(Channel, m.Text)) Accept(Settings.Current.UnattendedPerms);
                else Channel.SendJson(MsgType.AuthResult, new AuthResult { Ok = false, Retry = true, Reason = "Wrong password." });
            }
            return;
        }
        var p = m.Payload;
        switch (m.Type)
        {
            case MsgType.FrameAck: _ack.Set(); break;
            case MsgType.RequestRefresh: _fullRefresh = true; break;
            case MsgType.SetQuality:
                _quality = Math.Clamp((int)p[0], 10, 95);
                _fps = Math.Clamp((int)p[1], 1, 60);
                break;
            case MsgType.SelectMonitor:
                _monitor = BinaryPrimitives.ReadInt32LittleEndian(p);
                _fullRefresh = true;
                break;
            case MsgType.Mouse:
                if (!Perms.Input) break;
                var x = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(2));
                var y = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(6));
                var delta = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(10));
                x = Math.Clamp(x, 0, _boundsW - 1);
                y = Math.Clamp(y, 0, _boundsH - 1);
                InputInjector.Mouse((MouseKind)p[0], (MouseBtn)p[1], _boundsX + x, _boundsY + y, delta);
                break;
            case MsgType.Key:
                if (!Perms.Input) break;
                InputInjector.Key(BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)),
                    (p[4] & 2) != 0, (p[4] & 1) != 0);
                break;
            case MsgType.Action:
                var action = (RemoteAction)p[0];
                if (action == RemoteAction.RefreshScreen) _fullRefresh = true;
                else if (action == RemoteAction.Lock ? Perms.Lock : Perms.Input) InputInjector.Run(action);
                break;
            case MsgType.Clipboard:
                if (Perms.Clipboard) ClipboardHub.SetText(m.Text);
                break;
            case MsgType.Chat:
                var text = m.Text;
                Ui.Post(() => ChatReceived?.Invoke(text));
                break;
            case MsgType.FileList:
            case MsgType.FileDownload:
            case MsgType.FileUploadBegin:
            case MsgType.FileUploadChunk:
                if (!Perms.Files)
                {
                    if (m.Type == MsgType.FileList)
                        Channel.SendJson(MsgType.FileListResult, new FileListing { Path = m.Text, Error = "File transfer is not permitted by the remote side." });
                    else Channel.SendJson(MsgType.FileError, new FileStatus { Id = -1, Message = "File transfer is not permitted by the remote side." });
                    break;
                }
                HandleFile(m);
                break;
        }
    }

    // ---- screen streaming ----

    void StreamLoop()
    {
        Native.SetThreadDpiAwarenessContext(Native.DPI_PER_MONITOR_AWARE_V2); // capture in physical pixels
        using var cap = new ScreenCapturer();
        var sw = Stopwatch.StartNew();
        while (!Closed)
        {
            var start = sw.ElapsedMilliseconds;
            try
            {
                var screens = Monitors.GetAll();
                int idx = Math.Clamp(_monitor, 0, screens.Count - 1);
                var b = screens[idx].Bounds;
                _boundsX = b.X; _boundsY = b.Y; _boundsW = b.Width; _boundsH = b.Height;

                bool full = _fullRefresh;
                _fullRefresh = false;
                var rects = cap.Capture(b, full);
                if (rects.Count > 0)
                {
                    var payload = BuildFrame(cap, rects, _quality);
                    _ack.Reset();
                    Channel.Send(MsgType.Frame, payload);
                    _ack.WaitOne(3000); // flow control: next frame only after the viewer drew this one
                }
            }
            catch when (!Closed)
            {
                // Secure desktop (UAC / lock screen) can't be captured by a user process; retry.
                _fullRefresh = true;
                Thread.Sleep(500);
            }
            catch { break; }

            var wait = 1000 / Math.Max(1, _fps) - (int)(sw.ElapsedMilliseconds - start);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    static byte[] BuildFrame(ScreenCapturer cap, List<Rectangle> rects, int quality)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(cap.Bounds.Width);
        w.Write(cap.Bounds.Height);
        w.Write((short)rects.Count);
        foreach (var r in rects)
        {
            var jpg = Jpeg.Encode(cap.Image, r, quality);
            w.Write((short)r.X); w.Write((short)r.Y); w.Write((short)r.Width); w.Write((short)r.Height);
            w.Write(jpg.Length);
            w.Write(jpg);
        }
        w.Flush();
        return ms.ToArray();
    }

    // ---- file transfer ----

    void HandleFile(NetMessage m)
    {
        switch (m.Type)
        {
            case MsgType.FileList:
                var path = m.Text;
                Task.Run(() => Channel.TrySend(MsgType.FileListResult, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(FileLister.List(path))));
                break;
            case MsgType.FileDownload:
                var req = m.Json<FileUpload>();
                Task.Run(() => SendFile(req));
                break;
            case MsgType.FileUploadBegin:
                var up = m.Json<FileUpload>();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(up.Path)!);
                    lock (_uploads) _uploads[up.Id] = new FileStream(up.Path, FileMode.Create, FileAccess.Write);
                }
                catch (Exception ex) { Channel.SendJson(MsgType.FileError, new FileStatus { Id = up.Id, Message = ex.Message }); }
                break;
            case MsgType.FileUploadChunk:
                int id = BinaryPrimitives.ReadInt32LittleEndian(m.Payload);
                bool final = m.Payload[4] != 0;
                FileStream fs;
                lock (_uploads) _uploads.TryGetValue(id, out fs);
                if (fs == null) break;
                try
                {
                    fs.Write(m.Payload, 5, m.Payload.Length - 5);
                    if (final)
                    {
                        fs.Dispose();
                        lock (_uploads) _uploads.Remove(id);
                        Channel.SendJson(MsgType.FileDone, new FileStatus { Id = id });
                    }
                }
                catch (Exception ex)
                {
                    fs.Dispose();
                    lock (_uploads) _uploads.Remove(id);
                    Channel.SendJson(MsgType.FileError, new FileStatus { Id = id, Message = ex.Message });
                }
                break;
        }
    }

    void SendFile(FileUpload req)
    {
        try
        {
            using var fs = new FileStream(req.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[5 + 256 * 1024];
            BinaryPrimitives.WriteInt32LittleEndian(buf, req.Id);
            while (!Closed)
            {
                int n = fs.Read(buf, 5, buf.Length - 5);
                bool final = fs.Position >= fs.Length || n == 0;
                buf[4] = (byte)(final ? 1 : 0);
                Channel.Send(MsgType.FileChunk, buf.AsSpan(0, 5 + n));
                if (final) break;
            }
        }
        catch (Exception ex)
        {
            Channel.TrySend(MsgType.FileError, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new FileStatus { Id = req.Id, Message = ex.Message }));
        }
    }

    public void Close(string reason)
    {
        if (Closed) return;
        Closed = true;
        CloseReason = reason;
        ClipboardHub.TextChanged -= OnLocalClipboard;
        Channel.TrySend(MsgType.Disconnect, ReadOnlySpan<byte>.Empty);
        Channel.Dispose();
        _ack.Set();
        lock (_uploads)
        {
            foreach (var fs in _uploads.Values) fs.Dispose();
            _uploads.Clear();
        }
        _server.Remove(this);
        Ui.Post(() => StateChanged?.Invoke());
    }
}
