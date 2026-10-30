using Microsoft.Win32;

namespace RemoteCraft;

/// <summary>Per-user install: copies the app to %LOCALAPPDATA%\Programs\RemoteCraft and adds shortcuts.</summary>
public static class Installer
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RemoteCraft");
    static string InstalledExe => Path.Combine(InstallDir, "RemoteCraft.exe");
    static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RemoteCraft.exe");

    /// <summary>Installed per user (setup or in-app install), or running from an all-users install in Program Files.</summary>
    public static bool IsInstalled => File.Exists(InstalledExe) ||
        AppContext.BaseDirectory.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);

    /// <summary>Running as the portable single-file exe (the assembly has no path on disk).</summary>
#pragma warning disable IL3000 // an empty Location is exactly what this checks for
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
#pragma warning restore IL3000

    public static bool RunningFromInstall =>
        string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies the program files, creates Start menu and desktop shortcuts, and enables autostart.</summary>
    public static string Install()
    {
        var src = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
        if (!RunningFromInstall)
        {
            Directory.CreateDirectory(InstallDir);
            // Portable single-file exe: copy just the exe, never the rest of its folder (e.g. Downloads).
            if (IsSingleFile) File.Copy(CurrentExe, InstalledExe, true);
            else
                foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(src, file);
                    if (rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                    var dest = Path.Combine(InstallDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, true);
                }
        }
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Shortcut(Path.Combine(programs, "RemoteCraft.lnk"), InstalledExe);
        Shortcut(Path.Combine(desktop, "RemoteCraft.lnk"), InstalledExe);
        AutoStartPath = InstalledExe;
        return InstalledExe;
    }

    static void Shortcut(string lnk, string target)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type);
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = target + ",0";
            sc.Description = "RemoteCraft remote desktop";
            if (AppPaths.Profile != "") sc.Arguments = "--profile " + AppPaths.Profile;
            sc.Save();
        }
        catch { }
    }

    static string RunValueName => AppPaths.Profile == "" ? "RemoteCraft" : "RemoteCraft-" + AppPaths.Profile;

    static string AutoStartPath
    {
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value == null) key.DeleteValue(RunValueName, false);
            else key.SetValue(RunValueName, $"\"{value}\" --tray" + (AppPaths.Profile != "" ? " --profile " + AppPaths.Profile : ""));
        }
    }

    public static bool AutoStart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValueName) != null;
        }
        set => AutoStartPath = value ? (IsInstalled ? InstalledExe : CurrentExe) : null;
    }
}
