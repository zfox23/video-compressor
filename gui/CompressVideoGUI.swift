// Visual trim editor + settings dialog for compress-video-to-100mb-ffmpeg.sh
// Usage: CompressVideoGUI -InputFile <video file>
// Build: ./build.sh

import SwiftUI
import AVKit
import AVFoundation
import AppKit

// MARK: - Helpers

func fmtTime(_ t: Double) -> String {
    let t = max(0, t)
    let m = Int(t) / 60
    let s = t - Double(m * 60)
    return String(format: "%d:%06.3f", m, s)
}

/// Accepts seconds or [[HH:]MM:]SS[.ms], like the shell script.
func parseTime(_ text: String) -> Double? {
    let parts = text.trimmingCharacters(in: .whitespaces).split(separator: ":", omittingEmptySubsequences: false)
    guard (1...3).contains(parts.count) else { return nil }
    var total = 0.0
    for p in parts {
        guard let v = Double(p), v >= 0, v.isFinite else { return nil }
        total = total * 60 + v
    }
    return total
}

enum Phase {
    case editing
    case encoding
    case done(String)
    case failed(String)
}

// MARK: - Scrub audio

/// Plays audio while scrubbing, like an NLE: speed and direction follow the drag with pitch preserved
/// (time-stretched), and a short snippet plays on each frame step. Reads PCM straight from the file on demand.
final class ScrubAudio {
    private let engine = AVAudioEngine()
    private let node = AVAudioPlayerNode()
    private let timePitch = AVAudioUnitTimePitch()   // changes speed without changing pitch
    private let file: AVAudioFile
    private let format: AVAudioFormat
    private var lastT: Double?
    private var lastWall: CFTimeInterval = 0
    private var started = false
    private let maxSpeed = 4.0
    private let minSpeed = 0.25

    init?(url: URL) {
        guard let f = try? AVAudioFile(forReading: url), f.length > 0 else { return nil }
        file = f
        format = f.processingFormat
        engine.attach(node)
        engine.attach(timePitch)
        engine.connect(node, to: timePitch, format: format)
        engine.connect(timePitch, to: engine.mainMixerNode, format: format)
    }

    func reset() { lastT = nil }

    /// Marks `t` as the current scrub position so the next `scrub(to:)` continues from it.
    func prime(at t: Double) {
        lastT = t
        lastWall = CACurrentMediaTime()
    }

    /// Plays `length` seconds of source audio at normal speed from `t` (backwards when `reversed`).
    func snippet(at t: Double, length: Double, reversed: Bool) {
        guard let src = read(start: t, length: length) else { return }
        node.stop()   // interrupt whatever was queued
        play(src, reversed: reversed, rate: 1, fade: true)
    }

    /// Call on every scrub update with the new playhead time.
    func scrub(to t: Double) {
        let now = CACurrentMediaTime()
        guard let prev = lastT else {
            lastT = t; lastWall = now
            snippet(at: t, length: 0.06, reversed: false)
            return
        }
        let wall = now - lastWall
        if wall < 0.008 { return }   // accumulate tiny updates
        let delta = t - prev
        lastT = t; lastWall = now
        guard abs(delta) > 1e-4 else { return }
        let w = min(wall, 0.1)
        // Drag speed (timeline seconds per wall second), clamped to what the time-stretcher handles well
        let speed = min(max(abs(delta) / w, minSpeed), maxSpeed)
        let len = speed * w
        let start = delta > 0 ? t - len : t
        guard let src = read(start: start, length: len) else { return }
        play(src, reversed: delta < 0, rate: speed, fade: false)
    }

