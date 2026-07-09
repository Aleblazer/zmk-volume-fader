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

    static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZmkVolumeFader", "error.log");

    static bool _errorShown;

    [STAThread]
    static void Main()
    {
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
            MessageBox.Show($"Something went wrong:\n\n{ex.Message}\n\nDetails were saved to:\n{LogPath}",
                "ZMK Volume Fader", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }

    static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var f = new FileInfo(LogPath);
            if (f.Exists && f.Length > 512 * 1024) f.Delete();   // keep the log bounded
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
