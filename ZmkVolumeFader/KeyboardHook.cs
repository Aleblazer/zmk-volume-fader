using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL). Observes key-down events
/// system-wide and ALWAYS passes them through (never swallows), so a bound key
/// still reaches the focused app — the unobtrusive, Discord-style model, unlike
/// RegisterHotKey which consumes the key. The hook procedure is invoked on the
/// thread that installed it, so install it on the UI thread (which pumps
/// messages) and the <see cref="KeyDown"/> handler runs there too.
/// </summary>
sealed class KeyboardHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    /// <summary>vk, ctrl, alt, shift, win — fired on each key-down (incl. OS auto-repeat).</summary>
    public event Action<int, bool, bool, bool, bool>? KeyDown;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    HookProc? _proc;   // MUST stay referenced: a GC'd delegate crashes the callback
    IntPtr _hook;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        using var proc = Process.GetCurrentProcess();
        using var mod = proc.MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod?.ModuleName), 0);
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Keep this fast — it's on the global keyboard input path.
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode is the first field
                bool ctrl = Held(VK_CONTROL), alt = Held(VK_MENU), shift = Held(VK_SHIFT),
                     win = Held(VK_LWIN) || Held(VK_RWIN);
                try { KeyDown?.Invoke(vk, ctrl, alt, shift, win); } catch { }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);   // never swallow
    }

    static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _proc = null;
    }
}
