# Adds "Compress video..." to the Explorer right-click menu for video files (the Windows counterpart of the
# macOS Quick Action). Per-user, no admin needed. On Windows 11 it is under "Show more options" (or Shift+right-click).
#   gui\install.ps1              build the GUI and add the menu entry
#   gui\install.ps1 -Uninstall   remove the menu entry
param([switch]$Uninstall)

$extensions = ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm", ".wmv", ".mts", ".m2ts", ".3gp", ".hevc"
$verb = "CompressVideo"

foreach ($ext in $extensions) {
    $key = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\$verb"
    if (Test-Path $key) { Remove-Item $key -Recurse -Force }
}
if ($Uninstall) {
    Write-Host "Removed the 'Compress video...' menu entry."
    exit 0
}

& (Join-Path $PSScriptRoot "build.ps1")
if ($LASTEXITCODE -ne 0) { exit 1 }
$exe = Join-Path $PSScriptRoot "CompressVideo.exe"

foreach ($ext in $extensions) {
    $key = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\$verb"
    New-Item -Path "$key\command" -Force | Out-Null
    Set-ItemProperty -Path $key -Name "(default)" -Value "Compress video..."
    Set-ItemProperty -Path $key -Name "Icon" -Value "`"$exe`""
    Set-ItemProperty -Path "$key\command" -Name "(default)" -Value "`"$exe`" `"%1`""
}
Write-Host "Added 'Compress video...' to the right-click menu for: $($extensions -join ' ')"

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    Write-Host "ffmpeg was not found on PATH. Install it with: winget install Gyan.FFmpeg" -ForegroundColor Yellow
}
