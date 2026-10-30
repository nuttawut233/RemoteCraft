using RemoteCraft.UI;

namespace RemoteCraft;

static class Program
{
    public const string Version = "1.0.0";

    [STAThread]
    static void Main(string[] args)
    {
        string Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // --profile lets several independent copies run on one PC (own id, settings and port), e.g. for testing.
        var profile = Arg("--profile") ?? "";
        AppPaths.Init(profile);

        using var mutex = new Mutex(true, "RemoteCraft-instance-" + profile, out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, "RemoteCraft-show-" + profile);
        if (!first)
        {
            show.Set(); // bring the running copy to the front
            return;
        }

        ApplicationConfiguration.Initialize();
        Theme.InitScale();
        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        Settings.Load();

        var form = new MainForm(args.Contains("--tray"));
        var connect = Arg("--connect");
        if (connect != null) form.Shown += (_, _) => form.Connect(connect, "control");
        new Thread(() =>
        {
            while (show.WaitOne()) Ui.Post(form.ShowMain);
        }) { IsBackground = true }.Start();
        Application.Run(form);
    }

    static void Log(Exception ex)
    {
        if (ex == null) return;
        try { File.AppendAllText(Path.Combine(AppPaths.Root, "error.log"), $"[{DateTime.Now:s}] {ex}\n\n"); } catch { }
    }
}
