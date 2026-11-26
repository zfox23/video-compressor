#!/usr/bin/env pwsh
# Video compression script for iPhone 4K Dolby Vision to 1080p60
# Target: 100MB max filesize (configurable)
# PowerShell version for Windows (same behaviour as compress-video-to-100mb-ffmpeg.sh)

param(
    [string]$InputFile = "",

    [double]$TargetSizeMB = 100,

    [int]$OutputWidth = 0,

    [int]$OutputHeight = 0,

    [ValidateSet(0, 90, 180, 270)]
    [int]$Rotation = 0,

    [switch]$ToneMapToSDR,

    [string]$StartTime = "",

    [string]$EndTime = "",

    [string]$OutputFile = "",

    [string]$AudioVolumes = ""
)

$inv = [Globalization.CultureInfo]::InvariantCulture

# When run from the GUI (output piped), write UTF-8 so paths with non-ASCII characters survive
if ([Console]::IsOutputRedirected) {
    try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
}

# Convert [[HH:]MM:]SS[.ms] or plain seconds to seconds ($null if invalid)
function ConvertTo-Seconds([string]$text) {
    if ($text -notmatch '^\d+(\.\d+)?(:\d+(\.\d+)?){0,2}$') { return $null }
    $total = 0.0
    foreach ($part in $text.Split(':')) {
        $total = $total * 60 + [double]::Parse($part, $inv)
    }
    return $total
}

