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
    throw 'HelloLock performs application-level credential verification. Re-run with -AllowApplicationLevelUnlock after reviewing the security model.'
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
$legacyScreenSaver = Join-Path $installDir 'HelloLock.scr'
$installedExe = Join-Path $installDir 'HelloLock.exe'
$backupPath = Join-Path $installDir 'screensaver-backup.json'
$trayBackupPath = Join-Path $installDir 'tray-run-backup.json'
$statePath = Join-Path $installDir 'install-state.json'
$settingsDir = Join-Path $env:LOCALAPPDATA 'HelloLock'
$userSettingsPath = Join-Path $settingsDir 'settings.json'
$desktopKey = 'HKCU:\Control Panel\Desktop'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$trayUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$trayUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$trayTaskName = "HelloLock Tray (iFwu, $trayUserSid)"
$legacyTrayTaskName = 'HelloLock Tray'
$managedTaskDescription = 'HelloLock tray launcher managed by iFwu/hello-lock'
$startMenuDir = Join-Path ([Environment]::GetFolderPath(
    [Environment+SpecialFolder]::Programs)) 'HelloLock'
$desktopDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$shortcutDefinitions = @(
    [ordered]@{
        Path = Join-Path $startMenuDir 'Lock with HelloLock.lnk'
        Arguments = '/lock'
        Description = 'Lock the desktop with HelloLock'
    },
    [ordered]@{
        Path = Join-Path $startMenuDir 'HelloLock Settings.lnk'
        Arguments = '/c'
        Description = 'Open HelloLock settings'
    },
    [ordered]@{
        Path = Join-Path $desktopDir 'Lock with HelloLock.lnk'
        Arguments = '/lock'
        Description = 'Lock the desktop with HelloLock'
    }
)

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

function Get-ShortcutInfo {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    try {
        return [pscustomobject]@{
            TargetPath = [string]$shortcut.TargetPath
            Arguments = ([string]$shortcut.Arguments).Trim()
        }
    } finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}

function Test-ManagedShortcut {
    param($Definition)

    $info = Get-ShortcutInfo -Path ([string]$Definition.Path)
    return $null -ne $info -and
        (Test-PathEquals $info.TargetPath $installedExe) -and
        $info.Arguments -eq ([string]$Definition.Arguments).Trim()
}

