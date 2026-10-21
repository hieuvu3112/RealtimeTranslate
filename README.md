# RealtimeTranslate

## Độ chính xác dịch Việt → Anh

Pipeline: Mic/System audio → VAD → PhoWhisper (ASR) → làm sạch văn bản → dịch.

Cải thiện chính:
- VAD: pre-buffer 500ms (không mất âm tiết đầu), bỏ đoạn < 400ms giọng nói thực, 800ms im lặng mới cắt câu, đệm 300ms im lặng 2 đầu.
- Resample 48k → 16k có lọc thông thấp (không còn méo do nearest-neighbour).
- Whisper: fallback nhiệt độ/entropy chống lặp, lọc đoạn độ tin cậy thấp & câu ảo giác ("subscribe", "ghiền mì gõ"...).
- Gộp vòng lặp từ ("A B C A B C" → "A B C").
- Dịch có ngữ cảnh bằng LLM (khuyến nghị) — tự sửa lỗi ASR, viết hoa tên riêng, giữ mạch hội thoại.

### Bật dịch bằng LLM (OpenAI-compatible)

```bash
# OpenAI
export TRANSLATE_LLM_API_KEY=sk-...          # hoặc OPENAI_API_KEY
export TRANSLATE_LLM_MODEL=gpt-4o-mini       # tuỳ chọn

# Hoặc Ollama chạy local (không cần key)
export TRANSLATE_LLM_URL=http://localhost:11434/v1/chat/completions
export TRANSLATE_LLM_MODEL=qwen2.5:7b

export TRANSLATE_CONTEXT=5                   # số câu trước dùng làm ngữ cảnh
```

Không cấu hình → dùng Google Translate (văn bản được chuẩn hoá trước). LLM lỗi → tự fallback sang Google.
Debug: `TRANSLATE_DEBUG=1` hiển thị xác suất từng đoạn và lý do bỏ qua.
