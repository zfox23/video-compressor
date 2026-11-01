# compress-video-to-100mb-ffmpeg

`compress-video-to-100mb-ffmpeg.sh` (macOS/Linux) and `.ps1` (Windows) re-encode a video to fit a target size.

Options (`.sh` flag / `.ps1` parameter): input `-i`/`-InputFile`, size `-s`/`-TargetSizeMB`, `-w`/`-OutputWidth`, `-h`/`-OutputHeight`
(neither = 1080p, one = aspect ratio preserved), rotation `-r`/`-Rotation`, trim `-b -e`/`-StartTime -EndTime`, output `-o`/`-OutputFile`.

HDR (HLG/PQ) input stays HDR: it is encoded as 10-bit HEVC (`hvc1`) with the BT.2020 colour tags kept, so it plays as HDR on
HDR-capable players. Dolby Vision / HDR10+ dynamic metadata is dropped. SDR input is encoded as 8-bit H.264.

To get SDR instead (looks right in every player), use `-t` (`.sh`) / `-ToneMapToSDR` (`.ps1`) or the "Convert HDR to SDR" checkbox in the GUI
(shown for HDR clips). This tone-maps to 8-bit BT.709 H.264 and needs an ffmpeg build with the `zscale` filter (libzimg).

### Installing ffmpeg with zscale
- macOS (Homebrew core's ffmpeg lacks it):
  ```
  brew uninstall ffmpeg
  brew tap homebrew-ffmpeg/ffmpeg
  brew install homebrew-ffmpeg/ffmpeg/ffmpeg --with-zimg
  ffmpeg -filters | grep zscale     # should print a line
  ```
  (If brew asks, run `brew trust homebrew-ffmpeg/ffmpeg` first. `--with-zimg` builds from source, so it takes a while.)
- Windows: `winget install Gyan.FFmpeg` (the full build includes zimg), then `ffmpeg -filters | findstr zscale`.

## macOS Quick Action: "Compress video..."
Finder > right-click > Quick Actions > **Compress video...** opens `gui/CompressVideo.app`, a small SwiftUI app with a
visual trim editor (drag in/out handles, scrub the playhead inside the trimmed zone) and the script's settings pre-filled.
It runs the `.sh` with the chosen arguments.

Editor keys: `Space` play/pause, `I`/`O` set in/out at the playhead, `←`/`→` step one frame.

The Quick Action rebuilds the app automatically when `gui/CompressVideoGUI.swift` changes; to build manually run `gui/build.sh`.
