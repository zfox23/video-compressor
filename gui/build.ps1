# Build the trim/compress GUI (gui\CompressVideo.exe) used by the Explorer "Compress video..." menu entry.
# Uses the C# compiler that ships with Windows' .NET Framework 4, so there is nothing to install.
$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319" }
$csc = Join-Path $fw "csc.exe"
if (-not (Test-Path $csc)) { Write-Error "C# compiler not found ($csc). .NET Framework 4.x is required."; exit 1 }

$exe = "CompressVideo.exe"
$old = "CompressVideo.old.exe"
# A running exe can't be overwritten but can be renamed, so move the current one out of the way
if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
if (Test-Path $exe) { Move-Item $exe $old -Force }

$refs = @(
    "$fw\WPF\PresentationFramework.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\WindowsBase.dll",
    "$fw\System.Xaml.dll", "System.dll", "System.Core.dll"
) | ForEach-Object { "/r:$_" }

& $csc /nologo /target:winexe /optimize+ /platform:anycpu /out:$exe @refs CompressVideoGUI.cs
if ($LASTEXITCODE -ne 0) {
    if ((Test-Path $old) -and -not (Test-Path $exe)) { Move-Item $old $exe }
    exit 1
}
Remove-Item $old -Force -ErrorAction SilentlyContinue
Write-Host "Built $PSScriptRoot\$exe"
