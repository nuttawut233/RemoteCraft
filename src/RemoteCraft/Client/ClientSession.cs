using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using RemoteCraft.Host;
using RemoteCraft.Net;

namespace RemoteCraft.Client;

public enum ClientState { Connecting, Waiting, Connected, Closed }

/// <summary>Viewer side of a session: connects, authenticates, keeps the framebuffer, sends input.</summary>
public sealed class ClientSession
{
    readonly Discovery _discovery;
    SecureChannel _ch;
    ServerInfo _info;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    volatile bool _closing;

    public string Address { get; }
    public string Mode { get; }
    public ClientState State { get; private set; } = ClientState.Connecting;
    public string StatusText { get; private set; } = "Connecting…";
    public string Via { get; private set; }
    public ServerInfo Info => _info;
    public AuthResult Auth { get; private set; }
    public Permissions Perms { get; private set; } = new();
    public string RemoteId => _info?.Id ?? Address;
    public bool PasswordAvailable => _info?.Salt != null;
    public int LatencyMs { get; private set; } = -1;
    public bool SyncClipboard { get; set; } = true;
    public string Fingerprint => _ch?.Fingerprint;

    /// <summary>Remote screen; lock <see cref="FrameLock"/> while reading or drawing it.</summary>
    public Bitmap Frame { get; private set; }
    public readonly object FrameLock = new();

    // Events are raised on the UI thread.
    public event Action StateChanged;
    public event Action<Rectangle[]> FrameUpdated;
    public event Action<string> ChatReceived;
    public event Action<FileListing> FileListReceived;
    public event Action<int, byte[], bool> FileChunkReceived;
    public event Action<int, string, bool> FileStatusReceived; // id, error message, done

    public ClientSession(string address, string mode, Discovery discovery)
    {
        Address = address.Trim();
        Mode = mode;
        _discovery = discovery;
    }

    public void Start() => new Thread(Run) { IsBackground = true, Name = "ClientSession" }.Start();

    void SetState(ClientState s, string text)
    {
        State = s;
        StatusText = text;
        Ui.Post(() => StateChanged?.Invoke());
    }

    void Run()
    {
        try
        {
            if (DeskId.IsId(Address) && DeskId.Normalize(Address) == Settings.Current.DeskId)
                throw new IOException("This is your own desk. Enter the address of another desk.");

            var (stream, via) = Connector.Open(Address, _discovery, Settings.Current);
            if (State == ClientState.Closed) { stream.Dispose(); return; } // cancelled while dialling
            Via = via;
            SetState(ClientState.Connecting, "Establishing an encrypted connection…");
            _ch = SecureChannel.Establish(stream, false);
            var m = _ch.Receive();
            if (m.Type != MsgType.ServerInfo) throw new IOException("Unexpected reply.");
            _info = m.Json<ServerInfo>();

            string proof = null;
            if (_info.Salt != null)
            {
                var key = Settings.Current.GetSavedKey(_info.Id);
                if (key != null) proof = Convert.ToHexString(RemoteCraft.Auth.Proof(key, _ch.Transcript));
            }
            _ch.SendJson(MsgType.Hello, new HelloMsg
            {
                Id = Settings.Current.DeskId,
                Name = Settings.Current.DisplayName,
                User = Environment.UserName,
                Mode = Mode,
                Proof = proof,
            });
            SetState(ClientState.Waiting, "Waiting for the remote side to accept…");
            new Thread(PingLoop) { IsBackground = true }.Start();
            while (true) Handle(_ch.Receive());
        }
        catch (Exception ex)
        {
            if (State == ClientState.Closed) return;
            var msg = _closing ? "Session ended." : ex is IOException or System.Net.Sockets.SocketException ? ex.Message : "Connection lost: " + ex.Message;
            if (State == ClientState.Connected && !_closing) msg = "The connection to the remote desk was lost.";
            Finish(msg);
        }
    }

