<p align="center">
  <img src="src/Assets/HelloLock.png" width="104" alt="HelloLock icon">
</p>

<h1 align="center">HelloLock</h1>

<p align="center">
  A transparent Windows desktop lock with native credential verification.
</p>

<p align="center">
  <a href="README.zh-CN.md">简体中文</a>
</p>

HelloLock is an application-level lock for Windows. It keeps the desktop
recognizable through an in-memory blurred snapshot, blocks ordinary keyboard
input and pointer interaction with the covered desktop, and verifies the
current user through the Windows credential UI before unlocking.

It supports Windows Hello PIN, fingerprint, face recognition, and other
credential providers exposed for the current user.

> [!IMPORTANT]
> HelloLock is not a Windows security boundary and does not replace the real
> Windows lock screen. See [Security model](#security-model).

![HelloLock protecting a synthetic demonstration workspace](docs/images/hello-lock-demo.png)

## Features

- Gaussian-blurred in-memory snapshot across the entire virtual desktop
- Windows Hello verification without reading or storing the PIN
- Keyboard shortcut blocking while locked
- Global low-level mouse and keyboard guards while locked
- Idle auto-lock driven by the per-user tray process, independent of the
  legacy Windows screensaver runtime
- Windows session-state awareness prevents an application lock from being
  added behind the real Windows lock screen
- Tray launcher: left-click to lock immediately; right-click for settings
- Per-user single-instance protection for both tray and lock processes
- Reversible per-user installation; no administrator privileges or service
  required

## Requirements

- Windows 10 version 2004 (build 19041) or later
- x64 Windows
- A credential provider available to the current user
- .NET 8 SDK for building from source

Releases provide two packages:

- **self-contained** (recommended): works without a separately installed .NET
  runtime; approximately 73 MB compressed;
- **framework-dependent**: approximately 6 MB compressed, but requires the
  [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0).

## Install

Download either `hello-lock-vX.Y.Z-win-x64-self-contained.zip` or
`hello-lock-vX.Y.Z-win-x64-framework-dependent.zip` from
[Releases](https://github.com/iFwu/hello-lock/releases), extract it, and run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 `
  -PublishedDirectory publish `
  -TimeoutSeconds 1800 `
  -AllowApplicationLevelUnlock
```

The installer:

- copies the self-contained application to
  `%LOCALAPPDATA%\Programs\HelloLock`;
- stores a 30-minute idle lock timeout by default;
- creates and starts the per-user `HelloLock Tray` interactive logon task;
- creates Start Menu shortcuts for locking and settings, plus a desktop lock
  shortcut;
- backs up the legacy tray startup setting.

When upgrading from `v0.1.0`, the installer migrates the existing idle timeout
and removes HelloLock's legacy Windows screensaver registration without
overwriting unrelated screensaver settings.

No administrator privileges are required. `Win+L`, sleep, and lid-close lock
behavior are not changed.

`-AllowApplicationLevelUnlock` is an explicit acknowledgement that HelloLock
performs credential verification on the normal desktop. A crash or privileged
process termination returns to the normal desktop rather than the Winlogon
secure desktop.

To remove tray startup and delete the installed application files:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

Diagnostic logs are preserved by default. Add `-RemoveLogs` to delete them, or
use `-KeepFiles` when troubleshooting an uninstall.

## Usage

- Left-click the HelloLock icon in the notification area to lock immediately.
- Right-click it for **Lock now**, **Settings**, and **Exit tray**.
- Run `HelloLock.exe /lock` to lock directly.
- Press any key or click the overlay to open Windows credential verification.

Authentication diagnostics are written to
`%LOCALAPPDATA%\HelloLock\authentication.log`; idle-trigger events are written
to `idle-monitor.log`. Neither log contains credential contents.

## Build

```powershell
dotnet restore src\HelloLock.csproj
dotnet build src\HelloLock.csproj -c Release --no-restore
dotnet publish src\HelloLock.csproj -c Release -r win-x64 `
  --self-contained false -o artifacts\framework-dependent

dotnet publish src\HelloLock.csproj -c Release -r win-x64 `
  --self-contained true -o artifacts\self-contained
```

Both packages use multi-file publishing. WPF single-file bundling caused native
DLL load failures on one tested Windows machine. Trimming and NativeAOT are not
supported for this WPF/WinForms application.

The automated and interactive Windows checks used for releases are documented
in [`tests/README.md`](tests/README.md).

## How authentication works

HelloLock follows the same Windows device-authentication pattern used by
Chromium:

1. Call `CredUIPromptForWindowsCredentials` with only
   `CREDUIWIN_ENUMERATE_CURRENT_USER` and let Windows select the authentication
   package.
2. Pass the returned serialized credential buffer and package to
   `LsaLogonUser` through an untrusted LSA connection.
3. Require the returned token SID to equal the SID of the currently signed-in
   user.
4. Zero and free the serialized credential buffer immediately after use.

Preselecting the `Negotiate` package or adding extra packing flags can hide the
PIN provider for passwordless Microsoft accounts, so HelloLock deliberately
uses the package returned by CredUI.

Reference implementation:
[Chromium `password_manager_util_win.cc`](https://github.com/chromium/chromium/blob/main/chrome/browser/password_manager/password_manager_util_win.cc).

## Security model

HelloLock is designed to prevent ordinary local interaction with a visible
desktop. Low-level mouse and keyboard hooks block ordinary local input. Windows
credential UI remains interactive through the operating system's input
isolation. Touch or pen input that Windows does not promote to mouse messages,
plus system UI in a higher window band, remains outside the guaranteed
input-blocking boundary.

On tested Windows systems, the normal Task Manager remains behind the overlay
and is not a direct keyboard/mouse bypass. This is observed behavior, not a
security guarantee across all Windows versions and configurations.

However, HelloLock runs on the normal user desktop, not the Winlogon secure
desktop. It has no tamper protection. An administrator or SYSTEM process,
remote-management software, debugger or injector, forced sign-out, reboot, or
application crash can remove the protection. Use the real Windows lock screen
when protection against an active attacker is required.

See [SECURITY.md](SECURITY.md) for reporting security issues.

## License

[MIT](LICENSE)
