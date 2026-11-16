# Drives the real app window with the real mouse and keyboard (SendInput), the way a person would:
# type a 伪装域名 with a Chinese full stop, click 应用, check it is saved; connect, change it while
# connected, run 网络诊断, close. Runs in Windows PowerShell 5.1 on the build machine.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Data,
    [Parameter(Mandatory)] [string] $Out,
    [Parameter(Mandatory)] [string] $Shots
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Input {
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    static void Send(INPUT i) { if (SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))) != 1) throw new Exception("SendInput failed: " + Marshal.GetLastWin32Error()); }
    public static void Click(int x, int y) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(80);
        var down = new INPUT { type = 0 }; down.u.mi.flags = 0x0002; Send(down);
        var up = new INPUT { type = 0 }; up.u.mi.flags = 0x0004; Send(up);
        System.Threading.Thread.Sleep(150);
    }
    public static void Key(ushort vk, bool keyUp) { var k = new INPUT { type = 1 }; k.u.ki.vk = vk; k.u.ki.flags = keyUp ? 2u : 0u; Send(k); }
    public static void Press(ushort vk) { Key(vk, false); Key(vk, true); System.Threading.Thread.Sleep(40); }
    public static void Chord(ushort mod, ushort vk) { Key(mod, false); Press(vk); Key(mod, true); System.Threading.Thread.Sleep(40); }
    // Each character as a real key press (KEYEVENTF_UNICODE), so 。 arrives exactly as a Chinese input method types it.
    public static void Type(string text) {
        foreach (var ch in text) {
            var d = new INPUT { type = 1 }; d.u.ki.scan = ch; d.u.ki.flags = 0x0004; Send(d);
            var u = new INPUT { type = 1 }; u.u.ki.scan = ch; u.u.ki.flags = 0x0004 | 0x0002; Send(u);
            System.Threading.Thread.Sleep(30);
        }
    }
}
'@
[Input]::SetProcessDPIAware() | Out-Null

