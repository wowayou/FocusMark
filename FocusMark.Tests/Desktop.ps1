# Run on an unlocked Windows desktop. Uses disposable profiles and moves focus/mouse.
param([switch]$Performance, [switch]$Ime, [ValidateSet('J','K')][string]$Letter = 'J', [ValidateRange(0,60)][int]$Cycles = 0, [string]$Code, [string]$Browser = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe")
$ErrorActionPreference = 'Stop'
if ($Performance) {
    if ($PSBoundParameters.ContainsKey('Cycles') -and $Cycles -ne 24) { throw 'Performance budget v1 requires 24 cycles' }
    $Cycles = 24
    . "$PSScriptRoot\PerfBudget.ps1"
}
# A renamed EXE still owns the same per-session mutex.
$existing = $null
if ([Threading.Mutex]::TryOpenExisting('Local\FocusMark.Single',[ref]$existing)) {
    $existing.Dispose()
    throw 'Exit FocusMark from its tray menu before starting desktop acceptance'
}
if (!$Code) { $Code = Join-Path (Split-Path (Split-Path (Get-Command code.cmd).Source)) 'Code.exe' }
if (!(Test-Path $Code) -or !(Test-Path $Browser)) { throw 'VS Code and Edge are required' }
if (Get-Process FocusMark -ErrorAction SilentlyContinue) { throw 'Exit the running FocusMark before testing' }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Desktop {
 [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32")] public static extern void SwitchToThisWindow(IntPtr h,bool altTab);
 [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32")] public static extern bool ShowWindowAsync(IntPtr h,int cmd);
 [DllImport("user32")] public static extern bool IsIconic(IntPtr h);
 // Native x64 INPUT union, keyboard member at offset 8.
 [StructLayout(LayoutKind.Explicit, Size=40)] public struct Stroke {
    [FieldOffset(0)] public int type; [FieldOffset(8)] public ushort key; [FieldOffset(12)] public uint flags;
 }
 [DllImport("user32",SetLastError=true)] static extern uint SendInput(uint n,Stroke[] keys,int size);
 public static void SendChord(byte[] keys) {
    var strokes=new Stroke[keys.Length*2];
    for(int i=0;i<keys.Length;i++) {
        strokes[i]=new Stroke {type=1,key=keys[i]};
        strokes[strokes.Length-1-i]=new Stroke {type=1,key=keys[i],flags=2};
    }
    if(SendInput((uint)strokes.Length,strokes,40)!=strokes.Length) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
 }
 [DllImport("user32")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32")] public static extern void mouse_event(int flags,int x,int y,int data,int extra);
 [DllImport("user32")] public static extern IntPtr WindowFromPoint(long pt);
 [DllImport("user32")] public static extern IntPtr GetAncestor(IntPtr h,int flags);
 [DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
 [StructLayout(LayoutKind.Sequential)] public struct Bounds { public int Left, Top, Right, Bottom; }
 [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h,out Bounds bounds);
 [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32")] public static extern bool PostMessage(IntPtr h,int msg,IntPtr w,IntPtr l);
 [DllImport("user32")] public static extern IntPtr GetKeyboardLayout(uint thread);
 [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h,IntPtr pid);
}
'@
$null = [Desktop]::SetThreadDpiAwarenessContext([IntPtr](-4))
$ae = [Windows.Automation.AutomationElement]
$base = Join-Path ([IO.Path]::GetTempPath()) ('FocusMark-GUI-' + [guid]::NewGuid().ToString('N'))
$appExe = (Resolve-Path "$PSScriptRoot\..\dist\FocusMark.exe").ProviderPath
$null = New-Item $base -ItemType Directory
$owned = New-Object 'Collections.Generic.List[Diagnostics.Process]'
function Launch($exe, $arguments, [bool]$app = $false) {
    $info = New-Object Diagnostics.ProcessStartInfo($exe, $arguments)
    $info.UseShellExecute = $false
    if ($app) { $info.EnvironmentVariables['LOCALAPPDATA'] = $base }
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $p = [Diagnostics.Process]::Start($info)
    $p.BeginOutputReadLine(); $p.BeginErrorReadLine()
    $owned.Add($p)
    $p
}
function WaitFor($name, [scriptblock]$check, $seconds = 8) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    do { $value = & $check; if ($value) { return $value }; Start-Sleep -Milliseconds 15 } while ($t.Elapsed.TotalSeconds -lt $seconds)
    throw "Timeout: $name"
}
function Handle($p) { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $p.MainWindowHandle } }
function Front($h) {
    # Only used for arranging the two disposable fixture windows.
    if (![Desktop]::SetForegroundWindow($h)) { [Desktop]::SwitchToThisWindow($h,$true) }
    $null = WaitFor 'foreground switch' { [Desktop]::GetForegroundWindow() -eq $h } 2
}
function Key($h, [byte[]]$keys) {
    if ([Desktop]::GetForegroundWindow() -ne $h) { throw 'Focus changed before keystroke' }
    [Desktop]::SendChord($keys)
}
function Item($id) {
    $root = $ae::FromHandle($script:panel)
    $cond = New-Object Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $id)
    $el = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
    if ($el -and !$el.Current.IsOffscreen) { $el }
}
function Click($id, [bool]$middle = $false) {
    $el = WaitFor $id { Item $id }
    $pt = $el.GetClickablePoint(); $x = [int]$pt.X; $y = [int]$pt.Y
    $null = [Desktop]::SetCursorPos($x,$y)
    $under = [Desktop]::WindowFromPoint(([long]$y -shl 32) -bor ([long]$x -band 0xffffffffL))
    if ([Desktop]::GetAncestor($under,2) -ne $script:panel) { throw "Click obstructed: $id" }
    if ($middle) { [Desktop]::mouse_event(32,0,0,0,0); [Desktop]::mouse_event(64,0,0,0,0) }
    else { [Desktop]::mouse_event(2,0,0,0,0); [Desktop]::mouse_event(4,0,0,0,0) }
}
function TextValue { (Item 'Input').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern) }
function Cleared {
    $file="$base\FocusMark\note.json"
    if ($Letter -eq 'J') { return !(Test-Path $file) }
    try { $stream=[IO.File]::Open($file,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)) }
    catch [IO.IOException] { return $false }
    $reader=New-Object IO.StreamReader($stream)
    try { $saved=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    return $null -eq $saved.text -and $saved.key -eq $Letter
}
function EditorFrom($h) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Key $h @(17,18,([int][char]$Letter))
    try {
        $script:panel = WaitFor 'editor HWND' { $fg = [Desktop]::GetForegroundWindow(); if ($fg -ne [IntPtr]::Zero -and ($ae::FromHandle($fg)).Current.ProcessId -eq $app.Id) { $fg } }
    } catch {
        $c = New-Object Windows.Automation.PropertyCondition($ae::ProcessIdProperty,$app.Id)
        $own = $ae::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$c)
        throw "Editor foreground timeout. Exited=$($app.HasExited) visible_app_windows=$($own.Count) foreground_still_fixture=$([Desktop]::GetForegroundWindow() -eq $h)"
    }
    $foregroundMs = $sw.ElapsedMilliseconds
    $null = WaitFor 'focused input' { (Item 'Input').Current.HasKeyboardFocus }
    $elapsed = $sw.ElapsedMilliseconds
    if ($elapsed -ge 200) { Write-Host "slow_sample_ms=$elapsed foreground_observed_ms=$foregroundMs focus_probe_ms=$($elapsed-$foregroundMs)" }
    Write-Host "hotkey_to_focused_input_ms=$elapsed"
    return $elapsed
}
function KillOwned($p) { if ($p -and !$p.HasExited) { & "$env:windir\system32\taskkill.exe" /PID $p.Id /T /F | Out-Null; $null = $p.WaitForExit(5000) } }
try {
    $null = New-Item "$base\code\User" -ItemType Directory -Force
    [IO.File]::WriteAllText("$base\code\User\settings.json", '{"telemetry.telemetryLevel":"off","update.mode":"none","workbench.startupEditor":"none"}')
    [IO.File]::WriteAllText("$base\FocusMark-test.txt", 'FocusMark desktop acceptance')
    if ($Letter -ne 'J') {
        $null=New-Item "$base\FocusMark" -ItemType Directory
        [IO.File]::WriteAllText("$base\FocusMark\note.json",'{"text":null,"key":"K"}')
    }
    $start = [Diagnostics.Stopwatch]::StartNew()
    $app = Launch $appExe '' $true
    if (!$app.WaitForInputIdle(8000)) { throw 'Startup timeout' }
    $startupMs = $start.ElapsedMilliseconds
    Write-Output ('launch_to_first_input_idle_ms=' + $startupMs)
    $editor = Launch $Code "--new-window --disable-extensions --disable-updates --user-data-dir=`"$base\code`" `"$base\FocusMark-test.txt`""
    $codeH = WaitFor 'VS Code window' { Handle $editor } 30
    $edge = Launch $Browser "--no-first-run --disable-background-networking --disable-sync --user-data-dir=`"$base\edge`" --app=about:blank"
    $edge = WaitFor 'Edge window' {
        Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero -and (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)").CommandLine.Contains("$base\edge") } | Select-Object -First 1
    } 25
    $owned.Add($edge)
    $edgeH = Handle $edge
    Front $codeH
    $firstMs = EditorFrom $codeH
    Write-Output ('first_hotkey_to_focused_input_ms=' + $firstMs)
    (TextValue).SetValue('Continue line 12')
    Key $panel @(13)
    $null = WaitFor 'saved pill' { Item 'Pill' }
    Add-Type -AssemblyName System.Windows.Forms
    $screen = [Windows.Forms.Screen]::FromHandle($panel)
    $box = New-Object Desktop+Bounds
    $null = [Desktop]::GetWindowRect($panel,[ref]$box)
    if ([Math]::Abs(($box.Left+$box.Right)-($screen.WorkingArea.Left+$screen.WorkingArea.Right)) -gt 2) {throw 'Pill not centered'}
    $measuredDpi = [Desktop]::GetDpiForWindow($panel)
    Write-Output ('dpi={0} pill_pixels={1}x{2} top_offset={3}' -f $measuredDpi,($box.Right-$box.Left),($box.Bottom-$box.Top),($box.Top-$screen.WorkingArea.Top))
    Front $edgeH
    Key $edgeH @(122) # F11 on the disposable browser window
    $null = WaitFor 'Edge fills monitor' { $null=[Desktop]::GetWindowRect($edgeH,[ref]$box); $box.Top -eq $screen.Bounds.Top -and $box.Bottom -eq $screen.Bounds.Bottom -and $box.Left -eq $screen.Bounds.Left -and $box.Right -eq $screen.Bounds.Right }
    Click 'Pill' $true
    $null=WaitFor 'hidden over fullscreen browser' { !(Item 'Pill') -and [Desktop]::GetForegroundWindow() -eq $edgeH }
    Write-Output 'PASS fullscreen Edge: middle-click hides capsule and leaves Edge foreground'
    Key $edgeH @(122)
    EditorFrom $edgeH
    (TextValue).SetValue('cancel this edit')
    Click 'Rebind'
    Key $panel @(27)
    Click 'Pill'
    if ((Item 'FullNote').Current.Name -ne 'Continue line 12') { throw 'Esc changed saved note' }
    Click 'Return'
    $null = WaitFor 'original VS Code foreground' { [Desktop]::GetForegroundWindow() -eq $codeH }
    $null = WaitFor 'pill retained' { Item 'Pill' }
    Write-Output 'PASS VS Code capture, Edge hotkey edit/Esc, return to exact VS Code HWND; note retained'
    Front $edgeH
    EditorFrom $edgeH
    (TextValue).SetValue('Updated note')
    Key $panel @(13)
    $null = [Desktop]::ShowWindowAsync($codeH,6)
    $null = WaitFor 'minimized' { [Desktop]::IsIconic($codeH) }
    Click 'Pill'; Click 'Return'
    $null = WaitFor 'restored foreground' { ![Desktop]::IsIconic($codeH) -and [Desktop]::GetForegroundWindow() -eq $codeH }
    Write-Output 'PASS saved edit preserves target, restores minimized VS Code'
    $null = WaitFor 'collapsed after confirmed restore' { Item 'Pill' }
    if ($Cycles -gt 0) {
        $samples = @(); $app.Refresh(); $startMB = $app.WorkingSet64; $startHandles = $app.HandleCount
        for ($i=0; $i -lt $Cycles; $i++) {
            Front $edgeH
            try { $measurement = EditorFrom $edgeH } catch { throw "Cycle $($i+1): $_" }
            $samples += $measurement
            Key $panel @(27)
            $null = WaitFor 'collapsed after Esc' { Item 'Pill' }
        }
        $sorted = @($samples | Sort-Object); $app.Refresh()
        Write-Output ('cycles={0} hotkey_p50_ms={1} p95_ms={2} max_ms={3} working_set_before={4} after={5} handles_before={6} after={7}' -f $Cycles,$sorted[[int][Math]::Ceiling($Cycles*.50)-1],$sorted[[int][Math]::Ceiling($Cycles*.95)-1],$sorted[-1],$startMB,$app.WorkingSet64,$startHandles,$app.HandleCount)
    }
    $app.Refresh(); $cpu = $app.TotalProcessorTime; $timer = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 10
    $app.Refresh()
    $idleWallMs = $timer.ElapsedMilliseconds
    $idleCpuMs = ($app.TotalProcessorTime-$cpu).TotalMilliseconds
    $workingSet = $app.WorkingSet64
    Write-Output ('idle_wall_ms={0} cpu_ms={1} working_set_bytes={2}' -f $idleWallMs,$idleCpuMs,$workingSet)
    $null = WaitFor 'pill after restore' { Item 'Pill' }
    Front $edgeH; EditorFrom $edgeH
    Click 'Rebind'; Click 'Save'
    $null = WaitFor 'saved rebind' { Item 'Pill' }
    Front $codeH; Click 'Pill'; Click 'Return'
    $null = WaitFor 'rebound Edge HWND' { [Desktop]::GetForegroundWindow() -eq $edgeH }
    $null = WaitFor 'pill after rebind' { Item 'Pill' }
    Front $codeH; EditorFrom $codeH
    Click 'Rebind'; Click 'Save'
    $null = WaitFor 'rebound to Code' { Item 'Pill' }
    Write-Output 'PASS explicit rebind to Edge and back; cancelled rebind preserved VS Code'
    KillOwned $editor
    Click 'Pill'; Click 'Return'
    $null = WaitFor 'invalid-target feedback' { (Item 'Status').Current.Name }
    Write-Output 'PASS closed target feedback'
    KillOwned $app
    $app = Launch $appExe '' $true
    $script:panel = WaitFor 'restored window' { $cond = New-Object Windows.Automation.PropertyCondition($ae::ProcessIdProperty,$app.Id); $w = $ae::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$cond); if ($w) { [IntPtr]$w.Current.NativeWindowHandle } }
    Click 'Pill'
    if ((Item 'FullNote').Current.Name -ne 'Updated note' -or (Item 'Return').Current.IsEnabled) { throw 'Restart state incorrect' }
    Click 'Done'
    $null = WaitFor 'cleared note' { Cleared }
    Write-Output 'PASS forced exit, restart text recovery, expired target, explicit completion'
    if ($Ime) {
        Add-Type -AssemblyName System.Windows.Forms
        $zh = [Windows.Forms.InputLanguage]::InstalledInputLanguages | Where-Object { $_.Culture.Name -eq 'zh-CN' } | Select-Object -First 1
        if (!$zh) { throw 'Install/enable a Chinese IME to run -Ime' }
        Front $edgeH; EditorFrom $edgeH
        $thread = [Desktop]::GetWindowThreadProcessId($panel,[IntPtr]::Zero)
        $null = [Desktop]::PostMessage($panel,0x50,[IntPtr]::Zero,$zh.Handle)
        $null = WaitFor 'Chinese input language' { ([Desktop]::GetKeyboardLayout($thread).ToInt64() -band 0xffff) -eq 0x0804 }
        Start-Sleep -Milliseconds 350
        foreach ($k in 78,73,72,65,79,32) { Key $panel @($k); Start-Sleep -Milliseconds 75 }
        $word = (TextValue).Current.Value
        if ($word -notmatch '[\u3400-\u9fff]') { throw "Pinyin composition not confirmed in this profile" }
        foreach ($k in 78,73,72,65,79,13) { Key $panel @($k); Start-Sleep -Milliseconds 75 }
        if (!(Item 'Input').Current.HasKeyboardFocus -or !(Cleared)) { throw 'IME confirmation saved prematurely' }
        Key $panel @(13)
        $null = WaitFor 'save after IME confirmation' { Item 'Pill' }
        Write-Output 'PASS Chinese IME: Pinyin+Space produced Han text, composition Enter kept editor open, next Enter saved'
    }
} finally {
    foreach ($p in $owned) { KillOwned $p }
    if (Test-Path $base) { try { Remove-Item -LiteralPath $base -Recurse -Force } catch { Write-Warning "Disposable profile still in use: $base" } }
}

# Reached only when all requested GUI assertions and process cleanup have completed.
if ($Performance) {
    $result = Get-PerformanceResult @{
        startupInputIdleMs=$startupMs; firstFocusMs=$firstMs; hotSamplesMs=$samples
        idleWallMs=$idleWallMs; idleCpuMs=$idleCpuMs; workingSetBytes=$workingSet; exeBytes=(Get-Item $appExe).Length
    }
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($appExe)
    try { $digest = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
    $result | Add-Member NoteProperty run ([ordered]@{
        utc=[DateTime]::UtcNow.ToString('o'); exe=(Split-Path -Leaf $appExe); sha256=$digest
        os=[Environment]::OSVersion.Version.ToString(); logicalCores=[Environment]::ProcessorCount
        dpi=$measuredDpi; screens=[Windows.Forms.Screen]::AllScreens.Count; shortcutLetter=$Letter; ime=[bool]$Ime
    })
    $dir = New-Item "$PSScriptRoot\..\build\performance" -ItemType Directory -Force
    $report = Join-Path $dir.FullName ('run-' + [guid]::NewGuid().ToString('N') + '.json')
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $report -Encoding UTF8
    Write-Output "Performance report: $report"
    if (!$result.passed) { throw ('Performance budget exceeded: ' + ($result.exceeded -join ', ')) }
    Write-Output 'PASS performance budget v1'
}
