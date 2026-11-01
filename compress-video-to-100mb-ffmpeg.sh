#!/bin/bash

# Video compression script for iPhone 4K Dolby Vision to 1080p60
# Target: 100MB max filesize (configurable)

# Ensure ffmpeg/ffprobe are in PATH (needed for Automator/Quick Actions)
for prefix in /opt/homebrew /usr/local; do
    if [ -x "$prefix/bin/ffmpeg" ] && [ -x "$prefix/bin/ffprobe" ]; then
        export PATH="$prefix/bin:$PATH"
        break
    fi
done

# Default values
TARGET_SIZE_MB=100
OUTPUT_WIDTH=""
OUTPUT_HEIGHT=""
ROTATION=0
INPUT=""
OUTPUT=""
START_TIME=""
END_TIME=""
TONEMAP_SDR=0

# Convert [[HH:]MM:]SS[.ms] or plain seconds to seconds (empty output if invalid)
to_seconds() {
    echo "$1" | awk -F: '
        NF>3 || $0 !~ /^[0-9:.]+$/ { exit }
        { t=0; for (i=1;i<=NF;i++) { if ($i=="") exit; t=t*60+$i } print t }'
}

usage() {
    echo "Usage: $0 -i <input_file> [-s target_size_mb] [-w width] [-h height] [-r rotation_degrees] [-b start_time] [-e end_time] [-t] [-o output_file]"
    echo ""
    echo "Options:"
    echo "  -i  Input video file (required)"
    echo "  -s  Target size in MB (default: 100)"
    echo "  -w  Output width (aspect ratio preserved if -h is not given)"
    echo "  -h  Output height (aspect ratio preserved if -w is not given)"
    echo "      If neither -w nor -h is given, the short side is scaled to 1080 (1080p)"
    echo "  -r  Clockwise rotation in degrees: 0, 90, 180, or 270 (default: 0)"
    echo "  -b  Start time: seconds or [HH:]MM:SS[.ms] (default: start of video)"
    echo "  -e  End time: seconds or [HH:]MM:SS[.ms] (default: end of video)"
    echo "  -t  Tone-map HDR input to SDR (needs an ffmpeg build with zscale/libzimg; default keeps HDR)"
    echo "  -o  Output file path (default: <input>_compressed.mp4)"
    echo ""
    echo "Examples:"
    echo "  $0 -i video.mov"
    echo "  $0 -i video.mov -s 50"
    echo "  $0 -i video.mov -s 100 -w 1280 -h 720"
    echo "  $0 -i video.mov -w 1280"
    echo "  $0 -i video.mov -b 0:30 -e 1:45"
    echo "  $0 -i video.mov -r 90 -o rotated.mp4"
}

# Show usage if long help is requested
for arg in "$@"; do
    if [ "$arg" = "--help" ]; then
        usage
        exit 0
    fi
done

# Parse arguments (order-independent flags)
while getopts "i:s:w:h:r:b:e:to:" opt; do
    case $opt in
        i) INPUT="$OPTARG" ;;
        s) TARGET_SIZE_MB="$OPTARG" ;;
        w) OUTPUT_WIDTH="$OPTARG" ;;
        h) OUTPUT_HEIGHT="$OPTARG" ;;
        r) ROTATION="$OPTARG" ;;
        b) START_TIME="$OPTARG" ;;
        e) END_TIME="$OPTARG" ;;
        t) TONEMAP_SDR=1 ;;
        o) OUTPUT="$OPTARG" ;;
        \?)
            usage
            exit 1
            ;;
    esac
done

# Validate required arguments
if [ -z "$INPUT" ]; then
    echo "Error: Input file (-i) is required"
    echo "Run with -? for usage information"
    exit 1
fi

# Validate rotation
case "$ROTATION" in
    0|90|180|270) ;;
    *)
        echo "Error: Rotation must be 0, 90, 180, or 270 degrees"
        exit 1
        ;;
esac

# Validate width/height are positive integers when given
for dim in "$OUTPUT_WIDTH" "$OUTPUT_HEIGHT"; do
    case "$dim" in
        ""|*[!0-9]*)
            if [ -n "$dim" ]; then
                echo "Error: Width and height must be positive integers"
                exit 1
            fi
            ;;
    esac
done

# Check if input file exists
if [ ! -f "$INPUT" ]; then
    echo "Error: Input file '$INPUT' not found"
    exit 1
fi

# Default output filename if not specified
if [ -z "$OUTPUT" ]; then
    OUTPUT="${INPUT%.*}_compressed.mp4"
fi

# Check if ffmpeg and ffprobe are available
if ! command -v ffmpeg >/dev/null 2>&1 || ! command -v ffprobe >/dev/null 2>&1; then
    echo "Error: ffmpeg is not installed or not in PATH"
    echo "Please install ffmpeg from https://ffmpeg.org/download.html"
    exit 1
