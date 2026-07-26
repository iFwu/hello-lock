[CmdletBinding()]
param(
    [switch]$KeepFiles,
    [switch]$RemoveLogs
)

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\HelloLock'
$logDir = Join-Path $env:LOCALAPPDATA 'HelloLock'
$backupPath = Join-Path $installDir 'screensaver-backup.json'
$trayBackupPath = Join-Path $installDir 'tray-run-backup.json'
$statePath = Join-Path $installDir 'install-state.json'
$desktopKey = 'HKCU:\Control Panel\Desktop'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$installedExe = Join-Path $installDir 'HelloLock.exe'
$trayUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$trayUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$trayTaskName = "HelloLock Tray (iFwu, $trayUserSid)"
$legacyTrayTaskName = 'HelloLock Tray'
$managedTaskDescription = 'HelloLock tray launcher managed by iFwu/hello-lock'

function Get-RegistryValueOrNull {
    param([string]$Path, [string]$Name)

    $item = Get-ItemProperty -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    $property = $item.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return [string]$property.Value
}

function Set-RegistryValueOrRemove {
    param([string]$Path, [string]$Name, [AllowNull()]$Value)

    if ($null -eq $Value) {
        Remove-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    } else {
        Set-ItemProperty -LiteralPath $Path -Name $Name -Value $Value
    }
}

function Test-PathEquals {
    param([string]$Left, [string]$Right)

    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) {
        return $false
    }
    return [string]::Equals(
        [IO.Path]::GetFullPath($Left).TrimEnd('\'),
        [IO.Path]::GetFullPath($Right).TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)
}

function ConvertTo-SidString {
    param([string]$Identity)

    if ([string]::IsNullOrWhiteSpace($Identity)) { return $null }
    try {
        return ([Security.Principal.SecurityIdentifier]$Identity).Value
    } catch {
        try {
            return ([Security.Principal.NTAccount]$Identity).Translate(
                [Security.Principal.SecurityIdentifier]).Value
        } catch {
            return $null
        }
    }
}

function Test-ManagedTrayTask {
    param($Task, [switch]$AllowLegacyDescription)

    if ($null -eq $Task -or $Task.Actions.Count -ne 1) { return $false }
    $action = $Task.Actions[0]
    $descriptionMatches = $Task.Description -eq $managedTaskDescription -or
        ($AllowLegacyDescription -and [string]::IsNullOrWhiteSpace($Task.Description))
    $taskSid = ConvertTo-SidString ([string]$Task.Principal.UserId)
    $expectedSid = ConvertTo-SidString $trayUser
    return (Test-PathEquals $action.Execute $installedExe) -and
        ([string]$action.Arguments).Trim() -eq '/tray' -and
        $null -ne $taskSid -and
        $taskSid -eq $expectedSid -and
        [string]$Task.Principal.RunLevel -eq 'Limited' -and
        $descriptionMatches
}

function Stop-InstalledHelloLockProcesses {
    $normalizedDirectory = [IO.Path]::GetFullPath($installDir).TrimEnd('\') + '\'
    Get-CimInstance Win32_Process -Filter "Name = 'HelloLock.exe' OR Name = 'HelloLock.scr'" |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
                $normalizedDirectory,
                [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Send-DesktopSettingsChanged {
    if (-not ('HelloLockSettingsBroadcast' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HelloLockSettingsBroadcast {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint message, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);
}
'@
    }

    $result = [IntPtr]::Zero
    [void][HelloLockSettingsBroadcast]::SendMessageTimeout(
        [IntPtr]0xffff, 0x001A, [IntPtr]::Zero, 'Control Panel\Desktop',
        0x0002, 5000, [ref]$result)
}

Stop-InstalledHelloLockProcesses

$task = Get-ScheduledTask -TaskName $trayTaskName -ErrorAction SilentlyContinue
if ($null -ne $task) {
    if (-not (Test-ManagedTrayTask $task)) {
        throw "Scheduled task '$trayTaskName' is not owned by HelloLock; refusing to remove it."
    }
    Unregister-ScheduledTask -TaskName $trayTaskName -Confirm:$false
}

$legacyTask = Get-ScheduledTask -TaskName $legacyTrayTaskName -ErrorAction SilentlyContinue
if ($null -ne $legacyTask -and (Test-ManagedTrayTask $legacyTask -AllowLegacyDescription)) {
    Unregister-ScheduledTask -TaskName $legacyTrayTaskName -Confirm:$false
}

$state = if (Test-Path -LiteralPath $statePath) {
    Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
} else {
    $null
}
$backup = if (Test-Path -LiteralPath $backupPath) {
    Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
} else {
    $null
}

foreach ($name in @('ScreenSaveActive', 'ScreenSaveTimeOut', 'ScreenSaverIsSecure', 'SCRNSAVE.EXE')) {
    $current = Get-RegistryValueOrNull -Path $desktopKey -Name $name
    $applied = if ($null -ne $state) { [string]$state.Applied.$name } else { $null }
    $managedFallback = $name -eq 'SCRNSAVE.EXE' -and
        (Test-PathEquals $current (Join-Path $installDir 'HelloLock.scr'))
    if (($null -ne $state -and $current -eq $applied) -or $managedFallback) {
        $original = if ($null -ne $backup) { $backup.$name } else { $null }
        Set-RegistryValueOrRemove -Path $desktopKey -Name $name -Value $original
    }
}

$legacyRun = Get-RegistryValueOrNull -Path $runKey -Name 'HelloLock'
if ($null -eq $legacyRun) {
    $trayBackup = if (Test-Path -LiteralPath $trayBackupPath) {
        Get-Content -LiteralPath $trayBackupPath -Raw | ConvertFrom-Json
    } else {
        $null
    }
    if ($null -ne $trayBackup -and $null -ne $trayBackup.HelloLock) {
        Set-ItemProperty -LiteralPath $runKey -Name 'HelloLock' -Value ([string]$trayBackup.HelloLock)
    }
}

Send-DesktopSettingsChanged

if (-not $KeepFiles -and (Test-Path -LiteralPath $installDir)) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}
if ($RemoveLogs -and (Test-Path -LiteralPath $logDir)) {
    Remove-Item -LiteralPath $logDir -Recurse -Force
}

Write-Host 'HelloLock screensaver and tray registration removed.'
if ($KeepFiles) {
    Write-Host "Installed files kept at: $installDir"
}
if (-not $RemoveLogs) {
    Write-Host "Diagnostic logs kept at: $logDir (use -RemoveLogs to delete them)"
}
