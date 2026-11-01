#!/usr/bin/env pwsh
# Video compression script for iPhone 4K Dolby Vision to 1080p60
# Target: 100MB max filesize (configurable)
# PowerShell version for Windows

param(
    [Parameter(Mandatory=$true)]
    [string]$InputFile,

    [int]$TargetSizeMB = 100,

    [int]$OutputWidth = 1920,

    [int]$OutputHeight = 1080,

    [ValidateSet(0, 90, 180, 270)]
    [int]$Rotation = 0,

    [switch]$ToneMapToSDR,

    [string]$StartTime = "",

    [string]$EndTime = "",

    [string]$OutputFile = ""
)

# Convert [[HH:]MM:]SS[.ms] or plain seconds to seconds ($null if invalid)
function ConvertTo-Seconds([string]$text) {
    if ($text -notmatch '^\d+(\.\d+)?(:\d+(\.\d+)?){0,2}$') { return $null }
    $total = 0.0
    foreach ($part in $text.Split(':')) {
        $total = $total * 60 + [double]::Parse($part, [Globalization.CultureInfo]::InvariantCulture)
    }
    return $total
}

# Show usage if help is requested
if ($InputFile -eq "-h" -or $InputFile -eq "--help" -or $InputFile -eq "/?") {
    Write-Host "Usage: .\compress-video-to-100mb-ffmpeg.ps1 -InputFile <path> [-TargetSizeMB <mb>] [-OutputWidth <w>] [-OutputHeight <h>] [-Rotation <degrees>] [-StartTime <time>] [-ToneMapToSDR] [-EndTime <time>] [-OutputFile <path>]"
    Write-Host ""
    Write-Host "Options:"
    Write-Host "  -InputFile       Input video file (required)"
    Write-Host "  -TargetSizeMB    Target size in MB (default: 100)"
    Write-Host "  -OutputWidth     Output width (default: 1920)"
    Write-Host "  -OutputHeight    Output height (default: 1080)"
    Write-Host "  -Rotation        Clockwise rotation in degrees: 0, 90, 180, or 270 (default: 0)"
    Write-Host "  -StartTime       Start time: seconds or [HH:]MM:SS[.ms] (default: start of video)"
    Write-Host "  -EndTime         End time: seconds or [HH:]MM:SS[.ms] (default: end of video)"
    Write-Host "  -ToneMapToSDR    Tone-map HDR input to SDR (needs ffmpeg with zscale/libzimg; default keeps HDR)"
    Write-Host "  -OutputFile      Output file path (default: <input>_compressed.mp4)"
    Write-Host ""
    Write-Host "Examples:"
    Write-Host "  .\compress-video-to-100mb-ffmpeg.ps1 -InputFile video.mov"
    Write-Host "  .\compress-video-to-100mb-ffmpeg.ps1 -InputFile video.mov -TargetSizeMB 50"
    Write-Host "  .\compress-video-to-100mb-ffmpeg.ps1 -InputFile video.mov -TargetSizeMB 100 -OutputWidth 1280 -OutputHeight 720"
    Write-Host "  .\compress-video-to-100mb-ffmpeg.ps1 -InputFile video.mov -StartTime 0:30 -EndTime 1:45"
    Write-Host "  .\compress-video-to-100mb-ffmpeg.ps1 -InputFile video.mov -Rotation 90 -OutputFile rotated.mp4"
    exit 0
}

# Check if input file exists
if (-not (Test-Path $InputFile)) {
    Write-Host "Error: Input file '$InputFile' not found" -ForegroundColor Red
    exit 1
}

# Generate output filename
$inputFileInfo = Get-Item $InputFile
if (-not $OutputFile) {
    $outputFile = Join-Path $inputFileInfo.DirectoryName ($inputFileInfo.BaseName + "_compressed.mp4")
}