    private func read(start: Double, length: Double) -> [[Float]]? {
        let sr = format.sampleRate
        let startFrame = AVAudioFramePosition(max(0, start) * sr)
        let want = AVAudioFrameCount(max(length, 0) * sr)
        guard want > 1, startFrame < file.length,
              let buf = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: want) else { return nil }
        file.framePosition = startFrame
        try? file.read(into: buf, frameCount: want)
        guard buf.frameLength > 1, let ch = buf.floatChannelData else { return nil }
        return (0..<Int(format.channelCount)).map {
            Array(UnsafeBufferPointer(start: ch[$0], count: Int(buf.frameLength)))
        }
    }

    /// Queues `src` (optionally reversed) at natural pitch; `rate` speeds it up or slows it down
    /// through the time-stretch unit so a chunk of `rate * w` seconds of source plays in `w` seconds.
    private func play(_ src: [[Float]], reversed: Bool, rate: Double, fade: Bool) {
        let n = src[0].count
        guard n > 1,
              let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(n)),
              let dst = out.floatChannelData else { return }
        out.frameLength = AVAudioFrameCount(n)
        let ramp = min(240, n / 4)   // ~5 ms fades at 48 kHz
        for c in 0..<src.count {
            let a = src[c]
            for i in 0..<n {
                var v = reversed ? a[n - 1 - i] : a[i]
                if fade && ramp > 0 {
                    if i < ramp { v *= Float(i) / Float(ramp) }
                    else if i >= n - ramp { v *= Float(n - 1 - i) / Float(ramp) }
                }
                dst[c][i] = v
            }
        }
        if !started { started = (try? engine.start()) != nil }
        guard started else { return }
        timePitch.rate = Float(rate)
        node.scheduleBuffer(out, completionHandler: nil)
        if !node.isPlaying { node.play() }
    }
}

// MARK: - Model

@MainActor
final class Editor: ObservableObject {
    static weak var current: Editor?

    let inputURL: URL
    let player = AVPlayer()
    let minGap = 0.1

    @Published var duration = 0.0
    @Published var inT = 0.0
    @Published var outT = 0.0
    @Published var playhead = 0.0
    @Published var isPlaying = false
    @Published var thumbs: [NSImage] = []
    @Published var previewError: String?

    @Published var sizeText: String
    @Published var widthText: String
    @Published var heightText: String
    @Published var rotation = 0
    @Published var isHDR = false
    @Published var toneMapToSDR = false
    @Published var outputPath: String
    @Published var formError: String?

    @Published var phase: Phase = .editing
    @Published var progress = 0.0

    var frameStep = 1.0 / 30
    var dragging = false { didSet { if !dragging { scrubAudio?.reset() } } }
    var scrubAudio: ScrubAudio?
    private var displaySize: CGSize?   // video size as displayed (preferred transform applied)
    private var lastStepWall: CFTimeInterval = 0

    private var timeObserver: Any?
    private var seeking = false
    private var pendingSeek: Double?

    private var process: Process?
    private var cancelled = false
    private var stdoutLog = ""
    private var stderrTail = ""

    init(url: URL) {
        inputURL = url
        let d = UserDefaults.standard
        sizeText = d.string(forKey: "sizeText") ?? "100"
        widthText = ""
        heightText = ""
        let base = url.deletingPathExtension().lastPathComponent + "_compressed.mp4"
        outputPath = url.deletingLastPathComponent().appendingPathComponent(base).path
        Editor.current = self
    }

