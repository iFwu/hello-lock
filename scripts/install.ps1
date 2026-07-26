[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishedDirectory,

    [ValidateRange(60, 86400)]
    [int]$TimeoutSeconds = 1800,

    [Parameter(Mandatory = $true)]
    [switch]$AllowApplicationLevelUnlock
)

$ErrorActionPreference = 'Stop'

if (-not $AllowApplicationLevelUnlock) {
    throw 'HelloLock sets ScreenSaverIsSecure=0 and performs application-level credential verification. Re-run with -AllowApplicationLevelUnlock after reviewing the security model.'
}

$sourceDirectory = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$sourceExe = Join-Path $sourceDirectory 'HelloLock.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "HelloLock.exe not found in published directory: $sourceDirectory"
}

$programsDir = Join-Path $env:LOCALAPPDATA 'Programs'
$installDir = Join-Path $programsDir 'HelloLock'
$stagingDir = Join-Path $programsDir "HelloLock.staging-$PID"
$rollbackDir = Join-Path $programsDir "HelloLock.rollback-$PID"
$screenSaver = Join-Path $installDir 'HelloLock.scr'
$installedExe = Join-Path $installDir 'HelloLock.exe'
$backupPath = Join-Path $installDir 'screensaver-backup.json'
$trayBackupPath = Join-Path $installDir 'tray-run-backup.json'
$statePath = Join-Path $installDir 'install-state.json'
$desktopKey = 'HKCU:\Control Panel\Desktop'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
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
    param(
        $Task,
        [string]$ExpectedExe,
        [string]$ExpectedUser,
        [switch]$AllowLegacyDescription
    )

    if ($null -eq $Task -or $Task.Actions.Count -ne 1) { return $false }
    $action = $Task.Actions[0]
    $descriptionMatches = $Task.Description -eq $managedTaskDescription -or
        ($AllowLegacyDescription -and [string]::IsNullOrWhiteSpace($Task.Description))
    $taskSid = ConvertTo-SidString ([string]$Task.Principal.UserId)
    $expectedSid = ConvertTo-SidString $ExpectedUser
    return (Test-PathEquals $action.Execute $ExpectedExe) -and
        ([string]$action.Arguments).Trim() -eq '/tray' -and
        $null -ne $taskSid -and
        $taskSid -eq $expectedSid -and
        [string]$Task.Principal.RunLevel -eq 'Limited' -and
        $descriptionMatches
}

function Stop-InstalledHelloLockProcesses {
    param([string]$Directory)

    $normalizedDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'HelloLock.exe' OR Name = 'HelloLock.scr'" |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
                [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
                    $normalizedDirectory,
                    [StringComparison]::OrdinalIgnoreCase)
            })
        $processes |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        if ($processes.Count -gt 0) { Start-Sleep -Milliseconds 250 }
    } while ($processes.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)

    if ($processes.Count -gt 0) {
        throw "HelloLock processes did not stop within 10 seconds: $($processes.ProcessId -join ', ')"
    }
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

New-Item -ItemType Directory -Path $programsDir -Force | Out-Null

$preInstall = [ordered]@{}
foreach ($name in @('ScreenSaveActive', 'ScreenSaveTimeOut', 'ScreenSaverIsSecure', 'SCRNSAVE.EXE')) {
    $preInstall[$name] = Get-RegistryValueOrNull -Path $desktopKey -Name $name
}
$preInstallRunValue = Get-RegistryValueOrNull -Path $runKey -Name 'HelloLock'
$screenBackupJson = if (Test-Path -LiteralPath $backupPath) {
    Get-Content -LiteralPath $backupPath -Raw
} else {
    $preInstall | ConvertTo-Json
}
$trayBackupJson = if (Test-Path -LiteralPath $trayBackupPath) {
    Get-Content -LiteralPath $trayBackupPath -Raw
} else {
    [ordered]@{
        HelloLock = Get-RegistryValueOrNull -Path $runKey -Name 'HelloLock'
    } | ConvertTo-Json
}

$existingTask = Get-ScheduledTask -TaskName $trayTaskName -ErrorAction SilentlyContinue
if ($null -ne $existingTask -and -not (Test-ManagedTrayTask $existingTask $installedExe $trayUser)) {
    throw "A scheduled task named '$trayTaskName' already exists and is not owned by HelloLock."
}
$taskExistedBefore = $null -ne $existingTask
$previousTaskXml = if ($taskExistedBefore) { Export-ScheduledTask -TaskName $trayTaskName } else { $null }
$taskWasRunning = $taskExistedBefore -and $existingTask.State -eq 'Running'

$legacyTask = Get-ScheduledTask -TaskName $legacyTrayTaskName -ErrorAction SilentlyContinue
if ($null -ne $legacyTask -and -not (
    Test-ManagedTrayTask $legacyTask $installedExe $trayUser -AllowLegacyDescription)) {
    throw "A scheduled task named '$legacyTrayTaskName' already exists and is not owned by HelloLock."
}
$legacyTaskXml = if ($null -ne $legacyTask) { Export-ScheduledTask -TaskName $legacyTrayTaskName } else { $null }
$legacyTaskWasRunning = $null -ne $legacyTask -and $legacyTask.State -eq 'Running'

