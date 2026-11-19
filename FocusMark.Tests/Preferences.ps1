# Run with Windows PowerShell -STA on a desktop with the app's shortcut available.
param([string]$Exe = "$PSScriptRoot\..\dist\FocusMark.exe", [ValidateSet('J','L')][string]$InitialLetter = 'J')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,WindowsFormsIntegration,System.Windows.Forms
Add-Type -MemberDefinition @'
[DllImport("user32")] public static extern bool RegisterHotKey(IntPtr h,int id,int mods,int key);
[DllImport("user32")] public static extern bool UnregisterHotKey(IntPtr h,int id);
[DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
[DllImport("user32")] public static extern bool SetCursorPos(int x,int y);
[DllImport("user32")] public static extern void mouse_event(int flags,int x,int y,int data,int extra);
[DllImport("user32")] public static extern IntPtr WindowFromPoint(long point);
[DllImport("user32")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32")] public static extern void keybd_event(byte key,byte scan,int flags,int extra);
[DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
[StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
'@ -Name Geometry -Namespace Tests
$null=[Tests.Geometry]::SetThreadDpiAwarenessContext([IntPtr](-4))
$base=Join-Path ([IO.Path]::GetTempPath()) ('FocusMark-Prefs-'+[guid]::NewGuid().ToString('N'))
$oldLocal=$env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA=$base
    $null=New-Item "$base\FocusMark\themes" -ItemType Directory -Force
    $pack=Join-Path $base 'FocusMark\themes\ink.json'
    [IO.File]::WriteAllText($pack, '{"version":1,"base":"dark","accent":"#77BBFF","capsuleWidth":248}')
    [IO.File]::WriteAllText("$base\FocusMark\note.json", (@{text='Working note';theme='ink';key=$InitialLetter} | ConvertTo-Json -Compress))
    # Explicitly load the build under test, including a WSL share path.
    $assembly=[Reflection.Assembly]::UnsafeLoadFrom((Resolve-Path $Exe).ProviderPath)
    $type=$assembly.GetType('FocusMark.App',$true)
    $app=[Activator]::CreateInstance($type,$true)
    $app.ShutdownMode='OnExplicitShutdown'
    $failure=$null
    $null=$app.Dispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::ApplicationIdle, [Action]{
        try {
            $panel=$app.Windows[0]
            $tray=$type.GetField('tray','NonPublic,Instance').GetValue($app)
            $items=$tray.ContextMenuStrip.Items
            if ($panel.Width -ne 248 -or $panel.Resources['Mint'].Color.ToString() -ne '#FF77BBFF') {throw 'Saved custom theme did not load'}
            function Settings { ($items | Where-Object Text -eq '选项').PerformClick(); $app.Windows | Where-Object Title -eq 'FocusMark 设置' }
            function ClickControl($window,$name) { $window.FindName($name).RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Button]::ClickEvent))) }
            $w=Settings
            $sample=$w.FindName('Sample'); $palette=$w.FindName('Palette')
            if ($sample.Width -ne $panel.Width -or $sample.LayoutTransform.ScaleX -ne 0.8) {throw 'Custom capsule preview size differs from scaled pill'}
            $palette.SelectedIndex=0
            if ($sample.Width -ne 216) {throw 'Default width did not update during preview'}
            $palette.SelectedIndex=2
            $w.FindName('ShowEdge').IsChecked=$true
            if ($sample.Width -ne 64 -or !$w.FindName('SampleGrip').IsVisible -or $w.FindName('SampleGrip').Background.Color -ne $panel.FindName('Grip').Background.Color) {throw 'Edge preview diverges from actual grip'}
            ClickControl $w 'TrySample'
            if (!$w.FindName('SampleNote').IsVisible -or $panel.Width -ne 248) {throw 'Preview did not expand independently'}
            ClickControl $w 'TrySample'
            if ($w.FindName('SampleNote').IsVisible -or !$w.FindName('SampleGrip').IsVisible) {throw 'Could not fold preview'}
            $w.FindName('ShowNone').IsChecked=$true
            if ($w.FindName('TrySample').IsVisible -or !$w.FindName('HiddenHint').IsVisible) {throw 'Hidden preview inaccurate'}
            $w.FindName('ShowPill').IsChecked=$true
            Write-Output 'PASS interactive preview, fold and hidden recovery hint' | Out-Host
            function Pump {
                $frame=New-Object Windows.Threading.DispatcherFrame; $t=New-Object Windows.Threading.DispatcherTimer
                $t.Interval=[TimeSpan]::FromMilliseconds(100); $t.Add_Tick({$t.Stop();$frame.Continue=$false}); $t.Start(); [Windows.Threading.Dispatcher]::PushFrame($frame)
            }
            $null=$w.Activate(); $null=$w.FindName('Letter').Focus(); ClickControl $w 'Letter'; Pump
            $wh=(New-Object Windows.Interop.WindowInteropHelper($w)).Handle
            if ([Tests.Geometry]::GetForegroundWindow() -ne $wh) {throw 'Keyboard target changed'}
            foreach ($vk in 17,16,75){[Tests.Geometry]::keybd_event($vk,0,0,0)}
            foreach ($vk in 75,16,17){[Tests.Geometry]::keybd_event($vk,0,2,0)}
            Pump
            if ($w.FindName('Letter').Content -ne 'Ctrl+Shift+K') {throw 'Custom chord recording failed'}
            if (![Tests.Geometry]::RegisterHotKey([IntPtr]::Zero,124,0x4006,75)) { throw 'K is in use by another app' }
            try { ClickControl $w 'Apply'; if (!$w.FindName('Error').IsVisible -or $tray.Text -notlike "*+$InitialLetter") {throw 'Conflict replaced original binding'} }
            finally { $null=[Tests.Geometry]::UnregisterHotKey([IntPtr]::Zero,124) }
            $lock=[IO.File]::Open("$base\FocusMark\note.json",'Open','Read','Read')
            try { ClickControl $w 'Apply'; if (!$w.FindName('Error').IsVisible -or $tray.Text -notlike "*+$InitialLetter") {throw 'Failed write replaced original binding'} }
            finally { $lock.Dispose() }
            $w.FindName('Palette').SelectedIndex=1; ClickControl $w 'Apply'
            if ($tray.Text -notlike '*+K' -or ([IO.File]::ReadAllText("$base\FocusMark\note.json") | ConvertFrom-Json).key -ne 'K') {throw 'Key not saved'}
            if ([Tests.Geometry]::RegisterHotKey([IntPtr]::Zero,124,0x4006,75)) { $null=[Tests.Geometry]::UnregisterHotKey([IntPtr]::Zero,124);throw 'New key is unbound' }
            if (![Tests.Geometry]::RegisterHotKey([IntPtr]::Zero,124,0x4003,([int][char]$InitialLetter))) {throw 'Old key is still registered'}
            $null=[Tests.Geometry]::UnregisterHotKey([IntPtr]::Zero,124)
            Write-Output "PASS new shortcut reserved, old released, collision and write failure preserve $InitialLetter" | Out-Host
            if ($panel.Resources['Panel'].Color.ToString() -ne '#FFF8F7F2' -or $panel.Width -ne 216) {throw 'Light palette or width not applied'}
            $saved=[IO.File]::ReadAllText("$base\FocusMark\note.json") | ConvertFrom-Json
            if ($saved.text -ne 'Working note' -or $saved.theme -ne 'light') {throw 'Theme persistence changed note'}
            [IO.File]::WriteAllText($pack,'{"version":2,"base":"dark","accent":"#DDDDDD","capsuleWidth":248}')
            $w=Settings; $w.FindName('Palette').SelectedIndex=2; ClickControl $w 'Apply'
            if (!$w.FindName('Error').IsVisible -or $panel.Resources['Panel'].Color.ToString() -ne '#FFF8F7F2') {throw 'Invalid pack replaced active theme'}
            ClickControl $w 'Cancel'; Remove-Item -LiteralPath $pack
            $w=Settings; if ($w.FindName('Palette').Items.Count -ne 2) {throw 'Removed pack still listed'}
            $w.FindName('Palette').SelectedIndex=0; $w.FindName('Mask').IsChecked=$true; $w.FindName('Position').SelectedIndex=1; ClickControl $w 'Apply'
            if ($panel.Resources['Panel'].Color.ToString() -ne '#FF22252B') {throw 'Dark theme not restored'}
            $panel.UpdateLayout()
            $h=(New-Object Windows.Interop.WindowInteropHelper($panel)).Handle
            $work=[Windows.Forms.Screen]::FromHandle($h).WorkingArea
            $rect=New-Object Tests.Geometry+Rect
            $null=[Tests.Geometry]::GetWindowRect($h,[ref]$rect)
            if ($panel.FindName('Preview').Text -ne '查看备注' -or $rect.Bottom -ne $work.Bottom-8) {throw 'Masked preview or bottom placement failed'}
            $click=New-Object Windows.RoutedEventArgs([Windows.Controls.Button]::ClickEvent)
            $panel.FindName('Pill').RaiseEvent($click)
            if (!$panel.FindName('FullNote').IsVisible -or $panel.FindName('FullNote').Text -ne 'Working note') {throw 'Expanded text missing'}
            $panel.FindName('Fold').RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Button]::ClickEvent)))
            if (!$panel.FindName('Pill').IsVisible -or $panel.FindName('Preview').Text -ne '查看备注') {throw 'Fold failed'}
            $w=Settings; $w.FindName('Mask').IsChecked=$false; ClickControl $w 'Apply'
            if ($panel.FindName('Preview').Text -ne 'Working note') {throw 'Preview did not restore'}
            $w=Settings; $w.FindName('ShowEdge').IsChecked=$true; ClickControl $w 'Cancel'
            if ($panel.Width -ne 216) {throw 'Cancelled preview changed capsule'}
            $w=Settings; $w.FindName('ShowEdge').IsChecked=$true; ClickControl $w 'Apply'
            $panel.UpdateLayout(); $null=[Tests.Geometry]::GetWindowRect($h,[ref]$rect)
            if (!$panel.FindName('Grip').IsVisible -or $rect.Bottom -ne $work.Bottom -or $panel.Width -ne 64) {throw 'Edge geometry failed'}
            function Hit($x,$y) { [Tests.Geometry]::WindowFromPoint(([long]$y -shl 32) -bor ([long]$x -band 0xffffffffL)) }
            Pump
            if ((Hit $rect.Left $rect.Top) -eq $h -or (Hit ($rect.Left+32) ($rect.Top+9)) -ne $h) {throw 'Transparent corner or center hit test failed'}
            foreach ($dock in 0,1,2,3) {
                $w=Settings; $w.FindName('Position').SelectedIndex=$dock; $w.FindName('Along').Value=25; ClickControl $w 'Apply'; Pump
                $null=[Tests.Geometry]::GetWindowRect($h,[ref]$rect)
                if ($dock -lt 2) {
                    $expected=$work.Left+($work.Width-($rect.Right-$rect.Left))*.25
                    if ([Math]::Abs($rect.Left-$expected) -gt 1 -or ($dock -eq 0 -and $rect.Top -ne $work.Top) -or ($dock -eq 1 -and $rect.Bottom -ne $work.Bottom)){throw 'Horizontal dock failed'}
                } else {
                    $expected=$work.Top+($work.Height-($rect.Bottom-$rect.Top))*.25
                    if ([Math]::Abs($rect.Top-$expected) -gt 1 -or ($dock -eq 2 -and $rect.Left -ne $work.Left) -or ($dock -eq 3 -and $rect.Right -ne $work.Right) -or [Math]::Abs($panel.Width-18) -gt 0.5){throw 'Vertical dock failed'}
                }
            }
            Write-Output 'PASS four edges and 25% displacement, transparent corner and opaque center' | Out-Host
            $w=Settings; $w.FindName('Scale').Value=125; $w.FindName('Radius').Value=2; ClickControl $w 'Apply'; Pump
            if ($panel.FindName('Shell').CornerRadius.TopLeft -ne 2.5) {throw 'Scaled radius failed'}
            $null=[Tests.Geometry]::GetWindowRect($h,[ref]$rect)
            $x=[int](($rect.Left+$rect.Right)/2); $y=[int](($rect.Top+$rect.Bottom)/2)
            if ((Hit $x $y) -ne $h) {throw 'Drag target obstructed'}
            $null=[Tests.Geometry]::SetCursorPos($x,$y); Pump
            [Tests.Geometry]::mouse_event(2,0,0,0,0)
            try {
                Pump; $null=[Tests.Geometry]::SetCursorPos($x-30,$y); Pump
                $null=[Tests.Geometry]::SetCursorPos([int]($work.Left+$work.Width*.7),$work.Top+40); Pump
            } finally { [Tests.Geometry]::mouse_event(4,0,0,0,0) }; Pump
            $null=[Tests.Geometry]::GetWindowRect($h,[ref]$rect)
            $state=[IO.File]::ReadAllText("$base\FocusMark\note.json") | ConvertFrom-Json
            if (!$panel.FindName('Grip').IsVisible -or $rect.Top -ne $work.Top -or $rect.Right-$rect.Left -le 80 -or $state.offset -lt 18 -or $state.offset -gt 24 -or $state.side -eq 'right' -or $state.scale -ne 125 -or $state.radius -ne 2) {throw "Drag, snap or atomic preferences failed: $($rect.Top) offset=$($state.offset)"}
            $x=[int](($rect.Left+$rect.Right)/2); $y=[int](($rect.Top+$rect.Bottom)/2)
            $null=[Tests.Geometry]::SetCursorPos($x,$y); Pump
            if ((Hit $x $y) -ne $h) {throw 'Click target obstructed'}
            [Tests.Geometry]::mouse_event(2,0,0,0,0); [Tests.Geometry]::mouse_event(4,0,0,0,0); Pump
            if (!$panel.FindName('FullNote').IsVisible) {throw 'Click after dragging did not open note'}
            Write-Output 'PASS physical drag right to top, persisted shape, no accidental expansion, click to open' | Out-Host
            ClickControl $panel 'Fold'
            $w=Settings; $w.FindName('ShowNone').IsChecked=$true; ClickControl $w 'Apply'
            if ($panel.IsVisible) {throw 'Hidden mode still visible'}
            ($items | Where-Object Text -eq '打开').PerformClick()
            if ($panel.FindName('Input').Text -ne 'Working note') {throw 'Hidden note could not be retrieved'}
            ClickControl $panel 'Save'; if ($panel.IsVisible) {throw 'Saved hidden note still visible'}
            Write-Output 'PASS draft preview cancel, edge bounds, expand, hidden save and recovery' | Out-Host
            $items | Where-Object Text -eq '打开' | ForEach-Object { $_.PerformClick() }
            $entry=$panel.FindName('Input'); $entry.Text='草稿'; $entry.SelectAll()
            foreach ($case in @(@(('字'*201),$true),@(('🙂'*200),$false),@(('x'*100000),$true))) {
                $data=New-Object Windows.DataObject([Windows.DataFormats]::UnicodeText,$case[0])
                $paste=New-Object Windows.DataObjectPastingEventArgs($data,$false,[Windows.DataFormats]::UnicodeText)
                $entry.RaiseEvent($paste)
                if ($paste.CommandCancelled -ne $case[1] -or $entry.Text -ne '草稿') { throw 'Paste boundary or selection handling failed' }
            }
            $entry.Text='字'*201
            if ($panel.FindName('Save').IsEnabled -or $panel.FindName('Count').Text -ne '请再精简 1 字') {throw 'Overflow feedback failed'}
            $items | Where-Object Text -eq '隐藏' | ForEach-Object { $_.PerformClick() }
            if ($entry.Text.Length -ne 0 -or $entry.CanUndo) { throw 'Hidden editor retained draft or undo history' }
            Write-Output 'PASS paste boundaries, overflow feedback and discarded draft cleanup' | Out-Host
            Write-Output 'PASS menu callbacks, bottom window bounds, masked and restored preview, expand and fold' | Out-Host
        } catch { $script:failure=$_ } finally { $app.Shutdown() }
    })
    $null=$app.Run()
    if ($failure) {throw $failure}
} finally {
    $env:LOCALAPPDATA=$oldLocal
    if(Test-Path $base){Remove-Item -LiteralPath $base -Recurse -Force}
}