# Check if ffmpeg and ffprobe are available
try {
    $null = Get-Command ffmpeg -ErrorAction Stop
    $null = Get-Command ffprobe -ErrorAction Stop
} catch {
    Write-Host "Error: ffmpeg is not installed or not in PATH" -ForegroundColor Red
    Write-Host "Please install ffmpeg from https://ffmpeg.org/download.html" -ForegroundColor Yellow
    exit 1
}

# Get video duration in seconds
Write-Host "Analyzing video..." -ForegroundColor Cyan
$durationOutput = & ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "$InputFile" 2>&1
$duration = [double]$durationOutput

if ($duration -eq 0) {
    Write-Host "Error: Could not determine video duration" -ForegroundColor Red
    exit 1
}

# Resolve clip start/end (seconds) and the length of the portion being encoded
$startSec = 0.0
$endSec = $duration
if ($StartTime) {
    $startSec = ConvertTo-Seconds $StartTime
    if ($null -eq $startSec) {
        Write-Host "Error: Invalid start time '$StartTime'" -ForegroundColor Red
        exit 1
    }
}
if ($EndTime) {
    $endSec = ConvertTo-Seconds $EndTime
    if ($null -eq $endSec) {
        Write-Host "Error: Invalid end time '$EndTime'" -ForegroundColor Red
        exit 1
    }
    # Clamp to the end of the video
    if ($endSec -gt $duration) { $endSec = $duration }
}
if ($startSec -ge $endSec) {
    Write-Host "Error: Start time must be before end time (and within the video)" -ForegroundColor Red
    exit 1
}
$duration = $endSec - $startSec

# Calculate target bitrate with safety margin
# Use 95% of target size to ensure we stay under the limit
$safeTargetSizeMB = $TargetSizeMB * 0.95
$targetSizeBits = $safeTargetSizeMB * 8 * 1024 * 1024
$audioBitrate = 128000

# Calculate video bitrate: (total_bits / duration) - audio_bitrate
$targetVideoBitrate = [math]::Floor(($targetSizeBits / $duration) - $audioBitrate)

# Ensure bitrate is reasonable (minimum 500kbps)
if ($targetVideoBitrate -lt 500000) {
    Write-Host "Warning: Calculated bitrate is very low. Video may be too long for ${TargetSizeMB}MB target." -ForegroundColor Yellow
    $targetVideoBitrate = 500000
}