$applied = [ordered]@{
    ScreenSaveActive = '1'
    ScreenSaveTimeOut = [string]$TimeoutSeconds
    ScreenSaverIsSecure = '0'
    'SCRNSAVE.EXE' = $screenSaver
}
$installMovedToRollback = $false
$newInstallActive = $false
$legacyTaskRemoved = $false

try {
    Remove-Item -LiteralPath $stagingDir, $rollbackDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $stagingDir -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $stagingDir 'HelloLock.exe') `
        -Destination (Join-Path $stagingDir 'HelloLock.scr') -Force
    Set-Content -LiteralPath (Join-Path $stagingDir 'screensaver-backup.json') `
        -Value $screenBackupJson -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $stagingDir 'tray-run-backup.json') `
        -Value $trayBackupJson -Encoding UTF8

    if ($taskExistedBefore -and $existingTask.State -eq 'Running') {
        Stop-ScheduledTask -TaskName $trayTaskName -ErrorAction SilentlyContinue
    }
    if ($legacyTaskWasRunning) {
        Stop-ScheduledTask -TaskName $legacyTrayTaskName -ErrorAction SilentlyContinue
    }
    Stop-InstalledHelloLockProcesses -Directory $installDir
    if (Test-Path -LiteralPath $installDir) {
        Move-Item -LiteralPath $installDir -Destination $rollbackDir
        $installMovedToRollback = $true
    }
    Move-Item -LiteralPath $stagingDir -Destination $installDir
    $newInstallActive = $true

    $trayAction = New-ScheduledTaskAction -Execute $installedExe -Argument '/tray'
    $trayTrigger = New-ScheduledTaskTrigger -AtLogOn -User $trayUser
    $trayPrincipal = New-ScheduledTaskPrincipal -UserId $trayUser -LogonType Interactive -RunLevel Limited
    $traySettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 0)
    Register-ScheduledTask `
        -TaskName $trayTaskName `
        -Description $managedTaskDescription `
        -Action $trayAction `
        -Trigger $trayTrigger `
        -Principal $trayPrincipal `
        -Settings $traySettings `
        -Force | Out-Null

    Remove-ItemProperty -LiteralPath $runKey -Name 'HelloLock' -ErrorAction SilentlyContinue
    foreach ($name in $applied.Keys) {
        Set-ItemProperty -LiteralPath $desktopKey -Name $name -Value $applied[$name]
    }

    [ordered]@{
        SchemaVersion = 1
        Applied = $applied
        TrayTaskName = $trayTaskName
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding UTF8

    Send-DesktopSettingsChanged
    Start-ScheduledTask -TaskName $trayTaskName

    if ($null -ne $legacyTask) {
        Unregister-ScheduledTask -TaskName $legacyTrayTaskName -Confirm:$false
        $legacyTaskRemoved = $true
    }

    Remove-Item -LiteralPath $rollbackDir -Recurse -Force -ErrorAction SilentlyContinue
} catch {
    foreach ($name in $preInstall.Keys) {
        Set-RegistryValueOrRemove -Path $desktopKey -Name $name -Value $preInstall[$name]
    }
    Set-RegistryValueOrRemove -Path $runKey -Name 'HelloLock' -Value $preInstallRunValue
    Send-DesktopSettingsChanged

    $createdTask = Get-ScheduledTask -TaskName $trayTaskName -ErrorAction SilentlyContinue
    if (Test-ManagedTrayTask $createdTask $installedExe $trayUser) {
        Unregister-ScheduledTask -TaskName $trayTaskName -Confirm:$false
    }

    Stop-InstalledHelloLockProcesses -Directory $installDir
    if ($newInstallActive -and (Test-Path -LiteralPath $installDir)) {
        Remove-Item -LiteralPath $installDir -Recurse -Force
    }
    if ($installMovedToRollback -and (Test-Path -LiteralPath $rollbackDir)) {
        Move-Item -LiteralPath $rollbackDir -Destination $installDir
    }
    Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue

    if ($taskExistedBefore) {
        Register-ScheduledTask -TaskName $trayTaskName -Xml $previousTaskXml -Force | Out-Null
        if ($taskWasRunning) { Start-ScheduledTask -TaskName $trayTaskName }
    }
    if ($legacyTaskRemoved) {
        Register-ScheduledTask -TaskName $legacyTrayTaskName -Xml $legacyTaskXml -Force | Out-Null
        if ($legacyTaskWasRunning) { Start-ScheduledTask -TaskName $legacyTrayTaskName }
    } elseif ($legacyTaskWasRunning) {
        Start-ScheduledTask -TaskName $legacyTrayTaskName -ErrorAction SilentlyContinue
    }
    throw
}

Write-Host "Installed HelloLock screensaver: $screenSaver"
Write-Host "Idle timeout: $TimeoutSeconds seconds"
Write-Host 'Windows sign-in on screensaver resume: disabled (HelloLock performs its own credential verification)'
Write-Host 'Tray launcher: installed and started (left-click to lock)'
