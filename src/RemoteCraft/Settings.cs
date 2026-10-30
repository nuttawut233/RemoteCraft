using System.Security.Cryptography;
using System.Text.Json;

namespace RemoteCraft;

public sealed class Permissions
{
    public bool Input { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    public bool Files { get; set; } = true;
    public bool Lock { get; set; } = true;

    public Permissions Clone() => (Permissions)MemberwiseClone();
}

public sealed class RecentSession
{
    public string Id { get; set; }          // desk id, or host[:port] for direct addresses
    public string Alias { get; set; }
    public string Name { get; set; }        // remote display name reported at connect
    public DateTime LastUsed { get; set; }
    public bool Favorite { get; set; }

    public string Title => !string.IsNullOrEmpty(Alias) ? Alias : DeskId.Format(Id);
}

public sealed class Settings
{
    public string DeskId { get; set; }
    public string DisplayName { get; set; } = Environment.MachineName;
    public int Port { get; set; } = 7171;
    public bool DirectEnabled { get; set; } = true;
    public bool DiscoveryEnabled { get; set; } = true;
    public bool DiscoveryCardClosed { get; set; }
    public string RelayServer { get; set; } = "";
    /// <summary>Relay access key, DPAPI-protected (base64); empty for an open relay.</summary>
    public string RelayKeyProtected { get; set; }
    /// <summary>Random secret proving to the relay that this desk owns its id.</summary>
    public string RelaySecret { get; set; }
    public bool AllowRequests { get; set; } = true;
    public string PasswordSalt { get; set; }
    public string PasswordKey { get; set; }
    public Permissions UnattendedPerms { get; set; } = new();
    public Permissions RequestPerms { get; set; } = new();
    public string Quality { get; set; } = "Balanced";
    public bool FitToWindow { get; set; } = true;
    public string ViewMode { get; set; } = "Tiles";
    public bool CloseToTray { get; set; } = true;
    public bool InstallCardHidden { get; set; }
    public List<RecentSession> Recent { get; set; } = new();
    /// <summary>Remote desk id → DPAPI-protected password key (base64), for "remember password".</summary>
    public Dictionary<string, string> SavedKeys { get; set; } = new();

    public static Settings Current { get; private set; }
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool HasPassword => !string.IsNullOrEmpty(PasswordKey);

    public static void Load()
    {
        Settings s = null;
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile));
        }
        catch { }
        s ??= new Settings();
        if (string.IsNullOrEmpty(s.DeskId)) s.DeskId = RemoteCraft.DeskId.NewId();
        if (string.IsNullOrEmpty(s.RelaySecret)) s.RelaySecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Current = s;
        s.Save();
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, AppPaths.SettingsFile, true);
            }
            catch { }
        }
    }

    public void SetPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) { PasswordSalt = null; PasswordKey = null; }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            PasswordSalt = Convert.ToBase64String(salt);
            PasswordKey = Convert.ToBase64String(Auth.DeriveKey(password, salt));
        }
        Save();
    }

    public RecentSession Touch(string id, string name)
    {
        RecentSession r;
        lock (Gate)
        {
            r = Recent.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (r == null) { r = new RecentSession { Id = id }; Recent.Add(r); }
            if (!string.IsNullOrEmpty(name)) r.Name = name;
            r.LastUsed = DateTime.Now;
            Recent.Sort((a, b) => b.LastUsed.CompareTo(a.LastUsed));
        }
        Save();
        return r;
    }

    public string GetRelayKey()
    {
        if (string.IsNullOrEmpty(RelayKeyProtected)) return null;
        try
        {
            var b = Native.Unprotect(Convert.FromBase64String(RelayKeyProtected));
            return b == null ? null : System.Text.Encoding.UTF8.GetString(b);
        }
        catch { return null; }
    }

    public void SetRelayKey(string key)
    {
        var p = string.IsNullOrEmpty(key) ? null : Native.Protect(System.Text.Encoding.UTF8.GetBytes(key));
        RelayKeyProtected = p == null ? null : Convert.ToBase64String(p);
    }

    public byte[] GetSavedKey(string id)
    {
        if (id == null || !SavedKeys.TryGetValue(id, out var b64)) return null;
        try { return Native.Unprotect(Convert.FromBase64String(b64)); } catch { return null; }
    }

    public void SaveKey(string id, byte[] key)
    {
        var p = Native.Protect(key);
        if (p == null) return;
        SavedKeys[id] = Convert.ToBase64String(p);
        Save();
    }
}

public static class AppPaths
{
    public static string Profile { get; private set; } = "";
    public static string Root { get; private set; }
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string Thumbs => Path.Combine(Root, "thumbnails");

    public static void Init(string profile)
    {
        Profile = profile ?? "";
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteCraft");
        Root = string.IsNullOrEmpty(Profile) ? baseDir : Path.Combine(baseDir, "profiles", Profile);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Thumbs);
    }

    public static string ThumbFor(string id)
    {
        var safe = string.Concat(id.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return Path.Combine(Thumbs, safe + ".jpg");
    }
}

public static class DeskId
{
    public static string NewId() => RandomNumberGenerator.GetInt32(100_000_000, 999_999_999).ToString();

    /// <summary>"123456789" → "123 456 789"; non-ids are returned unchanged.</summary>
    public static string Format(string id)
    {
        if (!IsId(id)) return id;
        var digits = Normalize(id);
        var parts = new List<string>();
        for (int i = digits.Length; i > 0; i -= 3)
            parts.Insert(0, digits.Substring(Math.Max(0, i - 3), Math.Min(3, i)));
        return string.Join(" ", parts);
    }

    public static string Normalize(string s) => new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

    public static bool IsId(string s)
    {
        var n = Normalize(s);
        return n.Length >= 6 && n.Length <= 12 && n.All(char.IsDigit);
    }
}

public static class Auth
{
    public static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);

    public static byte[] Proof(byte[] key, byte[] transcript) => HMACSHA256.HashData(key, transcript);
}
