#!/usr/bin/env bash
# build-audio-capture.sh — Biên dịch AudioCapture Swift CLI
# Yêu cầu: Xcode Command Line Tools (xcode-select --install)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC="$SCRIPT_DIR/AudioCapture/main.swift"
OUT_DIR="$SCRIPT_DIR/RealtimeTranslate/bin/Debug/net10.0"
OUT_BIN="$OUT_DIR/AudioCapture"

echo "🔨 Compiling AudioCapture.swift → $OUT_BIN"

mkdir -p "$OUT_DIR"

swiftc -O "$SRC" \
    -o "$OUT_BIN" \
    -framework ScreenCaptureKit \
    -framework CoreMedia \
    -framework CoreGraphics \
    -framework Foundation \
    -target arm64-apple-macos13.0

chmod +x "$OUT_BIN"

# ── Codesign với ad-hoc signature ────────────────────────────────
# Bắt buộc để macOS TCC nhận ra binary trong System Settings
# → Privacy & Security → Screen & System Audio Recording
echo "🔏 Codesigning binary..."
codesign --force --deep --sign - "$OUT_BIN"
echo "✅ Codesign OK"

echo ""
echo "✅ Build thành công: $OUT_BIN"
echo ""
echo "══════════════════════════════════════════════════════"
echo " QUAN TRỌNG — Cấp quyền Screen & System Audio Recording"
echo "══════════════════════════════════════════════════════"
echo ""
echo " Bước 1: Chạy lần đầu từ Terminal để macOS hiện dialog:"
echo "         $OUT_BIN"
echo "         (Binary sẽ tự mở System Settings, cấp quyền rồi Ctrl+C)"
echo ""
echo " Bước 2: System Settings → Privacy & Security"
echo "         → Screen & System Audio Recording"
echo "         → Bật toggle cho AudioCapture"
echo ""
echo " Bước 3: Khởi động lại ứng dụng C# và chọn [s] System Audio"
echo "══════════════════════════════════════════════════════"
