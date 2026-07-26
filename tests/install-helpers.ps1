$ErrorActionPreference = 'Stop'

$installerPath = (Resolve-Path (Join-Path $PSScriptRoot '..\scripts\install.ps1')).Path
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $installerPath,
    [ref]$tokens,
    [ref]$errors)
if ($errors.Count -gt 0) {
    throw ($errors | Format-List | Out-String)
}

$helperNames = @(
    'Get-RegistryValueOrNull',
    'Set-RegistryValueOrRemove',
    'Test-PathEquals',
    'Get-ShortcutInfo',
    'Test-ManagedShortcut',
    'Set-ManagedShortcut',
    'Send-DesktopSettingsChanged',
    'Restore-LegacyScreenSaverSettings',
    'Ensure-IdleSetting'
    'Get-MigratedIdleMinutes'
)
foreach ($name in $helperNames) {
    $definition = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $name
    }, $true)
    if ($null -eq $definition) { throw "Installer helper not found: $name" }
    Invoke-Expression $definition.Extent.Text
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

$testRoot = Join-Path $env:TEMP "hello-lock-install-helper-test-$PID"
$settingsDir = Join-Path $testRoot 'settings'
$userSettingsPath = Join-Path $settingsDir 'settings.json'
$desktopKey = 'HKCU:\Software\iFwu\HelloLockInstallHelperTest'
$legacyScreenSaver = 'C:\Legacy\HelloLock.scr'
$installDir = Join-Path $testRoot 'install'
$installedExe = Join-Path $installDir 'HelloLock.exe'
$shortcutDefinition = [ordered]@{
    Path = Join-Path $testRoot 'Lock with HelloLock.lnk'
    Arguments = '/lock'
    Description = 'HelloLock shortcut test'
}

try {
    Send-DesktopSettingsChanged

    Ensure-IdleSetting -IdleMinutes 15
    $settings = Get-Content -LiteralPath $userSettingsPath -Raw | ConvertFrom-Json
    Assert-Equal 15 $settings.IdleMinutes 'Idle migration failed.'
    Ensure-IdleSetting -IdleMinutes 30
    $settings = Get-Content -LiteralPath $userSettingsPath -Raw | ConvertFrom-Json
    Assert-Equal 15 $settings.IdleMinutes 'Idle migration overwrote an existing setting.'

    Assert-Equal 0 (Get-MigratedIdleMinutes `
        -LegacyState ([pscustomobject]@{
            Applied = [pscustomobject]@{
                ScreenSaveActive = '0'
                ScreenSaveTimeOut = '1800'
            }
        }) `
        -FallbackTimeoutSeconds 1800) 'Disabled idle migration failed.'
    Assert-Equal 30 (Get-MigratedIdleMinutes `
        -LegacyState ([pscustomobject]@{
            Applied = [pscustomobject]@{
                ScreenSaveActive = '1'
                ScreenSaveTimeOut = '1800'
            }
        }) `
        -FallbackTimeoutSeconds 900) 'Enabled idle migration failed.'
    Assert-Equal 15 (Get-MigratedIdleMinutes `
        -LegacyState $null -FallbackTimeoutSeconds 900) `
        'Fresh-install idle default failed.'

    New-Item -Path $desktopKey -Force | Out-Null
    $applied = [pscustomobject]@{
        ScreenSaveActive = '1'
        ScreenSaveTimeOut = '1800'
        ScreenSaverIsSecure = '0'
        'SCRNSAVE.EXE' = $legacyScreenSaver
    }
    foreach ($property in $applied.PSObject.Properties) {
        Set-ItemProperty -LiteralPath $desktopKey -Name $property.Name -Value $property.Value
    }
    $state = [pscustomobject]@{ Applied = $applied }
    $backup = [pscustomobject]@{
        ScreenSaveActive = '0'
        ScreenSaveTimeOut = $null
        ScreenSaverIsSecure = $null
        'SCRNSAVE.EXE' = $null
    }

    $restored = Restore-LegacyScreenSaverSettings -State $state -Backup $backup
    Assert-Equal $true $restored 'Legacy screensaver state was not restored.'
    Assert-Equal '0' (Get-RegistryValueOrNull -Path $desktopKey -Name ScreenSaveActive) `
        'ScreenSaveActive restore failed.'
    foreach ($name in @('ScreenSaveTimeOut', 'ScreenSaverIsSecure', 'SCRNSAVE.EXE')) {
        Assert-Equal $null (Get-RegistryValueOrNull -Path $desktopKey -Name $name) `
            "$name should have been removed."
    }

    foreach ($property in $applied.PSObject.Properties) {
        Set-ItemProperty -LiteralPath $desktopKey -Name $property.Name -Value $property.Value
    }
    $restoredWithoutBackup = Restore-LegacyScreenSaverSettings -State $state -Backup $null
    Assert-Equal $true $restoredWithoutBackup 'Missing-backup cleanup did not run.'
    Assert-Equal '0' (Get-RegistryValueOrNull -Path $desktopKey -Name ScreenSaveActive) `
        'Missing-backup cleanup did not disable the legacy screensaver.'
    Assert-Equal '1800' (Get-RegistryValueOrNull -Path $desktopKey -Name ScreenSaveTimeOut) `
        'Missing-backup cleanup changed the timeout.'
    Assert-Equal '0' (Get-RegistryValueOrNull -Path $desktopKey -Name ScreenSaverIsSecure) `
        'Missing-backup cleanup changed the secure flag.'
    Assert-Equal $null (Get-RegistryValueOrNull -Path $desktopKey -Name 'SCRNSAVE.EXE') `
        'Missing-backup cleanup left the legacy path registered.'

    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Set-Content -LiteralPath $installedExe -Value 'test executable'
    Set-ManagedShortcut $shortcutDefinition
    Assert-Equal $true (Test-ManagedShortcut $shortcutDefinition) `
        'Managed shortcut was not recognized.'
    $shortcutInfo = Get-ShortcutInfo -Path $shortcutDefinition.Path
    Assert-Equal '/lock' $shortcutInfo.Arguments 'Shortcut arguments are incorrect.'

    Write-Output 'INSTALL_HELPERS_OK'
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $desktopKey -Recurse -Force -ErrorAction SilentlyContinue
}
