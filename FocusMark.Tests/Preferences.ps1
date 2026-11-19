# Run with Windows PowerShell -STA on a desktop with the app's shortcut available.
param([string]$Exe = "$PSScriptRoot\..\dist\FocusMark.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,WindowsFormsIntegration,System.Windows.Forms
Add-Type -MemberDefinition @'
[DllImport("user32")] public static extern bool RegisterHotKey(IntPtr h,int id,int mods,int key);
[DllImport("user32")] public static extern bool UnregisterHotKey(IntPtr h,int id);
[DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
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
            $options=($items | Where-Object Text -eq '选项').DropDownItems
            $keyMenu=$options | Where-Object Text -eq '唤出快捷键'
            $combo=$keyMenu.DropDownItems[0].ComboBox
            $change=$type.GetMethod('ChangeHotkey',[Reflection.BindingFlags]'NonPublic,Instance')
            if (![Tests.Geometry]::RegisterHotKey([IntPtr]::Zero,124,0x4003,75)) { throw 'K is in use by another app' }
            try {
                if (!$change.Invoke($app,@('K')) -or $tray.Text -notlike '*+J') { throw 'Conflict replaced original binding' }
            } finally { $null=[Tests.Geometry]::UnregisterHotKey([IntPtr]::Zero,124) }
            $lock=[IO.File]::Open("$base\FocusMark\note.json",'Open','Read','Read')
            try {
                if (!$change.Invoke($app,@('K')) -or $tray.Text -notlike '*+J') { throw 'Failed write replaced original binding' }
            } finally { $lock.Dispose() }
            $combo.SelectedIndex=10
            $commit=$combo.GetType().GetMethod('OnSelectionChangeCommitted',[Reflection.BindingFlags]'NonPublic,Instance')
            $null=$commit.Invoke($combo,@([EventArgs]::Empty))
            if ($tray.Text -notlike '*+K' -or ([IO.File]::ReadAllText("$base\FocusMark\note.json") | ConvertFrom-Json).key -ne 'K') { throw 'Menu selection did not persist' }
            if ([Tests.Geometry]::RegisterHotKey([IntPtr]::Zero,124,0x4003,75)) { $null=[Tests.Geometry]::UnregisterHotKey([IntPtr]::Zero,124);throw 'New key is unbound' }
            if (![Tests.Geometry]::RegisterHotKey([IntPtr]::Zero,124,0x4003,74)) {throw 'Old key is still registered'}
            $null=[Tests.Geometry]::UnregisterHotKey([IntPtr]::Zero,124)
            Write-Output 'PASS new shortcut reserved, old released, collision and write failure preserve J' | Out-Host
            $appearance=$options | Where-Object Text -eq '外观'
            $appearance.ShowDropDown(); $appearance.HideDropDown()
            ($appearance.DropDownItems | Where-Object Text -eq '浅色 · 纸白').PerformClick()
            if ($panel.Background.Color.ToString() -ne '#FFF8F7F2' -or $panel.Width -ne 216) {throw 'Light palette or width not applied'}
            $saved=[IO.File]::ReadAllText("$base\FocusMark\note.json") | ConvertFrom-Json
            if ($saved.text -ne 'Working note' -or $saved.theme -ne 'light') {throw 'Theme persistence changed note'}
            [IO.File]::WriteAllText($pack,'{"version":2,"base":"dark","accent":"#DDDDDD","capsuleWidth":248}')
            $load=$type.GetMethod('LoadTheme',[Reflection.BindingFlags]'NonPublic,Instance')
            if (!$load.Invoke($app,@('ink',$true)) -or $panel.Background.Color.ToString() -ne '#FFF8F7F2') {throw 'Invalid pack replaced active theme'}
            Remove-Item -LiteralPath $pack
            $appearance.ShowDropDown(); $appearance.HideDropDown()
            if ($appearance.DropDownItems | Where-Object Text -eq 'ink') {throw 'Removed theme still listed'}
            ($appearance.DropDownItems | Where-Object Text -eq '深色 · 薄荷').PerformClick()
            if ($panel.Background.Color.ToString() -ne '#FF22252B') {throw 'Default palette not restored'}
            Write-Output 'PASS startup custom theme, light selection, persistence, rejected version, unplug and restore dark' | Out-Host
            $options | Where-Object Text -eq '隐藏胶囊文字' | ForEach-Object { $_.PerformClick() }
            $options | Where-Object Text -eq '贴近屏幕底部' | ForEach-Object { $_.PerformClick() }
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
            $options | Where-Object Text -eq '隐藏胶囊文字' | ForEach-Object { $_.PerformClick() }
            if ($panel.FindName('Preview').Text -ne 'Working note') {throw 'Preview did not restore'}
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
