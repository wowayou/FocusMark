# Run with Windows PowerShell -STA on a desktop with the app's shortcut available.
param([string]$Exe = "$PSScriptRoot\..\dist\FocusMark.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,WindowsFormsIntegration,System.Windows.Forms
Add-Type -MemberDefinition @'
[DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
[DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
[StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
'@ -Name Geometry -Namespace Tests
$null=[Tests.Geometry]::SetThreadDpiAwarenessContext([IntPtr](-4))
$base=Join-Path ([IO.Path]::GetTempPath()) ('FocusMark-Prefs-'+[guid]::NewGuid().ToString('N'))
$oldLocal=$env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA=$base
    $null=New-Item "$base\FocusMark" -ItemType Directory -Force
    [IO.File]::WriteAllText("$base\FocusMark\note.json", '{"text":"Working note"}')
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
            $items | Where-Object Text -eq '隐藏胶囊文字' | ForEach-Object { $_.PerformClick() }
            $items | Where-Object Text -eq '贴近屏幕底部' | ForEach-Object { $_.PerformClick() }
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
            $items | Where-Object Text -eq '隐藏胶囊文字' | ForEach-Object { $_.PerformClick() }
            if ($panel.FindName('Preview').Text -ne 'Working note') {throw 'Preview did not restore'}
            Write-Output 'PASS menu callbacks, bottom window bounds, masked and restored preview, expand and fold' | Out-Host
        } catch { $script:failure=$_ } finally { $app.Shutdown() }
    })
    $null=$app.Run()
    if ($failure) {throw $failure}
} finally {
    $env:LOCALAPPDATA=$oldLocal
    if(Test-Path $base){Remove-Item -LiteralPath $base -Recurse -Force}
}
