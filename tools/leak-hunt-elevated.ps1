# ETW kernel-pool leak hunt — RUN AS ADMINISTRATOR.
#
# Context: every audio volume-set call leaks one ETW notification DataBlock
# (pool tags EtwD paged / Etwr nonpaged) into some process's never-drained
# notification queue. The backlog stays frozen after the volume-setting app
# exits, so this script kills/stops candidate holders one at a time: the one
# whose death makes the stuck EtwD count collapse is the culprit.
#
# Everything stopped is restarted afterward. Expect a brief audio hiccup on
# the final Audiosrv step.

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Please run this from an elevated (Administrator) PowerShell." -ForegroundColor Red
    exit 1
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PoolTags
{
    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int cls, IntPtr info, int len, out int retLen);
    public static long[] EtwCounts()   // [EtwD live, Etwr live]
    {
        int len = 1 << 22;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            int ret;
            if (NtQuerySystemInformation(22, buf, len, out ret) != 0) return null;
            int count = Marshal.ReadInt32(buf);
            long d = -1, r = -1;
            for (int i = 0; i < count; i++)
            {
                IntPtr e = buf + 8 + i * 40;
                string tag = System.Text.Encoding.ASCII.GetString(new byte[] {
                    Marshal.ReadByte(e, 0), Marshal.ReadByte(e, 1),
                    Marshal.ReadByte(e, 2), Marshal.ReadByte(e, 3) });
                if (tag == "EtwD") d = (uint)Marshal.ReadInt32(e, 4) - (uint)Marshal.ReadInt32(e, 8);
                if (tag == "Etwr") r = (uint)Marshal.ReadInt32(e, 24) - (uint)Marshal.ReadInt32(e, 28);
            }
            return new long[] { d, r };
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
'@

function Sample([string]$label) {
    $c = [PoolTags]::EtwCounts()
    Write-Host ("{0,-46} EtwD={1,8}  Etwr={2,8}" -f $label, $c[0], $c[1])
    return $c
}

function Test-Candidate([string]$label, [scriptblock]$stop, [scriptblock]$restart) {
    $pre = Sample "before: $label"
    try { & $stop } catch { Write-Host "  (skip: $($_.Exception.Message))" -ForegroundColor DarkYellow; return }
    Start-Sleep 12
    $post = Sample "after:  $label"
    $freedD = $pre[0] - $post[0]
    if ($freedD -gt 10000) {
        Write-Host ">>> CULPRIT: $label freed $freedD EtwD allocations <<<" -ForegroundColor Green
    } elseif ($freedD -gt 2000) {
        Write-Host ">>> partial drop from $label ($freedD) — note it" -ForegroundColor Yellow
    } else {
        Write-Host "    no drop — $label cleared"
    }
    try { & $restart } catch { Write-Host "  (restart failed: $($_.Exception.Message))" -ForegroundColor DarkYellow }
    Start-Sleep 3
}

Write-Host "`n== ETW notification-queue holder hunt ==" -ForegroundColor Cyan
Write-Host "The stuck backlog should be tens of thousands of EtwD allocations.`n"
Sample "baseline" | Out-Null

# --- user-level, previously blocked -------------------------------------
Write-Host "`nIf Discord is running, QUIT it fully now (tray icon -> Quit Discord)."
Read-Host  "Press Enter once Discord is closed (or immediately if not running)"
Sample "after Discord quit" | Out-Null

# --- elevated candidates -------------------------------------------------
Test-Candidate "lghub_updater (kill)" `
    { Stop-Process -Name lghub_updater -Force -ErrorAction Stop } `
    { }   # Logitech's scheduler restarts it on its own

Test-Candidate "AMD overlay: amdow + AMDRSServ (kill)" `
    { Stop-Process -Name amdow, AMDRSServ -Force -ErrorAction Stop } `
    { }   # AMD respawns these; a reboot fully restores the overlay

Test-Candidate "Realtek Audio Universal Service" `
    { Stop-Service RtkAudioUniversalService -Force -ErrorAction Stop } `
    { Start-Service RtkAudioUniversalService }

Test-Candidate "Realtek Bluetooth Manager Service" `
    { Stop-Service RtkBtManServ -Force -ErrorAction Stop } `
    { Start-Service RtkBtManServ }

Test-Candidate "GameInput services" `
    { Stop-Service GameInputSvc -Force -ErrorAction Stop; Stop-Service GameInputRedistService -Force -ErrorAction SilentlyContinue } `
    { Start-Service GameInputRedistService -ErrorAction SilentlyContinue; Start-Service GameInputSvc }

Test-Candidate "audiodg (kill; respawns itself)" `
    { Stop-Process -Name audiodg -Force -ErrorAction Stop } `
    { }

Test-Candidate "Windows Audio service (restart; brief audio outage)" `
    { Restart-Service Audiosrv -Force -ErrorAction Stop } `
    { }

Write-Host "`nDone. Final state:"
Sample "final" | Out-Null
Write-Host "`nIf nothing collapsed the backlog, the holder is a core svchost/system
process; the next step is an xperf pool-stack capture (ask Claude)." -ForegroundColor Cyan
