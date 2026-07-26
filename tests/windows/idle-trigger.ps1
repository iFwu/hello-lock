[CmdletBinding()]
param(
    [string]$HelloLockPath = (Join-Path $env:LOCALAPPDATA 'Programs\HelloLock\HelloLock.exe'),

    [ValidateRange(1, 60)]
    [int]$TestIdleMinutes = 1,

    [ValidateRange(15, 600)]
    [int]$TimeoutSeconds = 100,

    [string]$OutputPath = (Join-Path $env:TEMP 'hello-lock-idle-trigger-result.json')
)

$ErrorActionPreference = 'Stop'

$HelloLockPath = (Resolve-Path -LiteralPath $HelloLockPath).Path
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null

$settingsPath = Join-Path $env:LOCALAPPDATA 'HelloLock\settings.json'
$logPath = "$OutputPath.log"
$settingsExisted = Test-Path -LiteralPath $settingsPath
$settingsBytes = if ($settingsExisted) { [IO.File]::ReadAllBytes($settingsPath) } else { $null }

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class HelloLockTrayIdleProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    public static uint GetIdleMilliseconds()
    {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        if (!GetLastInputInfo(ref info))
            throw new InvalidOperationException("GetLastInputInfo failed.");
        return unchecked((uint)Environment.TickCount - info.dwTime);
    }
}
'@

function Write-ProbeLog([string]$message) {
    Add-Content -LiteralPath $logPath -Value ('{0:o} {1}' -f [DateTime]::Now, $message) -Encoding UTF8
}

$triggered = $false
$startIdleSeconds = $null
$triggeredIdleSeconds = $null
$triggeredPids = @()
$settingsRestored = $false
Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue

try {
    $settings = if ($settingsExisted) {
        Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    } else {
        [pscustomobject]@{}
    }
    if ($null -eq $settings.PSObject.Properties['IdleMinutes']) {
        $settings | Add-Member -NotePropertyName IdleMinutes -NotePropertyValue $TestIdleMinutes
    } else {
        $settings.IdleMinutes = $TestIdleMinutes
    }
    $temporaryPath = "$settingsPath.test"
    $settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
    Move-Item -LiteralPath $temporaryPath -Destination $settingsPath -Force

    $startIdleSeconds = [HelloLockTrayIdleProbe]::GetIdleMilliseconds() / 1000
    Write-ProbeLog "START session=$([Diagnostics.Process]::GetCurrentProcess().SessionId) idle=${startIdleSeconds}s testMinutes=$TestIdleMinutes"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $lockProcess = Get-CimInstance Win32_Process -Filter "Name = 'HelloLock.exe'" |
            Where-Object {
                $_.CommandLine -match '(^|\s)[/-]lock(\s|$)' -and
                -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
                [string]::Equals(
                    [IO.Path]::GetFullPath($_.ExecutablePath),
                    [IO.Path]::GetFullPath($HelloLockPath),
                    [StringComparison]::OrdinalIgnoreCase)
            }
        if ($lockProcess) {
            $triggeredIdleSeconds = [HelloLockTrayIdleProbe]::GetIdleMilliseconds() / 1000
            $triggeredPids = @($lockProcess.ProcessId)
            Write-ProbeLog "TRIGGERED idle=${triggeredIdleSeconds}s pids=$($triggeredPids -join ',')"
            $triggered = $true
            $lockProcess | ForEach-Object {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }
            break
        }
        Start-Sleep -Seconds 2
    }
    if (-not $triggered) {
        Write-ProbeLog "NOT_TRIGGERED idle=$([HelloLockTrayIdleProbe]::GetIdleMilliseconds() / 1000)s"
    }
} finally {
    if ($settingsExisted) {
        [IO.File]::WriteAllBytes($settingsPath, $settingsBytes)
    } else {
        Remove-Item -LiteralPath $settingsPath -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
    $settingsRestored = if ($settingsExisted) {
        (Test-Path -LiteralPath $settingsPath) -and
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($settingsPath)) -eq
            [Convert]::ToBase64String($settingsBytes)
    } else {
        -not (Test-Path -LiteralPath $settingsPath)
    }
    Write-ProbeLog 'RESTORED'
}

$result = [ordered]@{
    Valid = $true
    Triggered = $triggered
    HelloLockPath = $HelloLockPath
    TestIdleMinutes = $TestIdleMinutes
    StartIdleSeconds = $startIdleSeconds
    TriggeredIdleSeconds = $triggeredIdleSeconds
    TriggeredPids = $triggeredPids
    SettingsRestored = $settingsRestored
}
$json = $result | ConvertTo-Json -Depth 5
$json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$json
if (-not $triggered -or -not $settingsRestored) { exit 2 }
