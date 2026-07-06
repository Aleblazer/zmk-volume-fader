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

    [STAThread]
    static void Main()
    {
        try { SetPreferredAppMode(1); } catch { }   // AllowDark
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
