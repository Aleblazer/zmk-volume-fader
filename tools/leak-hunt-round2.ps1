# ETW leak hunt, round 2 — RUN AS ADMINISTRATOR.
# Tests the remaining audio-provider registrants the first round missed:
# sunshine (game-streaming host — prime suspect), NPSMSvc, camsvc, CmService,
# atieclxx, RadeonSoftware, and finally AudioEndpointBuilder.
# The holder is whichever one frees the stuck EtwD backlog (~47k allocations).

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Run from an elevated (Administrator) PowerShell." -ForegroundColor Red
    exit 1
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PoolTags2
{
    [DllImport("ntdll.dll")]
    static extern int NtQuerySystemInformation(int cls, IntPtr info, int len, out int retLen);
    public static long[] EtwCounts()
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
    $c = [PoolTags2]::EtwCounts()
    Write-Host ("{0,-52} EtwD={1,8}  Etwr={2,8}" -f $label, $c[0], $c[1])
    return $c
}

function Test-Candidate([string]$label, [scriptblock]$stop, [scriptblock]$restart) {
    $pre = Sample "before: $label"
    try { & $stop } catch { Write-Host "  (skip: $($_.Exception.Message))" -ForegroundColor DarkYellow; return }
    Start-Sleep 12
    $post = Sample "after:  $label"
    $freedD = $pre[0] - $post[0]
    if ($freedD -gt 10000) { Write-Host ">>> CULPRIT: $label freed $freedD EtwD allocations <<<" -ForegroundColor Green }
    elseif ($freedD -gt 2000) { Write-Host ">>> partial drop from $label ($freedD) — note it" -ForegroundColor Yellow }
    else { Write-Host "    no drop — $label cleared" }
    try { & $restart } catch { Write-Host "  (restart failed: $($_.Exception.Message))" -ForegroundColor DarkYellow }
    Start-Sleep 3
}

Sample "baseline" | Out-Null

Test-Candidate "sunshine (game-streaming host; kill)" `
    { Stop-Process -Name sunshine -Force -ErrorAction Stop } `
    { }   # restart it yourself afterward if it doesn't auto-recover

Test-Candidate "Now Playing Session Manager (NPSMSvc)" `
    { Get-Service NPSMSvc_* | Stop-Service -Force -ErrorAction Stop } `
    { Get-Service NPSMSvc_* | Start-Service }

Test-Candidate "Capability Access Manager (camsvc)" `
    { Stop-Service camsvc -Force -ErrorAction Stop } `
    { Start-Service camsvc }

Test-Candidate "Container Manager (CmService)" `
    { Stop-Service CmService -Force -ErrorAction Stop } `
    { Start-Service CmService }

Test-Candidate "AMD external events (atieclxx; kill)" `
    { Stop-Process -Name atieclxx -Force -ErrorAction Stop } `
    { }

Test-Candidate "Radeon Software (kill)" `
    { Stop-Process -Name RadeonSoftware -Force -ErrorAction Stop } `
    { }

Test-Candidate "AudioEndpointBuilder (restart; audio outage a few seconds)" `
    { Restart-Service AudioEndpointBuilder -Force -ErrorAction Stop } `
    { Start-Service Audiosrv -ErrorAction SilentlyContinue }

Write-Host "`nFinal:"
Sample "final" | Out-Null
