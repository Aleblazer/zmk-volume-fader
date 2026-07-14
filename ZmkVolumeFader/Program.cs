using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

static class Program
{
    // uxtheme ordinal 135 (Win10 1903+): allow per-window dark theming, so
    // SetWindowTheme(..., "DarkMode_Explorer", ...) actually darkens scrollbars.
    // AllowDark (1) respects each control's requested theme, so light mode is
    // unaffected. Undocumented but widely used; fails soft if unavailable.
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
    static extern int SetPreferredAppMode(int mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    // Broadcast by a second instance so the running one surfaces itself
    // (registered messages get the same id in every process; MainForm.WndProc
    // handles it — hidden/tray windows receive broadcasts too).
    internal static readonly int WM_SHOWME = RegisterWindowMessage("ZmkVolumeFader.ShowMe");

    // ---- kernel-pool / ETW leak isolation switches ------------------------
    // The system-wide EtwD/Etwr pool leak tracks a fader actively streaming
    // reports, but "streaming" drives four distinct channels at once: the HID
    // read itself, GDI repaints, the tray-icon text, and the audio volume set.
    // Each switch surgically disables exactly one channel so a leak run can
    // attribute the flood. Diagnostic-only; no effect unless passed.
    //   --diag-sink       read + discard HID reports (no fader pipeline work)
    //   --diag-no-draw    full pipeline, but no bar/label repaints or tray text
    //   --diag-no-tray    full pipeline, but no tray-icon text updates
    //   --diag-no-volume  full pipeline + UI/session tracking, but no volume sets
    //   --diag-audio-stats show cumulative endpoint/session setter counts in title
    //   --diag-synth      ignore real reports; synthesize continuous fader motion
    //                     (2..30% triangle) below the HID layer, so draw/tray/volume
    //                     run at full rate with the hardware untouched
    internal static bool DiagSink, DiagNoDraw, DiagNoTray, DiagNoVolume, DiagSynth, DiagAudioStats;

    internal static string DiagText()
    {
        var on = new List<string>();
        if (DiagSink) on.Add("sink");
        if (DiagNoDraw) on.Add("no-draw");
        if (DiagNoTray) on.Add("no-tray");
        if (DiagNoVolume) on.Add("no-volume");
        if (DiagSynth) on.Add("synth");
        if (DiagAudioStats) on.Add("audio-stats");
        return on.Count == 0 ? "" : $"[diag: {string.Join(" ", on)}]";
    }

    internal static string ErrorLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZmkVolumeFader", "error.log");
    internal static readonly DateTime StartedAtUtc = DateTime.UtcNow;

    static bool _errorShown;
    static readonly object LogLock = new();
    static readonly Dictionary<string, long> LastLogByKey = new(StringComparer.Ordinal);

    [STAThread]
    static void Main(string[] args)
    {
        foreach (var a in args)
        {
            if (a.Equals("--diag-sink", StringComparison.OrdinalIgnoreCase)) DiagSink = true;
            else if (a.Equals("--diag-no-draw", StringComparison.OrdinalIgnoreCase)) DiagNoDraw = true;
            else if (a.Equals("--diag-no-tray", StringComparison.OrdinalIgnoreCase)) DiagNoTray = true;
            else if (a.Equals("--diag-no-volume", StringComparison.OrdinalIgnoreCase)) DiagNoVolume = true;
            else if (a.Equals("--diag-synth", StringComparison.OrdinalIgnoreCase)) DiagSynth = true;
            else if (a.Equals("--diag-audio-stats", StringComparison.OrdinalIgnoreCase)) DiagAudioStats = true;
        }

        // Single instance: a second copy would fight over the HID stream, the
        // settings file, and the target volumes. Surface the running one instead.
        using var mutex = new Mutex(true, @"Local\ZmkVolumeFader-single-instance", out bool first);
        if (!first)
        {
            PostMessage(HWND_BROADCAST, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        // Last-resort handlers: log (and tell the user once) instead of the
        // bare .NET crash dialog when a timer/UI callback throws.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnUiException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log(ex); };

        try { SetPreferredAppMode(1); } catch { }   // AllowDark
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    static void OnUiException(Exception ex)
    {
        Log(ex);
        if (_errorShown) return;   // a re-throwing timer would otherwise spam dialogs
        _errorShown = true;
        try
        {
            MessageBox.Show($"Something went wrong:\n\n{ex.Message}\n\nDetails were saved to:\n{ErrorLogPath}",
                "ZMK Volume Fader", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }

    internal static void LogRateLimited(string key, Exception ex, string? context = null, int minimumIntervalMs = 60_000)
    {
        lock (LogLock)
        {
            long now = Environment.TickCount64;
            if (LastLogByKey.TryGetValue(key, out long last) && now - last < minimumIntervalMs) return;
            LastLogByKey[key] = now;
            LogCore(ex, context);
        }
    }

    internal static void Log(Exception ex, string? context = null)
    {
        lock (LogLock) LogCore(ex, context);
    }

    static void LogCore(Exception ex, string? context)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            var f = new FileInfo(ErrorLogPath);
            if (f.Exists && f.Length > 512 * 1024) f.Delete();   // keep the log bounded
            string prefix = context == null ? "" : $"{context}: ";
            File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {prefix}{ex}\n\n");
        }
        catch { }
    }
}