    var scriptURL: URL {
        // Walk up from the executable (gui/CompressVideo.app/Contents/MacOS/...) to find the script
        let name = "compress-video-to-100mb-ffmpeg.sh"
        var dir = URL(fileURLWithPath: Bundle.main.executablePath ?? CommandLine.arguments[0])
            .deletingLastPathComponent()
        for _ in 0..<6 {
            let candidate = dir.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: candidate.path) { return candidate }
            dir = dir.deletingLastPathComponent()
        }
        return dir.appendingPathComponent(name)
    }

    // MARK: Loading

    func load() async {
        let asset = AVURLAsset(url: inputURL)
        do {
            let d = try await asset.load(.duration).seconds
            guard d.isFinite, d > 0 else { throw CocoaError(.fileReadCorruptFile) }
            duration = d
            outT = d
            if !(try await asset.load(.isPlayable)) {
                previewError = "This video can't be previewed here. It will still be compressed (whole clip)."
                duration = 0
                return
            }
            var hdr = false
            if let track = try await asset.loadTracks(withMediaType: .video).first {
                let fps = try await track.load(.nominalFrameRate)
                if fps > 0 { frameStep = 1.0 / Double(fps) }
                hdr = try await track.load(.mediaCharacteristics).contains(.containsHDRVideo)
                let natural = try await track.load(.naturalSize)
                let transform = try await track.load(.preferredTransform)
                let r = CGRect(origin: .zero, size: natural).applying(transform)
                if r.width > 0, r.height > 0 { displaySize = CGSize(width: abs(r.width), height: abs(r.height)) }
            }
            let dims = defaultDims(rotation: rotation)
            if widthText.isEmpty && heightText.isEmpty { widthText = dims.w; heightText = dims.h }
            isHDR = hdr
            let item = AVPlayerItem(asset: asset)
            if hdr {
                // Preview HDR (HLG/PQ) tone-mapped to SDR (like VLC and the thumbnails). Apple's direct HDR
                // rendering of phone HLG footage looks blown out. The compressed file still keeps its HDR.
                let comp = try await AVMutableVideoComposition.videoComposition(withPropertiesOf: asset)
                comp.colorPrimaries = AVVideoColorPrimaries_ITU_R_709_2
                comp.colorTransferFunction = AVVideoTransferFunction_ITU_R_709_2
                comp.colorYCbCrMatrix = AVVideoYCbCrMatrix_ITU_R_709_2
                item.videoComposition = comp
            }
            player.replaceCurrentItem(with: item)
            timeObserver = player.addPeriodicTimeObserver(
                forInterval: CMTime(value: 1, timescale: 30), queue: .main
            ) { [weak self] t in
                MainActor.assumeIsolated { self?.tick(t.seconds) }
            }
            scrubAudio = ScrubAudio(url: inputURL)
            seek(0)
            await generateThumbs(asset)
        } catch {
            previewError = "Can't read this video for preview (\(error.localizedDescription)). It will still be compressed (whole clip)."
            duration = 0
        }
    }

    private func generateThumbs(_ asset: AVURLAsset) async {
        let gen = AVAssetImageGenerator(asset: asset)
        gen.appliesPreferredTrackTransform = true
        gen.dynamicRangePolicy = .forceSDR  // thumbnails are drawn in an SDR context, so tone-map them
        gen.maximumSize = CGSize(width: 320, height: 180)
        let n = 16
        let times = (0..<n).map {
            CMTime(seconds: duration * (Double($0) + 0.5) / Double(n), preferredTimescale: 600)
        }
        var got: [(Double, NSImage)] = []
        for await r in gen.images(for: times) {
            if let cg = try? r.image {
                got.append((r.requestedTime.seconds, NSImage(cgImage: cg, size: .zero)))
                thumbs = got.sorted { $0.0 < $1.0 }.map { $0.1 }
            }
        }
    }

    /// 1080p for the output orientation: the short side is 1080 and the long side keeps the video's aspect
    /// ratio (1920 for 16:9). Empty (= auto) when the video size is unknown.
    func defaultDims(rotation: Int) -> (w: String, h: String) {
        guard var size = displaySize else { return ("", "") }
        if rotation == 90 || rotation == 270 { size = CGSize(width: size.height, height: size.width) }
        let long = Int((1080 * max(size.width, size.height) / min(size.width, size.height) / 2).rounded()) * 2
        return size.height > size.width ? ("1080", String(long)) : (String(long), "1080")
    }

    /// Follows the rotation with the prefilled size unless the user has edited the fields.
    func rotationChanged(from old: Int) {
        let was = defaultDims(rotation: old), now = defaultDims(rotation: rotation)
        if widthText == was.w && heightText == was.h { widthText = now.w; heightText = now.h }
    }

    // MARK: Playback / seeking

    private func tick(_ t: Double) {
        guard isPlaying, !dragging else { return }
        if t >= outT {
            player.pause()
            isPlaying = false
            setPlayhead(outT)
            return
        }
        playhead = max(t, inT)
    }

    func seek(_ t: Double) {
        pendingSeek = t
        if !seeking { runSeek() }
    }

    private func runSeek() {
        guard let t = pendingSeek else { return }
        pendingSeek = nil
        seeking = true
        player.seek(to: CMTime(seconds: t, preferredTimescale: 600),
                    toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            Task { @MainActor in
                self?.seeking = false
                self?.runSeek()
            }
        }
    }

    func pause() {
        player.pause()
        isPlaying = false
    }

    func setPlayhead(_ t: Double) {
        playhead = min(max(t, inT), outT)
        seek(playhead)
        if dragging { scrubAudio?.scrub(to: playhead) }   // audible scrubbing while dragging
    }

    func setIn(_ t: Double) {
        inT = min(max(0, t), outT - minGap)
        setPlayhead(inT)
    }

    func setOut(_ t: Double) {
        outT = max(min(duration, t), inT + minGap)
        setPlayhead(outT)
    }

    func togglePlay() {
        guard duration > 0 else { return }
        if isPlaying { pause(); return }
        if playhead >= outT - 0.03 || playhead < inT { setPlayhead(inT) }
        player.play()
        isPlaying = true
    }

    func step(_ frames: Int) {
        guard duration > 0 else { return }
        pause()
        let before = playhead
        setPlayhead(playhead + Double(frames) * frameStep)
        guard playhead != before else { return }
        let now = CACurrentMediaTime()
        if now - lastStepWall < 0.25 {
            // Held key (key repeat): continuous scrub audio instead of restarting a snippet each time,
            // which would cut off the previous one and sound choppy
            scrubAudio?.scrub(to: playhead)
        } else {
            // Single step: short audio snippet for the frame we landed on (reversed when stepping back)
            let len = max(frameStep * 4, 0.12)
            scrubAudio?.snippet(at: playhead, length: len, reversed: frames < 0)
            scrubAudio?.prime(at: playhead)
        }
        lastStepWall = now
    }

    // MARK: Compression

    var estimate: String {
        guard duration > 0, let mb = Double(sizeText), mb > 0 else { return "" }
        let clip = max(outT - inT, 0.001)
        let kbps = (mb * 0.95 * 8 * 1024 * 1024 / clip - 128_000) / 1000
        var s = String(format: "Clip length %@ — video bitrate ≈ %.0f kbps", fmtTime(clip), max(kbps, 0))
        if kbps < 500 { s += " (below 500 kbps: output will exceed the target size)" }
        return s
    }

    func compress() {
        formError = nil
        guard let mb = Double(sizeText), mb > 0 else { formError = "Target size must be a positive number."; return }
        for (label, text) in [("Width", widthText), ("Height", heightText)] {
            if !text.isEmpty, Int(text).map({ $0 > 0 }) != true {
                formError = "\(label) must be a positive whole number (or empty)."
                return
            }
        }
        guard !outputPath.isEmpty, outputPath != inputURL.path else {
            formError = "Choose an output file different from the input."
            return
        }
        if FileManager.default.fileExists(atPath: outputPath) {
            let alert = NSAlert()
            alert.messageText = "Replace existing file?"
            alert.informativeText = outputPath
            alert.addButton(withTitle: "Replace")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
            try? FileManager.default.removeItem(atPath: outputPath)
        }

        let d = UserDefaults.standard
        d.set(sizeText, forKey: "sizeText")

        var args = [scriptURL.path, "-i", inputURL.path, "-s", sizeText, "-r", String(rotation), "-o", outputPath]
        if !widthText.isEmpty { args += ["-w", widthText] }
        if !heightText.isEmpty { args += ["-h", heightText] }
        if isHDR && toneMapToSDR { args.append("-t") }
        var clipLen = duration
        if duration > 0 {
            if inT > 0.001 { args += ["-b", String(format: "%.3f", inT)] }
            if outT < duration - 0.001 { args += ["-e", String(format: "%.3f", outT)] }
            clipLen = outT - inT
        }

        pause()
        cancelled = false
        stdoutLog = ""
        stderrTail = ""
        progress = 0
        phase = .encoding

        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/bash")
        p.arguments = args
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = "/opt/homebrew/bin:/usr/local/bin:" + (env["PATH"] ?? "/usr/bin:/bin")
        p.environment = env
        p.standardInput = FileHandle.nullDevice
        let outPipe = Pipe(), errPipe = Pipe()
        p.standardOutput = outPipe
        p.standardError = errPipe

        outPipe.fileHandleForReading.readabilityHandler = { [weak self] h in
            let data = h.availableData
            guard !data.isEmpty else { return }
            Task { @MainActor in self?.consume(data, isErr: false, clipLen: clipLen) }
        }
        errPipe.fileHandleForReading.readabilityHandler = { [weak self] h in
            let data = h.availableData
            guard !data.isEmpty else { return }
            Task { @MainActor in self?.consume(data, isErr: true, clipLen: clipLen) }
        }
        p.terminationHandler = { [weak self] proc in
            Task { @MainActor in
                outPipe.fileHandleForReading.readabilityHandler = nil
                errPipe.fileHandleForReading.readabilityHandler = nil
                if let rest = try? outPipe.fileHandleForReading.readToEnd() { self?.consume(rest, isErr: false, clipLen: clipLen) }
                if let rest = try? errPipe.fileHandleForReading.readToEnd() { self?.consume(rest, isErr: true, clipLen: clipLen) }
                self?.finished(status: proc.terminationStatus)
            }
        }
        do {
            try p.run()
            process = p
        } catch {
            phase = .failed("Could not start the compression script: \(error.localizedDescription)")
        }
    }

    private func consume(_ data: Data, isErr: Bool, clipLen: Double) {
        let text = String(decoding: data, as: UTF8.self)
        if isErr {
            stderrTail = String((stderrTail + text).suffix(4000))
            if clipLen > 0,
               let m = stderrTail.matches(of: /time=(\d+):(\d\d):(\d\d(?:\.\d+)?)/).last,
               let h = Double(m.1), let mi = Double(m.2), let s = Double(m.3) {
                progress = min(1, (h * 3600 + mi * 60 + s) / clipLen)
            }
        } else {
            stdoutLog += text
        }
    }

    private func finished(status: Int32) {
        process = nil
        if cancelled {
            try? FileManager.default.removeItem(atPath: outputPath)
            phase = .editing
            return
        }
        if status == 0 {
            let bytes = (try? FileManager.default.attributesOfItem(atPath: outputPath)[.size] as? Int) ?? 0
            var msg = String(format: "Saved %.2f MB to %@", Double(bytes) / 1024 / 1024, outputPath)
            let warnings = stdoutLog.split(separator: "\n").filter { $0.hasPrefix("Warning") }
            if !warnings.isEmpty { msg += "\n" + warnings.joined(separator: "\n") }
            progress = 1
            phase = .done(msg)
        } else {
            try? FileManager.default.removeItem(atPath: outputPath)
            let errors = stdoutLog.split(separator: "\n").filter { $0.hasPrefix("Error") }
            let detail = errors.isEmpty
                ? stderrTail.split(separator: "\n").suffix(6).joined(separator: "\n")
                : errors.joined(separator: "\n")
            phase = .failed(detail.isEmpty ? "Compression failed (exit code \(status))." : detail)
        }
    }

    func cancelEncoding() {
        cancelled = true
        process?.terminate()
    }
}