    void Finish(string message)
    {
        if (State == ClientState.Closed) return;
        ClipboardHub.TextChanged -= OnLocalClipboard;
        _ch?.Dispose();
        SetState(ClientState.Closed, message);
    }

    void PingLoop()
    {
        var buf = new byte[8];
        while (State != ClientState.Closed)
        {
            Thread.Sleep(2000);
            BinaryPrimitives.WriteInt64LittleEndian(buf, _clock.ElapsedMilliseconds);
            if (!(_ch?.TrySend(MsgType.Ping, buf) ?? false)) break;
        }
    }

    void Handle(NetMessage m)
    {
        switch (m.Type)
        {
            case MsgType.Waiting:
                SetState(ClientState.Waiting, m.Payload.Length > 0 && m.Payload[0] == 0
                    ? "This desk only accepts unattended access. Log in with the password."
                    : "Waiting for the remote side to accept…");
                break;
            case MsgType.AuthResult:
                var r = m.Json<AuthResult>();
                if (r.Ok)
                {
                    Auth = r;
                    Perms = r.Permissions ?? new Permissions();
                    ClipboardHub.TextChanged += OnLocalClipboard;
                    SetState(ClientState.Connected, "Connected");
                    SendQuality(Settings.Current.Quality);
                }
                else if (r.Retry) SetState(ClientState.Waiting, r.Reason);
                else throw new IOException(r.Reason ?? "The remote side rejected the session.");
                break;
            case MsgType.Permissions:
                Perms = m.Json<Permissions>();
                Ui.Post(() => StateChanged?.Invoke());
                break;
            case MsgType.Frame:
                ApplyFrame(m.Payload);
                break;
            case MsgType.Pong:
                LatencyMs = (int)(_clock.ElapsedMilliseconds - BinaryPrimitives.ReadInt64LittleEndian(m.Payload));
                break;
            case MsgType.Clipboard:
                if (SyncClipboard) ClipboardHub.SetText(m.Text);
                break;
            case MsgType.Chat:
                var text = m.Text;
                Ui.Post(() => ChatReceived?.Invoke(text));
                break;
            case MsgType.FileListResult:
                var listing = m.Json<FileListing>();
                Ui.Post(() => FileListReceived?.Invoke(listing));
                break;
            case MsgType.FileChunk:
                int id = BinaryPrimitives.ReadInt32LittleEndian(m.Payload);
                bool final = m.Payload[4] != 0;
                var data = m.Payload[5..];
                FileChunkReceived?.Invoke(id, data, final); // on the network thread, so writes stay in order
                break;
            case MsgType.FileDone:
                var done = m.Json<FileStatus>();
                Ui.Post(() => FileStatusReceived?.Invoke(done.Id, null, true));
                break;
            case MsgType.FileError:
                var err = m.Json<FileStatus>();
                Ui.Post(() => FileStatusReceived?.Invoke(err.Id, err.Message, true));
                break;
            case MsgType.Disconnect:
                _closing = true;
                throw new IOException("The remote side ended the session.");
        }
    }

