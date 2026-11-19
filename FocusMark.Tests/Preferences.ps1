# Run with Windows PowerShell -STA on a desktop with the app's shortcut available.
param([string]$Exe = "$PSScriptRoot\..\dist\FocusMark.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,WindowsFormsIntegration,System.Windows.Forms
Add-Type -MemberDefinition @'
[DllImport("user32")] public static extern bool RegisterHotKey(IntPtr h,int id,int mods,int key);
[DllImport("user32")] public static extern bool UnregisterHotKey(IntPtr h,int id);
[DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
[DllImport("gdi32")] public static extern IntPtr CreateRectRgn(int l,int t,int r,int b);
[DllImport("user32")] public static extern int GetWindowRgn(IntPtr h,IntPtr r);
[DllImport("gdi32")] public static extern bool PtInRegion(IntPtr r,int x,int y);
[DllImport("gdi32")] public static extern bool DeleteObject(IntPtr r);
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
    [IO.File]::WriteAllText("$base\FocusMark\note.json", '{"text":"Working note","theme":"ink"}')
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
            $w.FindName('ShowPill').IsChecked=$true
            Write-Output 'PASS theme width and edge accent track rendered capsule' | Out-Host
            $w.FindName('Letter').SelectedIndex=10
            if (![Tests.Geometry]::RegisterHotKey([IntPtr]::Zero,124,0x4003,75)) { throw 'K is in use by another app' }
            try { ClickControl $w 'Apply'; if (!$w.FindName('Error').IsVisible -or $tray.Text -notlike '*+J') {throw 'Conflict replaced original binding'} }
            finally { $null=[Tests.Geometry]::UnregisterHotKey([IntPtr]::Zero,124) }
            $lock=[IO.File]::Open("$base\FocusMark\note.json",'Open','Read','Read')
            try { ClickControl $w 'Apply'; if (!$w.FindName('Error').IsVisible -or $tray.Text -notlike '*+J') {throw 'Failed write replaced original binding'} }
            finally { $lock.Dispose() }
            $w.FindName('Palette').SelectedIndex=1; ClickControl $w 'Apply'
            if ($tray.Text -notlike '*+K' -or ([IO.File]::ReadAllText("$base\FocusMark\note.json") | ConvertFrom-Json).key -ne 'K') {throw 'Key not saved'}
            if ([Tests.Geometry]::RegisterHotKey([IntPtr]::Zero,124,0x4003,75)) { $null=[Tests.Geometry]::UnregisterHotKey([IntPtr]::Zero,124);throw 'New key is unbound' }
            if (![Tests.Geometry]::RegisterHotKey([IntPtr]::Zero,124,0x4003,74)) {throw 'Old key is still registered'}
            $null=[Tests.Geometry]::UnregisterHotKey([IntPtr]::Zero,124)
            Write-Output 'PASS new shortcut reserved, old released, collision and write failure preserve J' | Out-Host
            if ($panel.Background.Color.ToString() -ne '#FFF8F7F2' -or $panel.Width -ne 216) {throw 'Light palette or width not applied'}
            $saved=[IO.File]::ReadAllText("$base\FocusMark\note.json") | ConvertFrom-Json
            if ($saved.text -ne 'Working note' -or $saved.theme -ne 'light') {throw 'Theme persistence changed note'}
            [IO.File]::WriteAllText($pack,'{"version":2,"base":"dark","accent":"#DDDDDD","capsuleWidth":248}')
            $w=Settings; $w.FindName('Palette').SelectedIndex=2; ClickControl $w 'Apply'
            if (!$w.FindName('Error').IsVisible -or $panel.Background.Color.ToString() -ne '#FFF8F7F2') {throw 'Invalid pack replaced active theme'}
            ClickControl $w 'Cancel'; Remove-Item -LiteralPath $pack
            $w=Settings; if ($w.FindName('Palette').Items.Count -ne 2) {throw 'Removed pack still listed'}
            $w.FindName('Palette').SelectedIndex=0; $w.FindName('Mask').IsChecked=$true; $w.FindName('Position').SelectedIndex=1; ClickControl $w 'Apply'
            if ($panel.Background.Color.ToString() -ne '#FF22252B') {throw 'Dark theme not restored'}
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
            $region=[Tests.Geometry]::CreateRectRgn(0,0,0,0)
            try {
                if (![Tests.Geometry]::GetWindowRgn($h,$region) -or [Tests.Geometry]::PtInRegion($region,0,0) -or ![Tests.Geometry]::PtInRegion($region,32,9)) {throw 'Edge window hit region incorrect'}
            } finally { $null=[Tests.Geometry]::DeleteObject($region) }
            ClickControl $panel 'Pill'; if (!$panel.FindName('FullNote').IsVisible) {throw 'Edge did not open'}
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