fi

# Get video duration in seconds
echo "Analyzing video..."
DURATION=$(ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "$INPUT" 2>/dev/null)

if [ -z "$DURATION" ] || [ "$(echo "$DURATION > 0" | bc 2>/dev/null)" != "1" ]; then
    echo "Error: Could not determine video duration"
    exit 1
fi

# Resolve clip start/end (seconds) and the length of the portion being encoded
START_SEC=0
END_SEC="$DURATION"
if [ -n "$START_TIME" ]; then
    START_SEC=$(to_seconds "$START_TIME")
    if [ -z "$START_SEC" ]; then
        echo "Error: Invalid start time '$START_TIME'"
        exit 1
    fi
fi
if [ -n "$END_TIME" ]; then
    END_SEC=$(to_seconds "$END_TIME")
    if [ -z "$END_SEC" ]; then
        echo "Error: Invalid end time '$END_TIME'"
        exit 1
    fi
    # Clamp to the end of the video
    if [ "$(echo "$END_SEC > $DURATION" | bc)" -eq 1 ]; then
        END_SEC="$DURATION"
    fi
fi
if [ "$(echo "$START_SEC >= $END_SEC" | bc)" -eq 1 ]; then
    echo "Error: Start time must be before end time (and within the video)"
    exit 1
fi
DURATION=$(echo "scale=3; $END_SEC - $START_SEC" | bc)

# Calculate target bitrate with safety margin
# Use 95% of target size to ensure we stay under the limit
# Account for container overhead and audio bitrate
SAFE_TARGET_SIZE_MB=$(echo "scale=2; $TARGET_SIZE_MB * 0.95" | bc)
TARGET_SIZE_BITS=$(echo "scale=0; $SAFE_TARGET_SIZE_MB * 8 * 1024 * 1024" | bc)
AUDIO_BITRATE=128000

# Calculate video bitrate: (total_bits / duration) - audio_bitrate
TARGET_VIDEO_BITRATE=$(echo "scale=0; ($TARGET_SIZE_BITS / $DURATION) - $AUDIO_BITRATE" | bc)

# Ensure bitrate is reasonable (minimum 500kbps)
if [ "$TARGET_VIDEO_BITRATE" -lt 500000 ]; then
    echo "Warning: Calculated bitrate is very low. Video may be too long for ${TARGET_SIZE_MB}MB target."
    TARGET_VIDEO_BITRATE=500000
fi

# Build scale filter. -2 keeps aspect ratio while rounding to an even size.
# The scale filter runs after rotation, so dimensions refer to the rotated frame.
if [ -n "$OUTPUT_WIDTH" ] && [ -n "$OUTPUT_HEIGHT" ]; then
    SCALE="scale=${OUTPUT_WIDTH}:${OUTPUT_HEIGHT}:flags=lanczos"
    RESOLUTION_DESC="${OUTPUT_WIDTH}x${OUTPUT_HEIGHT}"
elif [ -n "$OUTPUT_WIDTH" ]; then
    SCALE="scale=${OUTPUT_WIDTH}:-2:flags=lanczos"
    RESOLUTION_DESC="${OUTPUT_WIDTH}xauto (aspect ratio preserved)"
elif [ -n "$OUTPUT_HEIGHT" ]; then
    SCALE="scale=-2:${OUTPUT_HEIGHT}:flags=lanczos"
    RESOLUTION_DESC="autox${OUTPUT_HEIGHT} (aspect ratio preserved)"
else
    # 1080p: short side becomes 1080, so portrait video becomes 1080x1920
    SCALE="scale='if(gt(iw,ih),-2,1080)':'if(gt(iw,ih),1080,-2)':flags=lanczos"
    RESOLUTION_DESC="1080p (aspect ratio preserved)"
fi