# Detect HDR (HLG or PQ). HDR input is kept as HDR: 10-bit HEVC with the colour tags preserved.
# SDR input uses 8-bit H.264 for maximum compatibility.
function Get-StreamField([string]$field) {
    $value = & ffprobe -v error -select_streams v:0 -show_entries "stream=$field" -of "default=noprint_wrappers=1:nokey=1" "$InputFile" 2>$null | Select-Object -First 1
    return "$value".Trim()
}
$tonemapFilter = ""
$colorTrc = Get-StreamField "color_transfer"
$colorPrimaries = Get-StreamField "color_primaries"
$colorSpace = Get-StreamField "color_space"
if (-not $colorPrimaries -or $colorPrimaries -eq "unknown") { $colorPrimaries = "bt2020" }
if (-not $colorSpace -or $colorSpace -eq "unknown") { $colorSpace = "bt2020nc" }
if ($colorTrc -eq "arib-std-b67" -or $colorTrc -eq "smpte2084") {
    $hdrDesc = if ($colorTrc -eq "arib-std-b67") { "HDR (HLG)" } else { "HDR (PQ/HDR10)" }
    if ($ToneMapToSDR) {
        # Tone-map HDR to SDR BT.709 (needs the zscale filter from an ffmpeg build with libzimg)
        $filterList = & ffmpeg -hide_banner -filters 2>$null | Out-String
        if ($filterList -notmatch " zscale ") {
            Write-Host "Error: HDR to SDR tone-mapping needs an ffmpeg build with the zscale filter (libzimg)." -ForegroundColor Red
            Write-Host "Use the 'full' build from https://www.gyan.dev/ffmpeg/builds/ (winget install Gyan.FFmpeg)" -ForegroundColor Yellow
            exit 1
        }
        $encodeDesc = "H.264 8-bit SDR, tone-mapped from $hdrDesc"
        $videoArgs = @("-c:v", "libx264", "-preset", "slow", "-pix_fmt", "yuv420p", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709")
        $tonemapFilter = "zscale=tin=${colorTrc}:pin=${colorPrimaries}:min=${colorSpace}:t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p"
    } else {
        $encodeDesc = "HEVC 10-bit, $hdrDesc preserved"
        $videoArgs = @(
            "-c:v", "libx265", "-preset", "medium", "-tag:v", "hvc1", "-pix_fmt", "yuv420p10le",
            "-color_primaries", $colorPrimaries, "-color_trc", $colorTrc, "-colorspace", $colorSpace
        )
    }
} else {
    $encodeDesc = "H.264 8-bit (SDR)"
    $videoArgs = @("-c:v", "libx264", "-preset", "slow", "-pix_fmt", "yuv420p")
}

# Display compression parameters
Write-Host ""
Write-Host "Input file: $InputFile" -ForegroundColor Green
if ($StartTime -or $EndTime) {
    Write-Host "Clip: $([math]::Round($startSec, 2))s to $([math]::Round($endSec, 2))s"
}
Write-Host "Duration: $([math]::Round($duration, 2))s"
Write-Host "Target size: ${TargetSizeMB}MB (using $([math]::Round($safeTargetSizeMB, 2))MB with safety margin)"
Write-Host "Output resolution: ${OutputWidth}x${OutputHeight}"
Write-Host "Rotation: ${Rotation}° clockwise"
Write-Host "Encoding: $encodeDesc"
Write-Host "Target video bitrate: $([math]::Floor($targetVideoBitrate / 1000))kbps"
Write-Host "Output file: $outputFile" -ForegroundColor Green
Write-Host ""
Write-Host "Starting compression..." -ForegroundColor Cyan

# Build filter chain
$filter = "scale=${OutputWidth}:${OutputHeight}:flags=lanczos,fps=60"
if ($tonemapFilter) { $filter = "$filter,$tonemapFilter" }

# Add rotation if specified
if ($Rotation -eq 90) {
    $filter = "transpose=1,$filter"
} elseif ($Rotation -eq 180) {
    $filter = "transpose=1,transpose=1,$filter"
} elseif ($Rotation -eq 270) {
    $filter = "transpose=2,$filter"
}

# Compress video
$startArg = $startSec.ToString([Globalization.CultureInfo]::InvariantCulture)
$durationArg = $duration.ToString([Globalization.CultureInfo]::InvariantCulture)

$ffmpegArgs = @(
    "-ss", $startArg,
    "-i", "`"$InputFile`"",
    "-t", $durationArg,
    "-vf", "`"$filter`"",
    $videoArgs,
    "-b:v", "$targetVideoBitrate",
    "-maxrate", "$targetVideoBitrate",
    "-bufsize", "$($targetVideoBitrate * 2)",
    "-c:a", "aac",
    "-b:a", "128k",
    "-ac", "2",
    "-movflags", "+faststart",
    "`"$outputFile`""
)

$ffmpegCommand = "ffmpeg " + ($ffmpegArgs -join " ")

# Execute ffmpeg using cmd to handle the command properly
$process = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", $ffmpegCommand -NoNewWindow -Wait -PassThru

if ($process.ExitCode -eq 0) {
    $outputSize = (Get-Item $outputFile).Length
    $outputSizeMB = [math]::Round($outputSize / 1MB, 2)
    
    Write-Host ""
    Write-Host "Compression complete!" -ForegroundColor Green
    Write-Host "Output file: $outputFile" -ForegroundColor Green
    Write-Host "Output size: ${outputSizeMB}MB"
    
    if ($outputSizeMB -gt $TargetSizeMB) {
        Write-Host "Warning: Output size exceeds target by $([math]::Round($outputSizeMB - $TargetSizeMB, 2))MB" -ForegroundColor Yellow
    }
} else {
    Write-Host ""
    Write-Host "Error: Compression failed" -ForegroundColor Red
    exit 1
}
