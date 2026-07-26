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

HelloLock is a transparent, application-level lock for Windows. It keeps the
desktop visible, blocks ordinary keyboard input and pointer interaction with
the covered desktop, and verifies the current user through the Windows
credential UI before unlocking.

It supports Windows Hello PIN, fingerprint, face recognition, and other
credential providers exposed for the current user.

> [!IMPORTANT]
> HelloLock is not a Windows security boundary and does not replace the real
> Windows lock screen. See [Security model](#security-model).

![HelloLock protecting a synthetic demonstration workspace](docs/images/hello-lock-demo.png)

## Features

- Transparent, topmost overlay across the entire virtual desktop
- Windows Hello verification without reading or storing the PIN
- Keyboard shortcut blocking while locked
- Pointer blocking through the full-screen overlay (not a global mouse or
  touch hook)
- Standard Windows screensaver modes: `/s`, `/c`, and `/p`
- Optional tray launcher: left-click to lock immediately
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
- registers `HelloLock.scr` as the current user's screensaver;
- sets the idle timeout to 30 minutes by default;
- disables the additional Windows sign-in prompt after screensaver resume,
  because HelloLock performs its own credential verification;
- creates and starts the per-user `HelloLock Tray` interactive logon task;
- backs up the previous screensaver and legacy tray startup settings.

No administrator privileges are required. `Win+L`, sleep, and lid-close lock
behavior are not changed.

`-AllowApplicationLevelUnlock` is an explicit acknowledgement that the
installer sets `ScreenSaverIsSecure=0`. HelloLock then becomes responsible for
credential verification; a crash or privileged process termination returns to
the normal desktop rather than the Winlogon secure desktop.

To restore the previous screensaver settings, remove tray startup, and delete
the installed application files:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

Diagnostic logs are preserved by default. Add `-RemoveLogs` to delete them, or
use `-KeepFiles` when troubleshooting an uninstall.

## Usage

- Left-click the shield icon in the notification area to lock immediately.
- Right-click it for **Lock now** and **Exit tray**.
- Run `HelloLock.exe` or `HelloLock.scr /s` to lock directly.
- Press any key or click the overlay to open Windows credential verification.

The diagnostic log is written to
`%LOCALAPPDATA%\HelloLock\authentication.log`. It contains result codes and
buffer sizes, but never credential contents.

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
desktop. Pointer blocking comes from the overlay's window hit-testing, not a
global mouse/touch hook. A system or higher-band UI that Windows places above
the overlay may receive pointer, touch, or pen input. The low-level keyboard
hook still blocks ordinary keyboard input unless the trusted credential UI owns
the foreground.

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