// MARK: - Player

final class PlayerNSView: NSView {
    // AVPlayerView (not a bare AVPlayerLayer) so HDR (HLG/PQ) renders with proper EDR handling
    let avView = AVPlayerView()
    var rotation = 0 { didSet { needsLayout = true } }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        avView.controlsStyle = .none
        avView.videoGravity = .resizeAspect
        avView.showsFullScreenToggleButton = false
        avView.updatesNowPlayingInfoCenter = false
        addSubview(avView)
    }
    required init?(coder: NSCoder) { fatalError() }

    // Click the video to focus it; the keyboard shortcuts then work (text fields lose focus).
    override var acceptsFirstResponder: Bool { true }
    override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(self) }
    // Keep clicks (and keys) on this view rather than the inner AVPlayerView
    override func hitTest(_ point: NSPoint) -> NSView? { frame.contains(point) ? self : nil }
    override func becomeFirstResponder() -> Bool {
        layer?.borderColor = NSColor.controlAccentColor.cgColor
        layer?.borderWidth = 2
        return true
    }
    override func resignFirstResponder() -> Bool {
        layer?.borderWidth = 0
        return true
    }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        // Start with the video focused rather than the first text field
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [weak self] in
            guard let self, let w = self.window else { return }
            w.makeFirstResponder(self)
        }
    }

    override func layout() {
        super.layout()
        let area = bounds.insetBy(dx: 2, dy: 2)   // leaves room for the focus border
        let swapped = rotation == 90 || rotation == 270
        let size = swapped ? CGSize(width: area.height, height: area.width) : area.size
        avView.frameCenterRotation = 0
        avView.frame = CGRect(x: area.midX - size.width / 2, y: area.midY - size.height / 2,
                              width: size.width, height: size.height)
        // Positive frameCenterRotation is counterclockwise; the script rotates clockwise
        avView.frameCenterRotation = -CGFloat(rotation)
    }
}

