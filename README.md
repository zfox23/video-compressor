# compress-video-to-100mb-ffmpeg

`compress-video-to-100mb-ffmpeg.sh` (macOS/Linux) and `.ps1` (Windows) re-encode a video to fit a target size.

Options (`.sh` flag / `.ps1` parameter): input `-i`/`-InputFile`, size `-s`/`-TargetSizeMB`, `-w`/`-OutputWidth`, `-h`/`-OutputHeight`
(neither = 1080p, one = aspect ratio preserved), rotation `-r`/`-Rotation`, trim `-b -e`/`-StartTime -EndTime`, output `-o`/`-OutputFile`.

Audio: all audio tracks are mixed into one output track (e.g. OBS game + mic tracks). Set the volume per track with
`-a`/`-AudioVolumes` as a comma-separated list (`1` = unchanged, `0.5` = half, `0` = muted, default `1`), e.g. `-a 1,0.6`.
The Windows GUI has a mixer for this: a volume slider and VU meter per track and a meter for the mix (red at the right end
means the mix clips), and the preview plays all tracks mixed. Slider settings are remembered per track number.

AV1 input is decoded on the GPU (d3d11va) on Windows when possible; ffmpeg builds without dav1d otherwise use the very slow libaom.

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

Both scripts behave the same: `-OutputWidth`/`-OutputHeight` default to auto (1080p, aspect ratio kept), and the target size can be a decimal.

## macOS Quick Action: "Compress video..."
Finder > right-click > Quick Actions > **Compress video...** opens `gui/CompressVideo.app`, a small SwiftUI app with a
visual trim editor (drag in/out handles, scrub the playhead inside the trimmed zone) and the script's settings pre-filled.
It runs the `.sh` with the chosen arguments.

Editor keys: `Space` play/pause, `I`/`O` set in/out at the playhead, `←`/`→` step one frame.

The Quick Action rebuilds the app automatically when `gui/CompressVideoGUI.swift` changes; to build manually run `gui/build.sh`.

After a compression finishes, **Back** returns to the editor with the trim and settings kept, so you can compress again with
different settings without reopening the app.

## Windows Explorer menu: "Compress video..."
The same editor exists for Windows (`gui/CompressVideoGUI.cs`, a WPF app; same trim editor, keys, settings, HDR preview
tone-mapping and scrub audio). It runs the `.ps1` with the chosen arguments. To set it up, run once:
```
powershell -ExecutionPolicy Bypass -File gui\install.ps1
```
This builds `gui\CompressVideo.exe` with the C# compiler that ships with Windows (nothing to install) and adds
**Compress video...** to the right-click menu for video files (per-user, no admin). On Windows 11 it is under
**Show more options** (or Shift+right-click). Remove it with `gui\install.ps1 -Uninstall`.

The app rebuilds itself when `gui/CompressVideoGUI.cs` changes; to build manually run `gui\build.ps1`. The preview,
filmstrip and scrub audio are decoded with ffmpeg, so any file ffmpeg reads can be previewed. Scrubbing fast changes the
audio pitch (like tape), whereas macOS keeps the pitch.
