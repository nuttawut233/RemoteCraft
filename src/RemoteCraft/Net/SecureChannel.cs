using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteCraft.Net;

public enum MsgType : byte
{
    ServerInfo = 1, Hello = 2, AuthPassword = 3, AuthResult = 4, Waiting = 5,
    Frame = 10, FrameAck = 11, RequestRefresh = 12, SetQuality = 13, SelectMonitor = 14,
    Mouse = 20, Key = 21, Action = 22,
    Clipboard = 30, Chat = 31, Permissions = 32,
    FileList = 40, FileListResult = 41, FileDownload = 42, FileChunk = 43,
    FileUploadBegin = 44, FileUploadChunk = 45, FileDone = 46, FileError = 47,
    Ping = 50, Pong = 51,
    Disconnect = 60,
}

public readonly record struct NetMessage(MsgType Type, byte[] Payload)
{
    public T Json<T>() => JsonSerializer.Deserialize<T>(Payload);
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>
/// Encrypted, authenticated message channel over any stream (direct TCP or a relay pipe).
/// ECDH P-256 key agreement, then AES-256-GCM per direction with counter nonces.
/// The relay only ever sees ciphertext.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    const uint Magic = 0x52434631; // "RCF1"
    const int MaxFrame = 64 * 1024 * 1024;

    readonly Stream _stream;
    readonly AesGcm _tx, _rx;
    ulong _txCounter, _rxCounter;
    readonly object _sendLock = new();
    volatile bool _closed;

    /// <summary>SHA-256 over both public keys; bound into the password proof so it can't be replayed.</summary>
    public byte[] Transcript { get; }
    /// <summary>Short code both sides can compare to rule out a man in the middle.</summary>
    public string Fingerprint { get; }
    public bool IsClosed => _closed;

    SecureChannel(Stream stream, byte[] txKey, byte[] rxKey, byte[] transcript)
    {
        _stream = stream;
        _tx = new AesGcm(txKey, 16);
        _rx = new AesGcm(rxKey, 16);
        Transcript = transcript;
        Fingerprint = Convert.ToHexString(transcript, 0, 4).Insert(4, "-");
    }

    public static SecureChannel Establish(Stream stream, bool isServer)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mine = ecdh.ExportSubjectPublicKeyInfo();

        var hello = new byte[6 + mine.Length];
        BinaryPrimitives.WriteUInt32BigEndian(hello, Magic);
        BinaryPrimitives.WriteUInt16BigEndian(hello.AsSpan(4), (ushort)mine.Length);
        mine.CopyTo(hello, 6);
        stream.Write(hello);
        stream.Flush();

        var head = new byte[6];
        stream.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32BigEndian(head) != Magic)
            throw new IOException("The remote side is not a RemoteCraft client.");
        int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        if (len is < 32 or > 1024) throw new IOException("Bad handshake.");
        var theirs = new byte[len];
        stream.ReadExactly(theirs);

        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(theirs, out _);

        var serverPub = isServer ? mine : theirs;
        var clientPub = isServer ? theirs : mine;
        var transcript = SHA256.HashData(serverPub.Concat(clientPub).ToArray());

        var c2s = ecdh.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256, Encoding.ASCII.GetBytes("RemoteCraft c2s"), transcript);
        var s2c = ecdh.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256, Encoding.ASCII.GetBytes("RemoteCraft s2c"), transcript);
        return isServer ? new SecureChannel(stream, s2c, c2s, transcript) : new SecureChannel(stream, c2s, s2c, transcript);
    }

    static void Nonce(Span<byte> nonce, ulong counter)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }

    public void Send(MsgType type, ReadOnlySpan<byte> payload)
    {
        if (_closed) throw new IOException("Channel closed.");
        var plain = new byte[1 + payload.Length];
        plain[0] = (byte)type;
        payload.CopyTo(plain.AsSpan(1));

        var frame = new byte[4 + plain.Length + 16];
        BinaryPrimitives.WriteInt32BigEndian(frame, plain.Length + 16);
        Span<byte> nonce = stackalloc byte[12];
        lock (_sendLock)
        {
            Nonce(nonce, _txCounter++);
            _tx.Encrypt(nonce, plain, frame.AsSpan(4, plain.Length), frame.AsSpan(4 + plain.Length, 16));
            try
            {
                _stream.Write(frame);
                _stream.Flush();
            }
            catch { Close(); throw; }
        }
    }

    public void Send(MsgType type) => Send(type, ReadOnlySpan<byte>.Empty);
    public void SendText(MsgType type, string text) => Send(type, Encoding.UTF8.GetBytes(text ?? ""));
    public void SendJson<T>(MsgType type, T value) => Send(type, JsonSerializer.SerializeToUtf8Bytes(value));

    /// <summary>Best-effort send that never throws (for notifications on a dying channel).</summary>
    public bool TrySend(MsgType type, ReadOnlySpan<byte> payload)
    {
        try { Send(type, payload); return true; } catch { return false; }
    }

    public NetMessage Receive()
    {
        var head = new byte[4];
        _stream.ReadExactly(head);
        int len = BinaryPrimitives.ReadInt32BigEndian(head);
        if (len < 17 || len > MaxFrame) throw new IOException("Bad frame length.");
        var buf = new byte[len];
        _stream.ReadExactly(buf);
        var plain = new byte[len - 16];
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, _rxCounter++);
        _rx.Decrypt(nonce, buf.AsSpan(0, plain.Length), buf.AsSpan(plain.Length, 16), plain);
        return new NetMessage((MsgType)plain[0], plain[1..]);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _stream.Dispose(); } catch { }
    }

    public void Dispose()
    {
        Close();
        _tx.Dispose();
        _rx.Dispose();
    }
}