$failures = 0
New-Item -ItemType Directory -Force $Shots | Out-Null
Set-Content -Path $Out -Value '' -Encoding utf8
function Log($text) { Write-Host $text; Add-Content -Path $Out -Value $text -Encoding utf8 }
function Check($ok, $what) { if ($ok) { Log "ok    $what" } else { Log "FAIL  $what"; $script:failures++ } }
function Shot($name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save((Join-Path $Shots "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
$AE = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
function Find($root, $id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    for ($i = 0; $i -lt 50; $i++) {
        $e = $root.FindFirst($Tree::Descendants, $cond)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 200
    }
    throw "control $id not found"
}
function Center($e) {
    $r = $e.Current.BoundingRectangle
    return @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
}
function ClickOn($e) { $p = Center $e; [Input]::Click($p[0], $p[1]) }
function Value($e) { return $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function State() { return (Find $script:window 'StateLabel').Current.Name }
function WaitFor([scriptblock] $ok, [int] $seconds) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) { if (& $ok) { return $true }; Start-Sleep -Milliseconds 500 }
    return [bool](& $ok)
}
function SavedSni() {
    try { return (Get-Content -Raw -Encoding utf8 (Join-Path $Data 'settings.json') | ConvertFrom-Json).Sni } catch { return $null }
}
# The command line of the running usque, to see which 伪装域名 it really uses.
function UsqueArgs() {
    $p = Get-CimInstance Win32_Process -Filter "Name='usque.exe'" | Select-Object -First 1
    if ($p) { return $p.CommandLine } else { return '' }
}
# Click into the box, select what is there, type the new text the way a person does.
function TypeSni($text, [switch] $Enter) {
    ClickOn (Find $script:window 'SniBox')
    [Input]::Press(0x23)          # End
    [Input]::Chord(0x10, 0x24)    # Shift+Home: select all
    [Input]::Type($text)
    if ($Enter) { [Input]::Press(0x0D) } else { ClickOn (Find $script:window 'ApplySni') }
    Start-Sleep -Milliseconds 800
}

$env:WARPSWITCH_DATA = $Data
New-Item -ItemType Directory -Force $Data | Out-Null
$hasAccount = Test-Path (Join-Path $Data 'account.json')
Log "info  data: $Data (account: $hasAccount)"
$app = Start-Process -FilePath $Exe -PassThru
try {
    $byPid = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)
    $script:window = $null
    for ($i = 0; $i -lt 100 -and -not $script:window; $i++) { Start-Sleep -Milliseconds 200; $script:window = $AE::RootElement.FindFirst($Tree::Children, $byPid) }
    Check ($null -ne $script:window) "window opened: $($script:window.Current.Name)"
    if (-not $script:window) { throw 'no window' }
    [Input]::SetForegroundWindow([IntPtr]$script:window.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 500
    Shot 'ui-1-opened'

    $box = Find $script:window 'SniBox'
    Check ($box.Current.IsEnabled -and (Value $box) -ne '') "伪装域名 box is shown and editable: '$(Value $box)'"

    # 1. Not connected: type with a Chinese full stop, click 应用.
    TypeSni 'dock。schoology。com'
    Check ((SavedSni) -eq 'dock.schoology.com') "typed 'dock。schoology。com' + clicked 应用 -> saved as $(SavedSni)"
    Check ((Value (Find $script:window 'SniBox')) -eq 'dock.schoology.com') "box now shows dock.schoology.com"

    # 2. Something that isn't a domain: refused with a message, nothing saved.
    TypeSni '学校网站'
    $hint = (Find $script:window 'SniHint').Current.Name
    Check ($hint -like '*不是有效的域名*' -and (SavedSni) -eq 'dock.schoology.com') "invalid input refused: $hint"
    Shot 'ui-2-invalid'
    TypeSni 'www.cloudflare.com' -Enter
    Check ((SavedSni) -eq 'www.cloudflare.com') "typed www.cloudflare.com + Enter -> saved as $(SavedSni)"

    if ($hasAccount) {
        # 3. Connect with the mouse, change the 伪装域名 while connected.
        ClickOn (Find $script:window 'MainButton')
        $on = WaitFor { (State) -eq '已连接' } 90
        Check $on "clicked 开启 -> $(State): $((Find $script:window 'MessageLabel').Current.Name)"
        Check ((UsqueArgs) -match 'socks -b 127\.0\.0\.1 -p 1080 --http2 -s www\.cloudflare\.com') "usque runs as in the reference: $(UsqueArgs)"
        Shot 'ui-3-connected'
        if ($on) {
            Check ((Find $script:window 'SniBox').Current.IsEnabled) "伪装域名 box is editable while connected"
            TypeSni 'www.bing.com'
            Check ((SavedSni) -eq 'www.bing.com') "while connected: typed www.bing.com + clicked 应用 -> saved"
            Start-Sleep -Seconds 2
            $again = WaitFor { (State) -eq '已连接' -and (UsqueArgs) -match '-s www\.bing\.com' } 90
            Check $again "reconnected by itself with www.bing.com: $(State), exit IP $((Find $script:window 'IpValue').Current.Name)"
            Log "info  running: usque $(UsqueArgs)"
            Shot 'ui-4-changed-while-connected'
        }

        # 4. 网络诊断 from the window.
        ClickOn (Find $script:window 'Diagnose')
        $diagCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'DiagnosticsWindow')
        $diag = $null
        for ($i = 0; $i -lt 50 -and -not $diag; $i++) {
            Start-Sleep -Milliseconds 200
            $diag = $AE::RootElement.FindFirst($Tree::Children, $diagCond)
            if (-not $diag) { $diag = $script:window.FindFirst($Tree::Children, $diagCond) }
        }
        Check ($null -ne $diag) "clicked 网络诊断 -> window opened"
        if ($diag) {
            $text = Find $diag 'DiagnosticsOutput'
            $done = WaitFor { (Value $text) -match '结论' } 90
            $result = (Value $text) -split "`r`n" | Where-Object { $_ -match '结论|✅|❌' }
            $result | ForEach-Object { Log "    | $_" }
            Check ($done -and (Value $text) -match '✅') "网络诊断 finished and found a working 伪装域名"
            Shot 'ui-5-diagnostics'
            $diag.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        }

        # 5. Close with the mouse.
        ClickOn (Find $script:window 'MainButton')
        Check (WaitFor { (State) -eq '已关闭' } 30) "clicked 关闭 -> $(State)"
        $reg = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
        Check (-not ($reg.ProxyEnable -eq 1 -and $reg.ProxyServer -eq '127.0.0.1:7890')) "system proxy restored (ProxyEnable=$($reg.ProxyEnable) ProxyServer=$($reg.ProxyServer))"
        Shot 'ui-6-closed'
    }
    else { Log 'info  no WARP account (connection test did not register one); skipped the connected part' }
}
catch { Check $false "unexpected error: $_" }
finally {
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
}
if ($failures -eq 0) { Log 'PASS: the 伪装域名 can be typed and applied with mouse and keyboard, off and while connected' } else { Log "FAILED: $failures check(s)" }
exit $failures
