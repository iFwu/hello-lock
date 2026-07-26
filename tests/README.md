# HelloLock QA

## CI checks

GitHub Actions runs the following checks on `windows-latest`:

- restore, build, and both publish modes with warnings treated as errors;
- PowerShell parser validation for every script under `scripts/` and `tests/`;
- `tests/install-helpers.ps1`, which executes installer helper functions against
  temporary settings and registry fixtures.

These checks do not require an interactive desktop or an installed copy of
HelloLock.

## Idle-trigger E2E

`tests/windows/idle-trigger.ps1` verifies the tray-owned idle monitor. Run it
from an interactive Windows desktop where the installed tray process is active:

```powershell
powershell -ExecutionPolicy Bypass -File tests\windows\idle-trigger.ps1 `
  -HelloLockPath "$env:LOCALAPPDATA\Programs\HelloLock\HelloLock.exe" `
  -TestIdleMinutes 1
```

The test atomically changes the idle setting, waits for the tray to start a
separate `/lock` process, stops that test lock, and restores the original
settings bytes. The default result file is
`%TEMP%\hello-lock-idle-trigger-result.json`.

## Credential transition HITL

`tests/windows/input-transition-hitl.ps1` stresses the transition from Windows
credential UI back to the HelloLock overlay. It creates a topmost draggable
canary, opens HelloLock, and attacks the short interval after the credential UI
closes:

```powershell
powershell -ExecutionPolicy Bypass -File tests\windows\input-transition-hitl.ps1 `
  -HelloLockPath "$env:LOCALAPPDATA\Programs\HelloLock\HelloLock.exe" `
  -Cycles 12
```

Each time Windows credential verification appears, physically press `Esc`
until it closes. The script injects mouse input only after it confirms that
`CredentialUIBroker` has left the foreground.

Exit codes:

- `0`: every cycle was blocked;
- `1`: the canary received a click, drag, or context-menu event;
- `2`: the run was invalid, for example because credential UI never appeared.

This test moves the real cursor, injects mouse input, and terminates only the
`/lock` process that it starts. Run it on a disposable QA desktop or while no
other pointer-sensitive work is in progress.

## Release checklist

- The idle-trigger E2E starts `/lock` and restores the previous setting.
- A known vulnerable build produces `LEAKED` in the transition HITL test.
- The release candidate completes the requested HITL cycles with zero mouse,
  drag, and menu events.
- An always-on-top application such as TrafficMonitor remains visible if it
  wins the z-order race but cannot be clicked, dragged, right-clicked, or
  scrolled while locked.
- Windows credential UI remains mouse-accessible and accepts the current
  user's PIN, fingerprint, face, or other configured provider.
- Settings, tray startup, `/lock`, and uninstall are verified after installing
  the exact release artifact.