// ---- JSON control messages ----

public sealed class ServerInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string User { get; set; }
    public string Os { get; set; }
    public string Salt { get; set; }      // null when unattended access is off
    public string Version { get; set; }
}

public sealed class HelloMsg
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string User { get; set; }
    public string Mode { get; set; } = "control"; // control | files
    public string Proof { get; set; }
}

public sealed class AuthResult
{
    public bool Ok { get; set; }
    public string Reason { get; set; }
    /// <summary>The request is still open (e.g. a wrong password); the viewer may try again.</summary>
    public bool Retry { get; set; }
    public Permissions Permissions { get; set; }
    public List<MonitorInfo> Monitors { get; set; }
    public int Monitor { get; set; }
}

public sealed class MonitorInfo
{
    public string Name { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Primary { get; set; }
}

public sealed class FileEntry
{
    public string Name { get; set; }
    public bool Dir { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

public sealed class FileListing
{
    public string Path { get; set; }
    public string Parent { get; set; }
    public string Error { get; set; }
    public List<FileEntry> Entries { get; set; } = new();
}

public sealed class FileUpload
{
    public int Id { get; set; }
    public string Path { get; set; }
    public long Size { get; set; }
}

public sealed class FileStatus
{
    public int Id { get; set; }
    public string Message { get; set; }
}

public static class FileLister
{
    /// <summary>Lists a directory; an empty path lists the drives (like a "This PC" root).</summary>
    public static FileListing List(string path)
    {
        var result = new FileListing { Path = path ?? "" };
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                foreach (var d in DriveInfo.GetDrives())
                    result.Entries.Add(new FileEntry { Name = d.Name, Dir = true, Size = -1 });
                return result;
            }
            var di = new DirectoryInfo(path);
            result.Path = di.FullName;
            result.Parent = di.Parent?.FullName ?? "";
            foreach (var d in di.EnumerateDirectories())
            {
                if ((d.Attributes & FileAttributes.Hidden) != 0) continue;
                result.Entries.Add(new FileEntry { Name = d.Name, Dir = true, Size = -1, Modified = d.LastWriteTime });
            }
            foreach (var f in di.EnumerateFiles())
            {
                if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                result.Entries.Add(new FileEntry { Name = f.Name, Size = f.Length, Modified = f.LastWriteTime });
            }
        }
        catch (Exception ex) { result.Error = ex.Message; }
        return result;
    }
}