# Quote one argument for a Windows command line (CommandLineToArgvW rules)
function ConvertTo-Arg([string]$a) {
    if ($a -ne "" -and $a -notmatch '[\s"]') { return $a }
    return '"' + ($a -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Show-Usage {
    $me = ".\compress-video-to-100mb-ffmpeg.ps1"
    Write-Host "Usage: $me -InputFile <path> [-TargetSizeMB <mb>] [-OutputWidth <w>] [-OutputHeight <h>] [-Rotation <degrees>] [-StartTime <time>] [-EndTime <time>] [-ToneMapToSDR] [-AudioVolumes <v1,v2,...>] [-OutputFile <path>]"
    Write-Host ""
    Write-Host "Options:"
    Write-Host "  -InputFile       Input video file (required)"
    Write-Host "  -TargetSizeMB    Target size in MB (default: 100)"
    Write-Host "  -OutputWidth     Output width (aspect ratio preserved if -OutputHeight is not given)"
    Write-Host "  -OutputHeight    Output height (aspect ratio preserved if -OutputWidth is not given)"
    Write-Host "                   If neither is given, the short side is scaled to 1080 (1080p)"
    Write-Host "  -Rotation        Clockwise rotation in degrees: 0, 90, 180, or 270 (default: 0)"
    Write-Host "  -StartTime       Start time: seconds or [HH:]MM:SS[.ms] (default: start of video)"
    Write-Host "  -EndTime         End time: seconds or [HH:]MM:SS[.ms] (default: end of video)"
    Write-Host "  -ToneMapToSDR    Tone-map HDR input to SDR (needs ffmpeg with zscale/libzimg; default keeps HDR)"
    Write-Host "  -AudioVolumes    Volume per audio track, comma-separated (1 = unchanged, 0.5 = half, 0 = muted; default 1)."
    Write-Host "                   All audio tracks are mixed into one (e.g. OBS game + mic tracks)"
    Write-Host "  -OutputFile      Output file path (default: <input>_compressed.mp4)"
    Write-Host ""
    Write-Host "Examples:"
    Write-Host "  $me -InputFile video.mov"
    Write-Host "  $me -InputFile video.mov -TargetSizeMB 50"
    Write-Host "  $me -InputFile video.mov -TargetSizeMB 100 -OutputWidth 1280 -OutputHeight 720"
    Write-Host "  $me -InputFile video.mov -OutputWidth 1280"
    Write-Host "  $me -InputFile video.mov -StartTime 0:30 -EndTime 1:45"
    Write-Host "  $me -InputFile video.mov -Rotation 90 -OutputFile rotated.mp4"
    Write-Host "  $me -InputFile obs.mp4 -AudioVolumes 1,0.6"
}

# Show usage if help is requested
if ($InputFile -eq "-h" -or $InputFile -eq "--help" -or $InputFile -eq "/?") {
    Show-Usage
    exit 0
}
if (-not $InputFile) {
    Write-Host "Error: Input file (-InputFile) is required" -ForegroundColor Red
    Show-Usage
    exit 1
}
if ($TargetSizeMB -le 0) {
    Write-Host "Error: Target size must be a positive number" -ForegroundColor Red
    exit 1
}
if ($OutputWidth -lt 0 -or $OutputHeight -lt 0) {
    Write-Host "Error: Width and height must be positive integers" -ForegroundColor Red
    exit 1
}

# Check if input file exists
if (-not (Test-Path -LiteralPath $InputFile -PathType Leaf)) {
    Write-Host "Error: Input file '$InputFile' not found" -ForegroundColor Red
    exit 1
}
$InputFile = (Get-Item -LiteralPath $InputFile).FullName

# Default output filename if not specified
if (-not $OutputFile) {
    $OutputFile = Join-Path ([IO.Path]::GetDirectoryName($InputFile)) ([IO.Path]::GetFileNameWithoutExtension($InputFile) + "_compressed.mp4")
}

# Check if ffmpeg and ffprobe are available
$ffmpeg = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$ffprobe = Get-Command ffprobe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $ffmpeg -or -not $ffprobe) {
    Write-Host "Error: ffmpeg is not installed or not in PATH" -ForegroundColor Red
    Write-Host "Install it with: winget install Gyan.FFmpeg" -ForegroundColor Yellow
    exit 1
}

# Get video duration in seconds
Write-Host "Analyzing video..." -ForegroundColor Cyan
$durationOutput = & $ffprobe.Source -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "$InputFile" 2>$null | Select-Object -First 1
$duration = 0.0
if (-not [double]::TryParse("$durationOutput".Trim(), [Globalization.NumberStyles]::Float, $inv, [ref]$duration) -or $duration -le 0) {
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
# Account for container overhead and audio bitrate
$safeTargetSizeMB = $TargetSizeMB * 0.95
$targetSizeBits = $safeTargetSizeMB * 8 * 1024 * 1024
$audioBitrate = 128000

# Calculate video bitrate: (total_bits / duration) - audio_bitrate
$targetVideoBitrate = [long][math]::Floor(($targetSizeBits / $duration) - $audioBitrate)

# Ensure bitrate is reasonable (minimum 500kbps)
if ($targetVideoBitrate -lt 500000) {
    Write-Host "Warning: Calculated bitrate is very low. Video may be too long for ${TargetSizeMB}MB target." -ForegroundColor Yellow
    $targetVideoBitrate = 500000
}

# Build scale filter. -2 keeps aspect ratio while rounding to an even size.
# The scale filter runs after rotation, so dimensions refer to the rotated frame.
if ($OutputWidth -gt 0 -and $OutputHeight -gt 0) {
    $scale = "scale=${OutputWidth}:${OutputHeight}:flags=lanczos"
    $resolutionDesc = "${OutputWidth}x${OutputHeight}"
} elseif ($OutputWidth -gt 0) {
    $scale = "scale=${OutputWidth}:-2:flags=lanczos"
    $resolutionDesc = "${OutputWidth}xauto (aspect ratio preserved)"
} elseif ($OutputHeight -gt 0) {
    $scale = "scale=-2:${OutputHeight}:flags=lanczos"
    $resolutionDesc = "autox${OutputHeight} (aspect ratio preserved)"
} else {
    # 1080p: short side becomes 1080, so portrait video becomes 1080x1920
    $scale = "scale='if(gt(iw,ih),-2,1080)':'if(gt(iw,ih),1080,-2)':flags=lanczos"
    $resolutionDesc = "1080p (aspect ratio preserved)"
}

# Detect HDR (HLG or PQ). HDR input is kept as HDR: 10-bit HEVC with the colour tags preserved.
# SDR input uses 8-bit H.264 for maximum compatibility.
function Get-StreamField([string]$field) {
    $value = & $ffprobe.Source -v error -select_streams v:0 -show_entries "stream=$field" -of "default=noprint_wrappers=1:nokey=1" "$InputFile" 2>$null | Select-Object -First 1
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
        $filterList = & $ffmpeg.Source -hide_banner -filters 2>$null | Out-String
        if ($filterList -notmatch " zscale ") {
            Write-Host "Error: HDR to SDR tone-mapping needs an ffmpeg build with the zscale filter (libzimg)." -ForegroundColor Red
            Write-Host "Install one with: winget install Gyan.FFmpeg" -ForegroundColor Yellow
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

# Decode AV1 on the GPU when it can: ffmpeg builds without dav1d (e.g. the gyan.dev "essentials" build) otherwise
# decode AV1 with libaom, which is very slow. One test frame decides; if it fails, ffmpeg's default decoder is used.
$decodeArgs = @()
$decodeDesc = "software"
if ((Get-StreamField "codec_name") -eq "av1") {
    $test = & $ffmpeg.Source -v error -hwaccel d3d11va -c:v av1 -i "$InputFile" -frames:v 1 -an -f framemd5 - 2>$null
    if ($LASTEXITCODE -eq 0 -and ($test | Select-String '^0,')) {
        $decodeArgs = @("-hwaccel", "d3d11va", "-c:v", "av1")
        $decodeDesc = "GPU (d3d11va)"
    }
}

# Audio: every audio track is mixed into one, each at its own volume
$volumeList = @()
if ($AudioVolumes) {
    foreach ($v in ($AudioVolumes -split '[,\s]+' | Where-Object { $_ })) {
        $d = 0.0
        if (-not [double]::TryParse($v.Trim(), [Globalization.NumberStyles]::Float, $inv, [ref]$d) -or $d -lt 0) {
            Write-Host "Error: Invalid audio volume '$v' (use numbers like 1, 0.5 or 0)" -ForegroundColor Red
            exit 1
        }
        $volumeList += $d
    }
}
$audioCount = @(& $ffprobe.Source -v error -select_streams a -show_entries stream=index -of csv=p=0 "$InputFile" 2>$null | Where-Object { "$_".Trim() }).Count
$mapArgs = @("-map", "0:v:0")
$audioDesc = "none"
if ($audioCount -gt 0) {
    $chains = @(); $labels = ""; $pcts = @()
    for ($i = 0; $i -lt $audioCount; $i++) {
        $v = if ($i -lt $volumeList.Count) { $volumeList[$i] } else { 1.0 }
        $chains += "[0:a:${i}]volume=$($v.ToString($inv))[a$i]"
        $labels += "[a$i]"
        $pcts += "$([math]::Round($v * 100))%"
    }
    # normalize=0: the volumes are exactly as given (amix would otherwise divide every track by the track count)
    $mix = if ($audioCount -gt 1) { "${labels}amix=inputs=${audioCount}:normalize=0[aout]" } else { "[a0]anull[aout]" }
    $mapArgs = @("-filter_complex", (($chains + $mix) -join ";"), "-map", "0:v:0", "-map", "[aout]")
    $audioDesc = if ($audioCount -gt 1) { "$audioCount tracks mixed into one ($($pcts -join ', '))" } else { "1 track ($($pcts[0]))" }
}

$deg = [char]0xB0
Write-Host ""
Write-Host "Input file: $InputFile" -ForegroundColor Green
if ($StartTime -or $EndTime) {
    Write-Host "Clip: $([math]::Round($startSec, 3))s to $([math]::Round($endSec, 3))s"
}
Write-Host "Duration: $([math]::Round($duration, 2))s"
Write-Host "Target size: ${TargetSizeMB}MB (using $([math]::Round($safeTargetSizeMB, 2))MB with safety margin)"
Write-Host "Output resolution: $resolutionDesc"
Write-Host "Rotation: ${Rotation}$deg clockwise"
Write-Host "Encoding: $encodeDesc"
Write-Host "Decoding: $decodeDesc"
Write-Host "Audio: $audioDesc"
Write-Host "Target video bitrate: $([math]::Floor($targetVideoBitrate / 1000))kbps"
Write-Host "Output file: $OutputFile" -ForegroundColor Green
Write-Host ""
Write-Host "Starting compression..." -ForegroundColor Cyan

# Build filter chain
$filter = "$scale,fps=60"

# Tone-map after scaling/fps so the float conversion runs on the smallest frames
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
# - HDR (HLG/PQ) input stays HDR: 10-bit HEVC with BT.2020 colour tags (Dolby Vision /
#   HDR10+ dynamic metadata is dropped), or with -ToneMapToSDR is tone-mapped to SDR H.264; SDR input uses H.264
# - Scale to target resolution (preserving aspect ratio unless both dimensions given)
# - Set framerate to 60fps
# - Rotate if specified
$ffmpegArgs = $decodeArgs + @(
    "-ss", $startSec.ToString($inv),
    "-i", $InputFile,
    "-t", $duration.ToString($inv),
    "-vf", $filter
) + $mapArgs + $videoArgs + @(
    "-b:v", "$targetVideoBitrate",
    "-maxrate", "$targetVideoBitrate",
    "-bufsize", "$($targetVideoBitrate * 2)",
    "-c:a", "aac",
    "-b:a", "128k",
    "-ac", "2",
    "-movflags", "+faststart",
    $OutputFile
)

# Run ffmpeg directly (not through cmd.exe, which would mangle paths containing & or %).
# It shares this console, so its progress output goes wherever ours goes.
$argLine = ($ffmpegArgs | ForEach-Object { ConvertTo-Arg "$_" }) -join " "
$process = Start-Process -FilePath $ffmpeg.Source -ArgumentList $argLine -NoNewWindow -Wait -PassThru

if ($process.ExitCode -eq 0) {
    $outputSizeMB = [math]::Round((Get-Item -LiteralPath $OutputFile).Length / 1MB, 2)

    Write-Host ""
    Write-Host "Compression complete!" -ForegroundColor Green
    Write-Host "Output file: $OutputFile" -ForegroundColor Green
    Write-Host "Output size: ${outputSizeMB}MB"

    if ($outputSizeMB -gt $TargetSizeMB) {
        Write-Host "Warning: Output size exceeds target by $([math]::Round($outputSizeMB - $TargetSizeMB, 2))MB" -ForegroundColor Yellow
    }
} else {
    Write-Host ""
    Write-Host "Error: Compression failed" -ForegroundColor Red
    exit 1
}
