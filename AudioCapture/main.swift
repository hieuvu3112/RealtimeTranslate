// AudioCapture — Bắt system audio qua ScreenCaptureKit (macOS 13+)
// Output: raw Float32 PCM, 48 kHz, mono → stdout
// C# phía kia sẽ đọc và resample xuống 16 kHz.
//
// Build:
//   swiftc -O main.swift -o AudioCapture \
//     -framework ScreenCaptureKit -framework CoreMedia \
//     -framework CoreGraphics -framework Foundation
//
// Cấp quyền: System Settings → Privacy & Security → Screen & System Audio Recording

import Foundation
import ScreenCaptureKit
import CoreMedia
import CoreGraphics

guard #available(macOS 13.0, *) else {
    fputs("❌ ScreenCaptureKit audio requires macOS 13.0 or later.\n", stderr)
    exit(1)
}

// ── Kiểm tra quyền Screen Recording ngay khi khởi động ───────────
fputs("🔍 Kiểm tra quyền Screen & System Audio Recording...\n", stderr)
if !CGPreflightScreenCaptureAccess() {
    fputs("⚠️  Chưa được cấp quyền 'Screen & System Audio Recording'.\n", stderr)
    fputs("   Đang yêu cầu quyền — macOS sẽ mở System Settings...\n", stderr)
    CGRequestScreenCaptureAccess()
    // Chờ 2s để dialog hiện ra rồi thoát — user cấp xong chạy lại app
    Thread.sleep(forTimeInterval: 2)
    fputs("   Sau khi cấp quyền trong System Settings → Privacy & Security\n", stderr)
    fputs("   → Screen & System Audio Recording, khởi động lại ứng dụng.\n", stderr)
    exit(1)
}
fputs("✅ Đã có quyền Screen & System Audio Recording.\n", stderr)

// ── Delegate nhận audio từ SCStream ──────────────────────────────
final class AudioCaptureDelegate: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    var byteCount: Int = 0
    var lastLogTime = Date()

    func stream(_ stream: SCStream,
                didOutputSampleBuffer buf: CMSampleBuffer,
                of type: SCStreamOutputType) {
        guard type == .audio else { return }
        guard let blockBuf = CMSampleBufferGetDataBuffer(buf) else { return }

        var length = 0
        var ptr: UnsafeMutablePointer<Int8>?
        let status = CMBlockBufferGetDataPointer(
            blockBuf,
            atOffset: 0,
            lengthAtOffsetOut: nil,
            totalLengthOut: &length,
            dataPointerOut: &ptr)

        guard status == kCMBlockBufferNoErr, let p = ptr, length > 0 else { return }

        // Ghi thẳng Float32 PCM ra stdout (C# đọc và resample)
        FileHandle.standardOutput.write(Data(bytes: p, count: length))
        byteCount += length

        // Log mỗi 5 giây để xác nhận audio đang chạy
        let now = Date()
        if now.timeIntervalSince(lastLogTime) >= 5.0 {
            fputs("📊 Audio flowing: \(byteCount / 1024) KB written so far\n", stderr)
            lastLogTime = now
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        fputs("Stream stopped: \(error.localizedDescription)\n", stderr)
        exit(0)
    }
}

// ── Main ──────────────────────────────────────────────────────────
let capturer = AudioCaptureDelegate()
var activeStream: SCStream?

Task {
    do {
        fputs("🔄 Đang lấy danh sách màn hình...\n", stderr)
        let content = try await SCShareableContent.excludingDesktopWindows(
            false, onScreenWindowsOnly: false)

        guard let display = content.displays.first else {
            fputs("❌ Không tìm thấy display.\n", stderr)
            exit(1)
        }

        fputs("🖥  Dùng display: \(display.displayID)\n", stderr)

        let cfg = SCStreamConfiguration()
        cfg.capturesAudio               = true
        cfg.excludesCurrentProcessAudio = true
        cfg.sampleRate                  = 48_000
        cfg.channelCount                = 1        // mono
        // SCStream bắt buộc có video — dùng kích thước tối thiểu
        cfg.width                       = 2
        cfg.height                      = 2
        cfg.minimumFrameInterval        = CMTime(value: 1, timescale: 1)
        cfg.showsCursor                 = false
        cfg.queueDepth                  = 8

        let stream = SCStream(
            filter: SCContentFilter(display: display, excludingWindows: []),
            configuration: cfg,
            delegate: capturer)

        try stream.addStreamOutput(
            capturer,
            type: .audio,
            sampleHandlerQueue: .global(qos: .userInteractive))

        try await stream.startCapture()
        activeStream = stream
        fputs("✅ System audio capture started — 48 kHz mono Float32 → stdout\n", stderr)
        fputs("   Nếu không nghe gì, hãy phát audio (nhạc/video) trên máy.\n", stderr)
    } catch {
        fputs("❌ Lỗi ScreenCaptureKit: \(error.localizedDescription)\n", stderr)
        fputs("   Error code: \((error as NSError).code)\n", stderr)
        fputs("   Domain: \((error as NSError).domain)\n", stderr)
        if (error as NSError).code == -3801 {
            fputs("   → Lỗi -3801: Bị từ chối quyền. Kiểm tra System Settings → Privacy → Screen Recording\n", stderr)
        }
        exit(1)
    }
}

signal(SIGINT, SIG_DFL)
signal(SIGTERM, SIG_DFL)

dispatchMain()