struct PlayerView: NSViewRepresentable {
    let player: AVPlayer
    let rotation: Int

    func makeNSView(context: Context) -> PlayerNSView {
        let v = PlayerNSView()
        v.avView.player = player
        return v
    }
    func updateNSView(_ v: PlayerNSView, context: Context) { v.rotation = rotation }
}

// MARK: - Trim timeline

struct TimelineView: View {
    @ObservedObject var e: Editor
    private let handleW: CGFloat = 12

    var body: some View {
        GeometryReader { geo in
            let W = geo.size.width, H = geo.size.height
            let trackW = max(W - 2 * handleW, 1)
            let dur = max(e.duration, 0.001)
            let xOf: (Double) -> CGFloat = { handleW + CGFloat($0 / dur) * trackW }
            let tOf: (CGFloat) -> Double = { Double(($0 - handleW) / trackW) * dur }
            let xIn = xOf(e.inT), xOut = xOf(e.outT)

            ZStack(alignment: .topLeading) {
                // Filmstrip
                HStack(spacing: 0) {
                    if e.thumbs.isEmpty {
                        Color.gray.opacity(0.3)
                    } else {
                        ForEach(e.thumbs.indices, id: \.self) { i in
                            Image(nsImage: e.thumbs[i])
                                .resizable().scaledToFill()
                                .frame(width: trackW / CGFloat(e.thumbs.count), height: H)
                                .clipped()
                        }
                    }
                }
                .frame(width: trackW, height: H)
                .position(x: handleW + trackW / 2, y: H / 2)

                // Dim outside the trimmed zone
                Rectangle().fill(Color.black.opacity(0.6))
                    .frame(width: max(xIn - handleW, 0), height: H)
                    .position(x: handleW + max(xIn - handleW, 0) / 2, y: H / 2)
                Rectangle().fill(Color.black.opacity(0.6))
                    .frame(width: max(W - handleW - xOut, 0), height: H)
                    .position(x: xOut + max(W - handleW - xOut, 0) / 2, y: H / 2)

                // Zone border
                Rectangle().stroke(Color.yellow, lineWidth: 3)
                    .frame(width: max(xOut - xIn, 1), height: H)
                    .position(x: (xIn + xOut) / 2, y: H / 2)
                    .allowsHitTesting(false)

                // Click/drag anywhere to move the playhead (clamped to the zone)
                Color.clear.contentShape(Rectangle())
                    .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("tl"))
                        .onChanged { v in
                            if !e.dragging { e.pause(); e.dragging = true; NSApp.keyWindow?.makeFirstResponder(nil) }
                            e.setPlayhead(tOf(v.location.x))
                        }
                        .onEnded { _ in e.dragging = false })

                // Playhead
                Rectangle().fill(Color.white)
                    .frame(width: 2, height: H)
                    .overlay(alignment: .top) { Circle().fill(Color.white).frame(width: 10, height: 10).offset(y: -3) }
                    .position(x: xOf(e.playhead), y: H / 2)
                    .shadow(radius: 1)
                    .allowsHitTesting(false)

                // In handle
                handle(left: true)
                    .position(x: xIn - handleW / 2, y: H / 2)
                    .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("tl"))
                        .onChanged { v in
                            if !e.dragging { e.pause(); e.dragging = true; NSApp.keyWindow?.makeFirstResponder(nil) }
                            e.setIn(tOf(v.location.x + handleW / 2))
                        }
                        .onEnded { _ in e.dragging = false })

                // Out handle
                handle(left: false)
                    .position(x: xOut + handleW / 2, y: H / 2)
                    .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("tl"))
                        .onChanged { v in
                            if !e.dragging { e.pause(); e.dragging = true; NSApp.keyWindow?.makeFirstResponder(nil) }
                            e.setOut(tOf(v.location.x - handleW / 2))
                        }
                        .onEnded { _ in e.dragging = false })
            }
            .coordinateSpace(name: "tl")
            .frame(width: W, height: H)
        }
    }

    private func handle(left: Bool) -> some View {
        RoundedRectangle(cornerRadius: 3)
            .fill(Color.yellow)
            .overlay(Capsule().fill(Color.black.opacity(0.6)).frame(width: 2, height: 20))
            .frame(width: handleW)
    }
}

