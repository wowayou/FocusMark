# Exercises the real Explorer tray. Holds Ctrl+Alt+J to force the fallback path.
$ErrorActionPreference = 'Stop'
if (Get-Process FocusMark -ErrorAction SilentlyContinue) { throw 'Exit the running FocusMark first' }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -MemberDefinition @'
[DllImport("user32")] public static extern bool RegisterHotKey(IntPtr h,int id,int mods,int key);
[DllImport("user32")] public static extern bool UnregisterHotKey(IntPtr h,int id);
[DllImport("user32")] public static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr w,IntPtr l);
[DllImport("user32")] public static extern bool SetCursorPos(int x,int y);
[DllImport("user32")] public static extern void mouse_event(int f,int x,int y,int d,int e);
[DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
'@ -Name Tray -Namespace Probe
$null = [Probe.Tray]::SetThreadDpiAwarenessContext([IntPtr](-4))
$a = [Windows.Automation.AutomationElement]; $root = $a::RootElement
function By($property,$value){ New-Object Windows.Automation.PropertyCondition($property,$value) }
function Wait($name,[scriptblock]$query){
    for($i=0;$i -lt 25;$i++){ $result = & $query; if($result){return $result}; Start-Sleep -Milliseconds 100 }; throw "Missing $name"
}
function OwnWindow { $root.FindFirst('Children',(By $a::ProcessIdProperty $p.Id)) }
function Child($w,$property,$value){ if ($w) {$w.FindFirst('Descendants',(By $property $value))} }
function IconIn($windowClass){
    $shell=$root.FindFirst('Children',(By $a::ClassNameProperty $windowClass))
    if ($windowClass -eq 'Shell_TrayWnd') { $shell=Child $shell $a::ClassNameProperty 'TrayNotifyWnd' }
    if($shell){ foreach($el in $shell.FindAll('Descendants',[Windows.Automation.Condition]::TrueCondition)){ if($el.Current.Name -like '*FocusMark · 快捷键未启用*' -and !$el.Current.IsOffscreen -and $el.Current.BoundingRectangle.Width -gt 0){ return $el } } }
}
function Mouse($el,$right){ $r=$el.Current.BoundingRectangle; if($el.Current.IsOffscreen -or $r.Width -le 0){throw "Offscreen: $($el.Current.Name)"}; $null=[Probe.Tray]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2)); if($right){$down=8;$up=16}else{$down=2;$up=4}; [Probe.Tray]::mouse_event($down,0,0,0,0); [Probe.Tray]::mouse_event($up,0,0,0,0) }
function TrayMenu($label){
    $icon=IconIn 'Shell_TrayWnd'
    if (!$icon){
        $icon=IconIn 'TopLevelWindowForOverflowXamlIsland'
        if (!$icon){
            $bar=$root.FindFirst('Children',(By $a::ClassNameProperty 'Shell_TrayWnd'))
            # Select only the dedicated overflow button, never another application icon.
            $chevron=Child $bar $a::NameProperty '显示隐藏的图标'
            if (!$chevron){ $chevron=Child $bar $a::NameProperty 'Show hidden icons' }
            if (!$chevron){throw 'Missing tray overflow button'}
            $inv=$null
            if ($chevron.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$inv)) { $inv.Invoke() } else { Mouse $chevron $false }
            Start-Sleep -Milliseconds 350 # Allow Explorer to finish positioning the overflow flyout.
            $icon=Wait 'overflow icon' { IconIn 'TopLevelWindowForOverflowXamlIsland' }
        }
    }
    Mouse $icon $true
    $mi=Wait "FocusMark menu item $label" { foreach($w in $root.FindAll('Children',(By $a::ProcessIdProperty $p.Id))){$v=Child $w $a::NameProperty $label; if($v){return $v}} }
    Mouse $mi $false
}
$base=Join-Path ([IO.Path]::GetTempPath()) ('FocusMark-Tray-'+[guid]::NewGuid().ToString('N'))
$info=New-Object Diagnostics.ProcessStartInfo((Resolve-Path "$PSScriptRoot\..\dist\FocusMark.exe").ProviderPath)
$info.UseShellExecute=$false; $info.EnvironmentVariables['LOCALAPPDATA']=$base
$reserved=[Probe.Tray]::RegisterHotKey([IntPtr]::Zero,1,0x4003,0x4A)
if(!$reserved){throw 'Shortcut already held by another application'}
try {
    $p=[Diagnostics.Process]::Start($info)
    $win=Wait 'conflict dialog' { OwnWindow }; $ok=Wait 'dialog OK' { Child $win $a::AutomationIdProperty '2' }
    $msg=$win.FindAll('Descendants',[Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like 'Ctrl+Alt+J*' }
    if (!$msg){throw 'Missing conflict explanation'}
    $null=[Probe.Tray]::SendMessage([IntPtr]$ok.Current.NativeWindowHandle,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
    $null=Wait 'dismissed conflict dialog' { !(OwnWindow) }
    Write-Output 'PASS hotkey conflict feedback'
    TrayMenu '打开'; $null=Wait 'fallback editor' { Child (OwnWindow) $a::AutomationIdProperty 'Input' }
    TrayMenu '隐藏'; $null=Wait 'hidden panel' { !(OwnWindow) }
    TrayMenu '选项'
    $prefs=Wait 'visual settings window' { $w=OwnWindow; if($w -and $w.Current.Name -eq 'FocusMark 设置'){ $w } }
    Mouse (Child $prefs $a::AutomationIdProperty 'ShowEdge') $false
    $selected=(Child $prefs $a::AutomationIdProperty 'ShowEdge').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
    if (!$selected.Current.IsSelected) {throw 'Edge choice not selected'}
    Mouse (Child $prefs $a::AutomationIdProperty 'Cancel') $false
    $null=Wait 'settings closed' { !(OwnWindow) }
    Write-Output 'PASS visual settings from Explorer tray, click edge and cancel'
    TrayMenu '退出'; if(!$p.WaitForExit(3000)){throw 'Tray exit failed'}
    Write-Output 'PASS Explorer tray open, hide and clean exit'
} finally {
    $null=[Probe.Tray]::UnregisterHotKey([IntPtr]::Zero,1)
    if($p -and !$p.HasExited){$p.Kill()}
    if(Test-Path $base){Remove-Item -LiteralPath $base -Recurse -Force}
}
