[CmdletBinding()]
param(
    [string]$HelloLockPath = (Join-Path $env:LOCALAPPDATA 'Programs\HelloLock\HelloLock.exe'),

    [ValidateRange(1, 100)]
    [int]$Cycles = 12,

    [string]$OutputPath = (Join-Path $env:TEMP 'hello-lock-input-transition-result.json')
)

$ErrorActionPreference = 'Stop'

$targetPath = (Resolve-Path -LiteralPath $HelloLockPath).Path
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class TransitionRaceInput
{
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static bool IsCredentialUiForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        uint processId;
        GetWindowThreadProcessId(foreground, out processId);
        if (processId == 0) return false;
        try
        {
            using (Process process = Process.GetProcessById((int)processId))
            {
                return string.Equals(
                    process.ProcessName,
                    "CredentialUIBroker",
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }
}
'@

$form = [Windows.Forms.Form]::new()
$form.Text = 'HelloLock transition race canary'
$form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedSingle
$form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
$form.Location = [Drawing.Point]::new(120, 120)
$form.Size = [Drawing.Size]::new(320, 160)
$form.BackColor = [Drawing.Color]::FromArgb(255, 236, 92)
$form.TopMost = $true
$form.ShowInTaskbar = $false

$label = [Windows.Forms.Label]::new()
$label.Dock = [Windows.Forms.DockStyle]::Fill
$label.TextAlign = [Drawing.ContentAlignment]::MiddleCenter
$label.Text = 'CredUI transition race canary'
$label.Font = [Drawing.Font]::new('Segoe UI', 14, [Drawing.FontStyle]::Bold)
$label.Enabled = $false
$form.Controls.Add($label)

$menu = [Windows.Forms.ContextMenuStrip]::new()
[void]$menu.Items.Add('TrafficMonitor-style menu')
$form.ContextMenuStrip = $menu

$script:cyclesTarget = $Cycles
$script:cyclesCompleted = 0
$script:credentialsObserved = 0
$script:leftDownEvents = 0
$script:rightDownEvents = 0
$script:dragMoveEvents = 0
$script:menuOpenEvents = 0
$script:dragging = $false
$script:dragOffset = [Drawing.Point]::Empty
$script:testActive = $false
$script:phase = 'start-lock'
$script:phaseStarted = [DateTime]::UtcNow
$script:attackStep = 0
$script:attackOrigin = [Drawing.Point]::Empty
$script:lockProcess = $null
$script:failure = $null

$form.Add_MouseDown({
    param($sender, $event)
    if (-not $script:testActive) { return }
    if ($event.Button -eq [Windows.Forms.MouseButtons]::Left) {
        $script:leftDownEvents++
        $script:dragging = $true
        $script:dragOffset = [Drawing.Point]::new($event.X, $event.Y)
    } elseif ($event.Button -eq [Windows.Forms.MouseButtons]::Right) {
        $script:rightDownEvents++
    }
})
$form.Add_MouseMove({
    param($sender, $event)
    if ($script:testActive -and $script:dragging) {
        $script:dragMoveEvents++
        $cursor = [Windows.Forms.Cursor]::Position
        $form.Location = [Drawing.Point]::new(
            $cursor.X - $script:dragOffset.X,
            $cursor.Y - $script:dragOffset.Y)
    }
})
$form.Add_MouseUp({
    param($sender, $event)
    if ($event.Button -eq [Windows.Forms.MouseButtons]::Left) {
        $script:dragging = $false
    }
})
$menu.Add_Opening({
    if ($script:testActive) { $script:menuOpenEvents++ }
})

function Send-Key([byte]$key) {
    [TransitionRaceInput]::keybd_event($key, 0, 0, [UIntPtr]::Zero)
    [TransitionRaceInput]::keybd_event($key, 0, 0x0002, [UIntPtr]::Zero)
}

function Has-Leak {
    return $script:leftDownEvents -gt 0 -or
        $script:rightDownEvents -gt 0 -or
        $script:dragMoveEvents -gt 0 -or
        $script:menuOpenEvents -gt 0
}

$timer = [Windows.Forms.Timer]::new()
$timer.Interval = 20
$timer.Add_Tick({
    try {
        [void][TransitionRaceInput]::SetWindowPos(
            $form.Handle,
            [IntPtr]::new(-1),
            0, 0, 0, 0,
            0x0001 -bor 0x0002 -bor 0x0010)

        $elapsed = ([DateTime]::UtcNow - $script:phaseStarted).TotalMilliseconds
        switch ($script:phase) {
            'start-lock' {
                $script:lockProcess = Start-Process -FilePath $targetPath -ArgumentList '/lock' -PassThru
                $script:phase = 'wait-lock'
                $script:phaseStarted = [DateTime]::UtcNow
                break
            }
            'wait-lock' {
                if ($elapsed -lt 800) { break }
                Send-Key 0x20
                $script:phase = 'wait-credential'
                $script:phaseStarted = [DateTime]::UtcNow
                break
            }
            'wait-credential' {
                if ([TransitionRaceInput]::IsCredentialUiForeground()) {
                    $script:credentialsObserved++
                    $script:phase = 'credential-stable'
                    $script:phaseStarted = [DateTime]::UtcNow
                } elseif ($elapsed -gt 10000) {
                    throw 'CredentialUIBroker did not become foreground.'
                }
                break
            }
            'credential-stable' {
                if ($elapsed -lt 200) { break }
                $label.Text = "Press Esc now ($($script:cyclesCompleted + 1)/$($script:cyclesTarget))"
                $script:phase = 'wait-user-close'
                $script:phaseStarted = [DateTime]::UtcNow
                break
            }
            'wait-user-close' {
                if ([TransitionRaceInput]::IsCredentialUiForeground()) {
                    if ($elapsed -gt 60000) { throw 'Timed out waiting for physical Esc.' }
                    break
                }
                $label.Text = 'Attacking CredUI close transition'
                $script:testActive = $true
                $script:attackStep = 0
                $script:attackOrigin = $form.Location
                $script:phase = 'transition-attack'
                $script:phaseStarted = [DateTime]::UtcNow
                break
            }
            'transition-attack' {
                $script:attackStep++
                $centerX = $script:attackOrigin.X + [int]($form.Width / 2)
                $centerY = $script:attackOrigin.Y + [int]($form.Height / 2)
                $offset = ($script:attackStep % 12) * 3
                [void][TransitionRaceInput]::SetCursorPos($centerX + $offset, $centerY + [int]($offset / 2))

                switch ($script:attackStep % 6) {
                    0 { [TransitionRaceInput]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) }
                    1 { [void][TransitionRaceInput]::SetCursorPos($centerX + 35, $centerY + 18) }
                    2 { [TransitionRaceInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) }
                    3 { [TransitionRaceInput]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero) }
                    4 { [TransitionRaceInput]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero) }
                }

                if (Has-Leak) {
                    $timer.Stop()
                    $form.Close()
                } elseif ($script:attackStep -ge 30) {
                    [TransitionRaceInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
                    [TransitionRaceInput]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
                    $script:testActive = $false
                    $script:cyclesCompleted++
                    if ($script:cyclesCompleted -ge $script:cyclesTarget) {
                        $timer.Stop()
                        $form.Close()
                    } else {
                        $script:phase = 'wait-relock'
                        $script:phaseStarted = [DateTime]::UtcNow
                    }
                }
                break
            }
            'wait-relock' {
                if ($elapsed -lt 1200) { break }
                $label.Text = 'Opening Windows Hello'
                Send-Key 0x20
                $script:phase = 'wait-credential'
                $script:phaseStarted = [DateTime]::UtcNow
                break
            }
        }
    } catch {
        $script:failure = $_.Exception.Message
        $timer.Stop()
        $form.Close()
    }
})

