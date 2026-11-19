param([switch]$Test)
$ErrorActionPreference = 'Stop'
$f = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release -lt 533320) { throw 'Requires .NET Framework 4.8.1 x64' }
Push-Location $PSScriptRoot
try {
    $null = New-Item dist -ItemType Directory -Force
    $common = @('/nologo','/optimize+','/warnaserror+','/platform:x64','/codepage:65001','/r:System.Runtime.Serialization.dll','/r:System.Windows.Forms.dll')
    $wpf = 'PresentationCore','PresentationFramework','WindowsBase' | ForEach-Object { '/r:' + (Join-Path $f "WPF\$_.dll") }
    & "$f\csc.exe" @common @wpf /r:System.Xaml.dll /r:System.Drawing.dll /target:winexe /out:dist\FocusMark.exe /win32manifest:FocusMark.App\app.manifest /resource:FocusMark.App\Panel.xaml,Panel.xaml FocusMark.App\App.cs FocusMark.App\NoteStore.cs FocusMark.App\WindowTarget.cs
    if ($LASTEXITCODE) { throw 'App build failed' }
    if ($Test) {
        $null = New-Item build -ItemType Directory -Force
        & "$f\csc.exe" @common /target:exe /out:build\FocusMark.Tests.exe FocusMark.App\NoteStore.cs FocusMark.App\WindowTarget.cs FocusMark.Tests\Tests.cs
        if ($LASTEXITCODE) { throw 'Test build failed' }
        & .\build\FocusMark.Tests.exe
        if ($LASTEXITCODE) { throw 'Tests failed' }
    }
} finally { Pop-Location }
