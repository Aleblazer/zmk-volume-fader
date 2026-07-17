using System.Runtime.InteropServices;

namespace ZmkVolumeFader;

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL). Observes key-down events
/// system-wide and ALWAYS passes them through (never swallows), so a bound key
/// still reaches the focused app — the unobtrusive, Discord-style model, unlike
/// RegisterHotKey which consumes the key.
///
/// The hook runs on its own dedicated message-pump thread, NOT the UI thread:
/// Windows dispatches LL-hook callbacks to the installing thread, and if that
/// thread stalls past LowLevelHooksTimeout it delays every keystroke system-wide
/// — and Windows then silently removes the hook. A dedicated thread keeps the
/// callback path independent of UI work (so <see cref="KeyDown"/> handlers must
/// marshal to the UI themselves), and a periodic re-install self-heals if the
/// hook is ever silently dropped anyway.
/// </summary>
sealed class KeyboardHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104,
        WM_SYSKEYUP = 0x0105, WM_TIMER = 0x0113, WM_QUIT = 0x0012;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    // Self-heal cadence: unhook + rehook so a silently-removed hook comes back.
    const uint RehookMs = 5 * 60_000;
    const uint RetryMs = 10_000;

    /// <summary>vk, ctrl, alt, shift, win, repeated — fired on each key-down.
    /// Raised on the hook thread: keep it fast and marshal any UI work yourself.</summary>
    public event Action<int, bool, bool, bool, bool, bool>? KeyDown;

    /// <summary>Raised (on the hook thread) if the hook could not be installed.</summary>
    public event Action? InstallFailed;

    /// <summary>While true, keys still pass through but no events are raised —
    /// used while the hotkey dialog is capturing a chord.</summary>
    public volatile bool Suspend;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    HookProc? _proc;   // MUST stay referenced: a GC'd delegate crashes the callback
    IntPtr _hook;
    Thread? _thread;
    uint _threadId;
    readonly ManualResetEventSlim _started = new();
    readonly KeyRepeatTracker _repeats = new();
    long _lastInstallFailure;

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint elapse, IntPtr timerFunc);
    [DllImport("user32.dll")]
    static extern bool KillTimer(IntPtr hWnd, UIntPtr id);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    public void Install()
    {
        if (_thread != null) return;
        _thread = new Thread(HookThread) { IsBackground = true, Name = "hotkey-hook" };
        _thread.Start();
    }

    void HookThread()
    {
        _threadId = GetCurrentThreadId();
        // Force creation of this thread's message queue before Install/Dispose can
        // race a PostThreadMessage against it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        _started.Set();
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) ReportInstallFailure();

        // Thread timer (no hwnd) — WM_TIMER lands in this thread's queue.
        UIntPtr timerId = SetTimer(IntPtr.Zero, UIntPtr.Zero,
            _hook == IntPtr.Zero ? RetryMs : RehookMs, IntPtr.Zero);
        if (timerId == UIntPtr.Zero)
            Program.LogRateLimited("hotkey-timer", new InvalidOperationException("The hotkey recovery timer could not be created."));
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_TIMER)
            {
                // Install a replacement before releasing the current hook. A
                // transient failure must not destroy a working hook; an initial
                // failure retries on a shorter cadence.
                IntPtr replacement = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                if (replacement == IntPtr.Zero)
                {
                    ReportInstallFailure();
                    if (_hook == IntPtr.Zero && timerId != UIntPtr.Zero)
                        SetTimer(IntPtr.Zero, timerId, RetryMs, IntPtr.Zero);
                    continue;
                }
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                _hook = replacement;
                _repeats.Clear();
                if (timerId != UIntPtr.Zero) SetTimer(IntPtr.Zero, timerId, RehookMs, IntPtr.Zero);
            }
        }
        if (timerId != UIntPtr.Zero) KillTimer(IntPtr.Zero, timerId);
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    void ReportInstallFailure()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastInstallFailure);
        if (last != 0 && now - last < 60_000) return;
        Interlocked.Exchange(ref _lastInstallFailure, now);
        try { InstallFailed?.Invoke(); } catch { }
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Keep this fast — it's on the global keyboard input path.
        if (nCode >= 0)
        {
            uint msg = (uint)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode is the first field
                bool repeated = _repeats.Press(vk);
                bool ctrl = Held(VK_CONTROL), alt = Held(VK_MENU), shift = Held(VK_SHIFT),
                     win = Held(VK_LWIN) || Held(VK_RWIN);
                if (!Suspend)
                    try { KeyDown?.Invoke(vk, ctrl, alt, shift, win, repeated); } catch { }
            }
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                _repeats.Release(Marshal.ReadInt32(lParam));
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);   // never swallow
    }

    static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_thread == null) return;
        KeyDown = null;
        InstallFailed = null;
        // Ask the hook thread to exit its pump (it unhooks on the way out).
        if (_started.Wait(1000)) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (!_thread.Join(2_000)) return;
        _thread = null;
        _started.Dispose();
    }
}