function Set-ManagedShortcut {
    param($Definition)

    $path = [string]$Definition.Path
    if ((Test-Path -LiteralPath $path) -and -not (Test-ManagedShortcut $Definition)) {
        throw "Shortcut already exists and is not owned by HelloLock: $path"
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($path)
    try {
        $shortcut.TargetPath = $installedExe
        $shortcut.Arguments = [string]$Definition.Arguments
        $shortcut.WorkingDirectory = $installDir
        $shortcut.IconLocation = "$installedExe,0"
        $shortcut.Description = [string]$Definition.Description
        $shortcut.Save()
    } finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
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

function Restore-LegacyScreenSaverSettings {
    param($State, $Backup)

    if ($null -eq $State -or $null -eq $State.PSObject.Properties['Applied']) {
        return $false
    }

    $restored = $false
    foreach ($name in @('ScreenSaveActive', 'ScreenSaveTimeOut', 'ScreenSaverIsSecure', 'SCRNSAVE.EXE')) {
        $appliedProperty = $State.Applied.PSObject.Properties[$name]
        if ($null -eq $appliedProperty) { continue }

        $current = Get-RegistryValueOrNull -Path $desktopKey -Name $name
        $appliedValue = [string]$appliedProperty.Value
        $managedFallback = $name -eq 'SCRNSAVE.EXE' -and
            (Test-PathEquals $current $legacyScreenSaver)
        if ($current -eq $appliedValue -or $managedFallback) {
            $originalProperty = if ($null -ne $Backup) { $Backup.PSObject.Properties[$name] } else { $null }
            if ($null -ne $originalProperty) {
                Set-RegistryValueOrRemove `
                    -Path $desktopKey -Name $name -Value $originalProperty.Value
            } elseif ($name -eq 'SCRNSAVE.EXE') {
                Set-RegistryValueOrRemove -Path $desktopKey -Name $name -Value $null
            } elseif ($name -eq 'ScreenSaveActive') {
                Set-RegistryValueOrRemove -Path $desktopKey -Name $name -Value '0'
            } else {
                continue
            }
            $restored = $true
        }
    }
    return $restored
}

function Ensure-IdleSetting {
    param([int]$IdleMinutes)

    $settings = if (Test-Path -LiteralPath $userSettingsPath) {
        Get-Content -LiteralPath $userSettingsPath -Raw | ConvertFrom-Json
    } else {
        [pscustomobject]@{}
    }
    if ($null -ne $settings.PSObject.Properties['IdleMinutes']) { return }

    $settings | Add-Member -NotePropertyName IdleMinutes -NotePropertyValue $IdleMinutes
    New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
    $temporaryPath = "$userSettingsPath.tmp"
    $settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
    Move-Item -LiteralPath $temporaryPath -Destination $userSettingsPath -Force
}

function Get-MigratedIdleMinutes {
    param($LegacyState, [int]$FallbackTimeoutSeconds)

    $seconds = $FallbackTimeoutSeconds
    if ($null -ne $LegacyState -and
        $null -ne $LegacyState.PSObject.Properties['Applied']) {
        $active = [string]$LegacyState.Applied.ScreenSaveActive
        if ($active -eq '0') { return 0 }

        $legacyTimeout = 0
        if ($active -eq '1' -and
            [int]::TryParse(
                [string]$LegacyState.Applied.ScreenSaveTimeOut,
                [ref]$legacyTimeout) -and
            $legacyTimeout -gt 0) {
            $seconds = $legacyTimeout
        }
    }

    if ($seconds -le 0) { return 0 }
    return [int][Math]::Max(1, [Math]::Ceiling($seconds / 60.0))
}

New-Item -ItemType Directory -Path $programsDir -Force | Out-Null

$preInstall = [ordered]@{}
foreach ($name in @('ScreenSaveActive', 'ScreenSaveTimeOut', 'ScreenSaverIsSecure', 'SCRNSAVE.EXE')) {
    $preInstall[$name] = Get-RegistryValueOrNull -Path $desktopKey -Name $name
}
$preInstallRunValue = Get-RegistryValueOrNull -Path $runKey -Name 'HelloLock'
$legacyState = if (Test-Path -LiteralPath $statePath) {
    Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
} else { $null }
$legacyScreenBackup = if (Test-Path -LiteralPath $backupPath) {
    Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
} else { $null }
$migratedIdleMinutes = Get-MigratedIdleMinutes `
    -LegacyState $legacyState -FallbackTimeoutSeconds $TimeoutSeconds
$userSettingsExistedBefore = Test-Path -LiteralPath $userSettingsPath
$userSettingsBytesBefore = if ($userSettingsExistedBefore) {
    [IO.File]::ReadAllBytes($userSettingsPath)
} else { $null }
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

$startMenuDirExistedBefore = Test-Path -LiteralPath $startMenuDir
$startMenuDirCreatedByHelloLock = -not $startMenuDirExistedBefore
if ($startMenuDirExistedBefore -and $null -ne $legacyState) {
    if ($null -ne $legacyState.PSObject.Properties['StartMenuDirectoryCreated']) {
        $startMenuDirCreatedByHelloLock = [bool]$legacyState.StartMenuDirectoryCreated
    } elseif ($null -ne $legacyState.PSObject.Properties['Shortcuts']) {
        $startMenuDirCreatedByHelloLock = @($legacyState.Shortcuts | Where-Object {
            Test-PathEquals (Split-Path -Parent ([string]$_.Path)) $startMenuDir
        }).Count -gt 0
    }
}
$shortcutBackups = @(
    foreach ($definition in $shortcutDefinitions) {
        $path = [string]$definition.Path
        $existed = Test-Path -LiteralPath $path
        if ($existed -and -not (Test-ManagedShortcut $definition)) {
            throw "Shortcut already exists and is not owned by HelloLock: $path"
        }
        [pscustomobject]@{
            Path = $path
            Existed = $existed
            Bytes = if ($existed) { [IO.File]::ReadAllBytes($path) } else { $null }
        }
    }
)

$installMovedToRollback = $false
$newInstallActive = $false
$legacyTaskRemoved = $false
$effectiveIdleMinutes = $migratedIdleMinutes

try {
    Remove-Item -LiteralPath $stagingDir, $rollbackDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $stagingDir -Recurse -Force
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
    Ensure-IdleSetting -IdleMinutes $migratedIdleMinutes
    $effectiveIdleMinutes = [int](
        Get-Content -LiteralPath $userSettingsPath -Raw | ConvertFrom-Json
    ).IdleMinutes
    $restoredLegacyScreenSaver = Restore-LegacyScreenSaverSettings `
        -State $legacyState -Backup $legacyScreenBackup
    foreach ($definition in $shortcutDefinitions) {
        Set-ManagedShortcut $definition
    }

    [ordered]@{
        SchemaVersion = 3
        TrayTaskName = $trayTaskName
        StartMenuDirectoryCreated = $startMenuDirCreatedByHelloLock
        Shortcuts = @(
            foreach ($definition in $shortcutDefinitions) {
                [ordered]@{
                    Path = [string]$definition.Path
                    Arguments = [string]$definition.Arguments
                }
            }
        )
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $statePath -Encoding UTF8

    if ($restoredLegacyScreenSaver) { Send-DesktopSettingsChanged }
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
    if ($userSettingsExistedBefore) {
        New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
        [IO.File]::WriteAllBytes($userSettingsPath, $userSettingsBytesBefore)
    } else {
        Remove-Item -LiteralPath $userSettingsPath -Force -ErrorAction SilentlyContinue
    }
    foreach ($backup in $shortcutBackups) {
        if ($backup.Existed) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup.Path) -Force | Out-Null
            [IO.File]::WriteAllBytes($backup.Path, $backup.Bytes)
        } else {
            Remove-Item -LiteralPath $backup.Path -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not $startMenuDirExistedBefore -and
        (Test-Path -LiteralPath $startMenuDir) -and
        @(Get-ChildItem -LiteralPath $startMenuDir -Force).Count -eq 0) {
        Remove-Item -LiteralPath $startMenuDir -Force
    }

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

Write-Host "Installed HelloLock: $installedExe"
Write-Host "Idle lock timeout: $effectiveIdleMinutes minutes"
Write-Host 'Tray launcher: installed and started (left-click to lock)'
Write-Host 'Shortcuts: Start Menu (Lock, Settings) and Desktop (Lock)'