# Detect HDR (HLG or PQ). HDR input is kept as HDR: 10-bit HEVC with the colour tags preserved.
# SDR input uses 8-bit H.264 for maximum compatibility.
TONEMAP_FILTER=""
read_stream_field() {
    ffprobe -v error -select_streams v:0 -show_entries "stream=$1" -of default=noprint_wrappers=1:nokey=1 "$INPUT" 2>/dev/null | head -n 1
}
COLOR_TRC=$(read_stream_field color_transfer)
COLOR_PRIMARIES=$(read_stream_field color_primaries)
COLOR_SPACE=$(read_stream_field color_space)
if [ "$COLOR_TRC" = "arib-std-b67" ] || [ "$COLOR_TRC" = "smpte2084" ]; then
    if [ "$COLOR_TRC" = "arib-std-b67" ]; then HDR_DESC="HDR (HLG)"; else HDR_DESC="HDR (PQ/HDR10)"; fi
    if [ "$TONEMAP_SDR" -eq 1 ]; then
        # Tone-map HDR to SDR BT.709 (needs the zscale filter from an ffmpeg build with libzimg)
        if ! ffmpeg -hide_banner -filters 2>/dev/null | grep -q " zscale "; then
            echo "Error: HDR to SDR tone-mapping needs an ffmpeg build with the zscale filter (libzimg)."
            echo "Install one with: brew tap homebrew-ffmpeg/ffmpeg && brew install homebrew-ffmpeg/ffmpeg/ffmpeg --with-zimg"
            exit 1
        fi
        ENCODE_DESC="H.264 8-bit SDR, tone-mapped from $HDR_DESC"
        VIDEO_ARGS=(-c:v libx264 -preset slow -pix_fmt yuv420p -color_primaries bt709 -color_trc bt709 -colorspace bt709)
        TONEMAP_FILTER="zscale=tin=${COLOR_TRC}:pin=${COLOR_PRIMARIES:-bt2020}:min=${COLOR_SPACE:-bt2020nc}:t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p"
    else
        ENCODE_DESC="HEVC 10-bit, $HDR_DESC preserved"
        VIDEO_ARGS=(-c:v libx265 -preset medium -tag:v hvc1 -pix_fmt yuv420p10le
            -color_primaries "${COLOR_PRIMARIES:-bt2020}" -color_trc "$COLOR_TRC" -colorspace "${COLOR_SPACE:-bt2020nc}")
    fi
else
    ENCODE_DESC="H.264 8-bit (SDR)"
    VIDEO_ARGS=(-c:v libx264 -preset slow -pix_fmt yuv420p)
fi

echo ""
echo "Input file: $INPUT"
[ -n "$START_TIME$END_TIME" ] && echo "Clip: ${START_SEC}s to ${END_SEC}s"
echo "Duration: $(printf '%.2f' "$DURATION")s"
echo "Target size: ${TARGET_SIZE_MB}MB (using ${SAFE_TARGET_SIZE_MB}MB with safety margin)"
echo "Output resolution: ${RESOLUTION_DESC}"
echo "Rotation: ${ROTATION}° clockwise"
echo "Encoding: ${ENCODE_DESC}"
echo "Target video bitrate: $((TARGET_VIDEO_BITRATE / 1000))kbps"
echo "Output file: $OUTPUT"
echo ""
echo "Starting compression..."

# Build filter chain
FILTER="${SCALE},fps=60"

# Tone-map after scaling/fps so the float conversion runs on the smallest frames
[ -n "$TONEMAP_FILTER" ] && FILTER="${FILTER},${TONEMAP_FILTER}"

# Add rotation if specified
if [ "$ROTATION" -eq 90 ]; then
    FILTER="transpose=1,${FILTER}"
elif [ "$ROTATION" -eq 180 ]; then
    FILTER="transpose=1,transpose=1,${FILTER}"
elif [ "$ROTATION" -eq 270 ]; then
    FILTER="transpose=2,${FILTER}"
fi

# Compress video
# - HDR (HLG/PQ) input stays HDR: 10-bit HEVC with BT.2020 colour tags (Dolby Vision /
#   HDR10+ dynamic metadata is dropped), or with -t is tone-mapped to SDR H.264; SDR input uses H.264
# - Scale to target resolution (preserving aspect ratio unless both dimensions given)
# - Set framerate to 60fps
# - Rotate if specified

ffmpeg -ss "$START_SEC" -i "$INPUT" \
    -t "$DURATION" \
    -vf "$FILTER" \
    "${VIDEO_ARGS[@]}" \
    -b:v ${TARGET_VIDEO_BITRATE} \
    -maxrate ${TARGET_VIDEO_BITRATE} \
    -bufsize $((TARGET_VIDEO_BITRATE * 2)) \
    -c:a aac \
    -b:a 128k \
    -ac 2 \
    -movflags +faststart \
    "$OUTPUT"

if [ $? -eq 0 ]; then
    OUTPUT_BYTES=$(wc -c < "$OUTPUT" | tr -d ' ')
    OUTPUT_SIZE_MB=$(echo "scale=2; $OUTPUT_BYTES / 1024 / 1024" | bc)
    echo ""
    echo "Compression complete!"
    echo "Output file: $OUTPUT"
    echo "Output size: ${OUTPUT_SIZE_MB}MB"

    if [ "$(echo "$OUTPUT_SIZE_MB > $TARGET_SIZE_MB" | bc)" -eq 1 ]; then
        echo "Warning: Output size exceeds target by $(echo "scale=2; $OUTPUT_SIZE_MB - $TARGET_SIZE_MB" | bc)MB"
    fi
else
    echo ""
    echo "Error: Compression failed"
    exit 1
fi