// MARK: - Main view

struct ContentView: View {
    @ObservedObject var e: Editor
    @State private var inText = fmtTime(0)
    @State private var outText = ""
    @State private var keyMonitor: Any?

    var body: some View {
        VStack(spacing: 12) {
            ZStack {
                Color.black
                PlayerView(player: e.player, rotation: e.rotation)
                if let msg = e.previewError {
                    Text(msg).foregroundStyle(.white).multilineTextAlignment(.center).padding()
                }
            }
            .frame(minHeight: 260)
            .clipShape(RoundedRectangle(cornerRadius: 6))

            if e.duration > 0 {
                TimelineView(e: e).frame(height: 60)
                transport
            }

            settings
            if e.duration > 0 {
                Text(e.estimate).font(.callout).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            Divider()
            bottom
        }
        .padding(16)
        .frame(minWidth: 760, minHeight: 720)
        .navigationTitle(e.inputURL.lastPathComponent)
        .task { await e.load() }
        .onChange(of: e.inT) { _, v in inText = fmtTime(v) }
        .onChange(of: e.outT) { _, v in outText = fmtTime(v) }
        .onChange(of: e.duration) { _, v in outText = fmtTime(v) }
        .onChange(of: e.rotation) { old, _ in e.rotationChanged(from: old) }
        .onAppear(perform: installKeys)
        .onDisappear { if let m = keyMonitor { NSEvent.removeMonitor(m) } }
    }

    private var transport: some View {
        HStack(spacing: 10) {
            Button { e.togglePlay() } label: {
                Image(systemName: e.isPlaying ? "pause.fill" : "play.fill").frame(width: 18)
            }
            Button("◀︎") { e.step(-1) }.help("Previous frame (←)")
            Button("▶︎") { e.step(1) }.help("Next frame (→)")
            Text(fmtTime(e.playhead)).monospacedDigit().frame(width: 70)
            Spacer()
            Text("In")
            TextField("0:00.000", text: $inText)
                .frame(width: 85).onSubmit { commit(isIn: true) }
            Button("Set In (I)") { e.setIn(e.playhead) }.help("Set in point at playhead (I)")
            Text("Out")
            TextField("", text: $outText)
                .frame(width: 85).onSubmit { commit(isIn: false) }
            Button("Set Out (O)") { e.setOut(e.playhead) }.help("Set out point at playhead (O)")
        }
        .disabled(isBusy)
    }

    private func hint(_ key: String, _ action: String) -> some View {
        HStack(spacing: 5) {
            Text(key)
                .font(.system(.caption, design: .monospaced).weight(.semibold))
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(RoundedRectangle(cornerRadius: 4).fill(Color.secondary.opacity(0.2)))
            Text(action).font(.caption).foregroundStyle(.secondary)
        }
    }

    private var settings: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Text("Target size")
                TextField("100", text: $e.sizeText).frame(width: 60)
                Text("MB")
                Spacer().frame(width: 16)
                Text("Width")
                TextField("auto", text: $e.widthText).frame(width: 70)
                Text("Height")
                TextField("auto", text: $e.heightText).frame(width: 70)
                Spacer().frame(width: 16)
                Text("Rotate")
                Picker("", selection: $e.rotation) {
                    ForEach([0, 90, 180, 270], id: \.self) { Text("\($0)°").tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 200)
            }
            if e.isHDR {
                Toggle("Convert HDR to SDR", isOn: $e.toneMapToSDR)
                    .help("Off keeps HDR (10-bit HEVC). On makes SDR H.264 that looks right everywhere. Needs ffmpeg with zscale")
            }
            HStack {
                Text("Output")
                TextField("", text: $e.outputPath)
                Button("Choose…") { chooseOutput() }
            }
        }
        .disabled(isBusy)
    }

    @ViewBuilder private var bottom: some View {
        switch e.phase {
        case .editing:
            VStack(alignment: .leading, spacing: 6) {
                if let err = e.formError { Text(err).foregroundStyle(.red) }
                HStack {
                    Spacer()
                    Button("Cancel") { NSApp.terminate(nil) }.keyboardShortcut(.cancelAction)
                    Button("Compress") { e.compress() }.keyboardShortcut(.defaultAction)
                }
            }
        case .encoding:
            HStack {
                ProgressView(value: e.progress)
                Text(String(format: "%.0f%%", e.progress * 100)).monospacedDigit().frame(width: 44)
                Button("Stop") { e.cancelEncoding() }
            }
        case .done(let msg):
            HStack(alignment: .top) {
                Text(msg).textSelection(.enabled)
                Spacer()
                Button("Reveal in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: e.outputPath)])
                }
                Button("Done") { NSApp.terminate(nil) }.keyboardShortcut(.defaultAction)
            }
        case .failed(let msg):
            HStack(alignment: .top) {
                Text(msg).foregroundStyle(.red).textSelection(.enabled)
                Spacer()
                Button("Back") { e.phase = .editing }
                Button("Quit") { NSApp.terminate(nil) }
            }
        }
    }

    private var isBusy: Bool {
        if case .editing = e.phase { return false }
        return true
    }

    private func commit(isIn: Bool) {
        if let t = parseTime(isIn ? inText : outText) {
            isIn ? e.setIn(t) : e.setOut(t)
        }
        inText = fmtTime(e.inT)
        outText = fmtTime(e.outT)
        NSApp.keyWindow?.makeFirstResponder(nil)
    }

    private func chooseOutput() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.mpeg4Movie]
        panel.nameFieldStringValue = URL(fileURLWithPath: e.outputPath).lastPathComponent
        panel.directoryURL = URL(fileURLWithPath: e.outputPath).deletingLastPathComponent()
        if panel.runModal() == .OK, let url = panel.url { e.outputPath = url.path }
    }

    private func installKeys() {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [e] event in
            if NSApp.keyWindow?.firstResponder is NSText { return event }
            var handled = true
            MainActor.assumeIsolated {
                guard case .editing = e.phase, e.duration > 0 else { handled = false; return }
                guard event.modifierFlags.intersection([.command, .control, .option]).isEmpty else { handled = false; return }
                switch event.keyCode {
                case 49: e.togglePlay()      // space
                case 123: e.step(-1)         // left arrow
                case 124: e.step(1)          // right arrow
                default:
                    switch event.charactersIgnoringModifiers?.lowercased() {
                    case "i": e.setIn(e.playhead)
                    case "o": e.setOut(e.playhead)
                    default: handled = false
                    }
                }
            }
            return handled ? nil : event
        }
    }
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ n: Notification) {
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ app: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ n: Notification) {
        MainActor.assumeIsolated { Editor.current?.cancelEncoding() }
    }
}

// The path arrives as `-InputFile <path>`: Cocoa treats bare path arguments as documents to open,
// which suppresses the initial window.
let inputFileArg: String? = UserDefaults.standard.string(forKey: "InputFile")
    ?? (CommandLine.arguments.count > 1 && !CommandLine.arguments[1].hasPrefix("-") ? CommandLine.arguments[1] : nil)

struct Root: View {
    @StateObject private var editor = Editor(url: URL(fileURLWithPath: inputFileArg ?? ""))
    var body: some View {
        if inputFileArg != nil {
            ContentView(e: editor)
        } else {
            Text("Usage: CompressVideoGUI -InputFile <video file>").padding(40)
        }
    }
}

@main
struct CompressVideoApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    var body: some Scene {
        WindowGroup { Root() }
            .windowResizability(.contentMinSize)
    }
}