    void ApplyFrame(byte[] p)
    {
        int w = BinaryPrimitives.ReadInt32LittleEndian(p);
        int h = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(4));
        int count = BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(8));
        int off = 10;
        var rects = new Rectangle[count];
        lock (FrameLock)
        {
            if (Frame == null || Frame.Width != w || Frame.Height != h)
            {
                Frame?.Dispose();
                Frame = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            }
            using var g = Graphics.FromImage(Frame);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            for (int i = 0; i < count; i++)
            {
                short x = BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(off));
                short y = BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(off + 2));
                short rw = BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(off + 4));
                short rh = BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(off + 6));
                int len = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(off + 8));
                off += 12;
                using (var ms = new MemoryStream(p, off, len))
                using (var img = Image.FromStream(ms))
                    g.DrawImage(img, new Rectangle(x, y, rw, rh), new Rectangle(0, 0, rw, rh), GraphicsUnit.Pixel);
                off += len;
                rects[i] = new Rectangle(x, y, rw, rh);
            }
        }
        _ch.TrySend(MsgType.FrameAck, ReadOnlySpan<byte>.Empty);
        Ui.Post(() => FrameUpdated?.Invoke(rects));
    }

    void OnLocalClipboard(string text)
    {
        if (SyncClipboard && State == ClientState.Connected && Perms.Clipboard)
            Task.Run(() => _ch.TrySend(MsgType.Clipboard, Encoding.UTF8.GetBytes(text)));
    }

    // ---- commands from the UI ----

    public void LoginWithPassword(string password, bool remember)
    {
        if (_info?.Salt == null || _ch == null) return;
        var key = RemoteCraft.Auth.DeriveKey(password, Convert.FromBase64String(_info.Salt));
        if (remember) Settings.Current.SaveKey(_info.Id, key);
        var proof = Convert.ToHexString(RemoteCraft.Auth.Proof(key, _ch.Transcript));
        Task.Run(() => _ch.TrySend(MsgType.AuthPassword, Encoding.UTF8.GetBytes(proof)));
        SetState(ClientState.Waiting, "Checking password…");
    }

    public void SendMouse(MouseKind kind, MouseBtn btn, int x, int y, int delta = 0)
    {
        if (State != ClientState.Connected || !Perms.Input) return;
        var buf = new byte[14];
        buf[0] = (byte)kind;
        buf[1] = (byte)btn;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(2), x);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(6), y);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(10), delta);
        _ch.TrySend(MsgType.Mouse, buf);
    }

    public void SendKey(ushort vk, ushort scan, bool extended, bool up)
    {
        if (State != ClientState.Connected || !Perms.Input) return;
        var buf = new byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, vk);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), scan);
        buf[4] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
        _ch.TrySend(MsgType.Key, buf);
    }

    public void SendAction(RemoteAction a) => _ch?.TrySend(MsgType.Action, new[] { (byte)a });
    public void SendChat(string text) => _ch?.TrySend(MsgType.Chat, Encoding.UTF8.GetBytes(text));

    public void SendQuality(string quality)
    {
        var (q, fps) = quality switch
        {
            "Best" => (85, 30),
            "Speed" => (45, 15),
            _ => (70, 24),
        };
        _ch?.TrySend(MsgType.SetQuality, new[] { (byte)q, (byte)fps });
    }

    public void SelectMonitor(int index)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, index);
        _ch?.TrySend(MsgType.SelectMonitor, buf);
        if (Auth != null) Auth.Monitor = index;
    }

    public void RequestFileList(string path) => Task.Run(() => _ch?.TrySend(MsgType.FileList, Encoding.UTF8.GetBytes(path ?? "")));

    public void RequestDownload(int id, string remotePath) =>
        _ch?.TrySend(MsgType.FileDownload, JsonSerializer.SerializeToUtf8Bytes(new FileUpload { Id = id, Path = remotePath }));

    /// <summary>Uploads a local file; blocks the calling (worker) thread. Progress reports bytes sent.</summary>
    public void Upload(int id, string localPath, string remotePath, Action<long> progress, CancellationToken ct)
    {
        using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _ch.Send(MsgType.FileUploadBegin, JsonSerializer.SerializeToUtf8Bytes(new FileUpload { Id = id, Path = remotePath, Size = fs.Length }));
        var buf = new byte[5 + 256 * 1024];
        BinaryPrimitives.WriteInt32LittleEndian(buf, id);
        long sent = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n = fs.Read(buf, 5, buf.Length - 5);
            bool final = n == 0 || fs.Position >= fs.Length;
            buf[4] = (byte)(final ? 1 : 0);
            _ch.Send(MsgType.FileUploadChunk, buf.AsSpan(0, 5 + n));
            sent += n;
            progress(sent);
            if (final) break;
        }
    }

    public void Disconnect()
    {
        _closing = true;
        _ch?.TrySend(MsgType.Disconnect, ReadOnlySpan<byte>.Empty);
        Finish("Session ended.");
    }
}
