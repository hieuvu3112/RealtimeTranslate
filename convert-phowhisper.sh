#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────
# Convert vinai/PhoWhisper-large → GGML format cho Whisper.net
# Chạy: bash convert-phowhisper.sh
# Yêu cầu: Python 3, ~10 GB dung lượng trống, kết nối internet
# ─────────────────────────────────────────────────────────────────
set -e

OUTPUT_DIR="$(cd "$(dirname "$0")" && pwd)/RealtimeTranslate/bin/Debug/net10.0"
OUTPUT_FILE="$OUTPUT_DIR/ggml-phowhisper-large.bin"

WHISPER_CPP_DIR="/tmp/whisper-cpp-convert"
OPENAI_WHISPER_DIR="/tmp/openai-whisper"
MODEL_DIR="/tmp/phowhisper-large"

echo "══════════════════════════════════════════"
echo "  Convert vinai/PhoWhisper-large → GGML"
echo "══════════════════════════════════════════"
echo "  Output: $OUTPUT_FILE"
echo ""

# ── 1. Kiểm tra Python ───────────────────────────────────────────
if ! command -v python3 &>/dev/null; then
    echo "❌ Cần Python 3: brew install python3"
    exit 1
fi
echo "✅ Python3: $(python3 --version)"

# ── 2. Cài Python dependencies ───────────────────────────────────
echo ""
echo "📦 Cài torch + transformers + huggingface_hub..."
pip3 install -q torch numpy transformers huggingface_hub

# ── 3. Clone whisper.cpp (sparse checkout chỉ lấy models/) ───────
echo ""
if [ ! -d "$WHISPER_CPP_DIR" ]; then
    echo "📥 Clone whisper.cpp (sparse)..."
    git clone --depth=1 --filter=blob:none --sparse \
        https://github.com/ggerganov/whisper.cpp "$WHISPER_CPP_DIR"
    cd "$WHISPER_CPP_DIR" && git sparse-checkout set models
else
    echo "✅ whisper.cpp đã có tại $WHISPER_CPP_DIR"
fi

CONVERT_SCRIPT="$WHISPER_CPP_DIR/models/convert-h5-to-ggml.py"
if [ ! -f "$CONVERT_SCRIPT" ]; then
    echo "❌ Không tìm thấy $CONVERT_SCRIPT"
    exit 1
fi
echo "✅ Script convert: $CONVERT_SCRIPT"

# ── 4. Clone openai/whisper (cần vocab/tokenizer) ────────────────
echo ""
if [ ! -d "$OPENAI_WHISPER_DIR" ]; then
    echo "📥 Clone openai/whisper (vocab/tokenizer)..."
    git clone --depth=1 https://github.com/openai/whisper "$OPENAI_WHISPER_DIR"
else
    echo "✅ openai/whisper đã có tại $OPENAI_WHISPER_DIR"
fi

# ── 5. Download vinai/PhoWhisper-large từ HuggingFace ────────────
echo ""
if [ -d "$MODEL_DIR" ] && [ "$(ls -A "$MODEL_DIR")" ]; then
    echo "✅ PhoWhisper-large đã có tại $MODEL_DIR"
else
    echo "📥 Download vinai/PhoWhisper-large từ HuggingFace (~3 GB)..."
    python3 - <<'PYEOF'
from huggingface_hub import snapshot_download
snapshot_download(
    repo_id="vinai/PhoWhisper-large",
    local_dir="/tmp/phowhisper-large",
    ignore_patterns=["*.msgpack", "*.h5", "flax_model*", "tf_model*", "rust_model*"]
)
print("✅ Download hoàn tất!")
PYEOF
fi

# ── 6. Convert sang GGML ─────────────────────────────────────────
echo ""
echo "🔄 Đang convert sang GGML (có thể mất vài phút)..."
mkdir -p "$OUTPUT_DIR"
python3 "$CONVERT_SCRIPT" "$MODEL_DIR" "$OPENAI_WHISPER_DIR" "$OUTPUT_DIR"

# Script output: ggml-model.bin hoặc ggml-model-f32.bin
CONVERTED=""
for f in "$OUTPUT_DIR/ggml-model.bin" "$OUTPUT_DIR/ggml-model-f32.bin"; do
    [ -f "$f" ] && CONVERTED="$f" && break
done

if [ -z "$CONVERTED" ]; then
    echo "❌ Không tìm thấy file output trong $OUTPUT_DIR"
    exit 1
fi

# ── 7. Đổi tên thành tên chuẩn ──────────────────────────────────
mv "$CONVERTED" "$OUTPUT_FILE"

echo ""
echo "══════════════════════════════════════════"
echo "✅ Hoàn tất!"
echo "   File: $OUTPUT_FILE"
echo "   Kích thước: $(du -sh "$OUTPUT_FILE" | cut -f1)"
echo ""
echo "🚀 Chạy ứng dụng để bắt đầu dịch!"
echo "══════════════════════════════════════════"