$form.Add_Shown({ $timer.Start() })

try {
    [Windows.Forms.Application]::Run($form)
} finally {
    $script:testActive = $false
    [TransitionRaceInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    [TransitionRaceInput]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
    if ($null -ne $script:lockProcess -and -not $script:lockProcess.HasExited) {
        $script:lockProcess.Kill()
        $script:lockProcess.WaitForExit(5000)
    }
    $timer.Dispose()
    $menu.Dispose()
    $form.Dispose()
}

$leaked = Has-Leak
$valid = $null -eq $script:failure -and $script:credentialsObserved -gt 0
$result = [ordered]@{
    Target = $targetPath
    Valid = $valid
    Failure = $script:failure
    CyclesTarget = $script:cyclesTarget
    CyclesCompleted = $script:cyclesCompleted
    CredentialTransitions = $script:credentialsObserved
    LeftDownEvents = $script:leftDownEvents
    RightDownEvents = $script:rightDownEvents
    DragMoveEvents = $script:dragMoveEvents
    MenuOpenEvents = $script:menuOpenEvents
    Verdict = if ($leaked) { 'LEAKED' } elseif ($script:cyclesCompleted -eq $script:cyclesTarget) { 'BLOCKED' } else { 'INVALID' }
}
$json = $result | ConvertTo-Json -Depth 5
$json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$json
if (-not $valid) { exit 2 }
if ($leaked) { exit 1 }
