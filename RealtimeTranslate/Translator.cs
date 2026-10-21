using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// Dịch có ngữ cảnh.
///   • LLM (OpenAI-compatible: OpenAI, Ollama, LM Studio, Groq, ...) — chính xác nhất:
///     nhận vài câu trước làm ngữ cảnh, tự sửa lỗi nhận dạng giọng nói (ASR), xử lý tên riêng.
///   • Google Translate (mặc định / fallback) — văn bản được chuẩn hoá trước khi gửi.
///
/// Biến môi trường:
///   TRANSLATE_LLM_API_KEY (hoặc OPENAI_API_KEY) — bật LLM
///   TRANSLATE_LLM_URL     — mặc định https://api.openai.com/v1/chat/completions
///                           (Ollama: http://localhost:11434/v1/chat/completions — không cần key)
///   TRANSLATE_LLM_MODEL   — mặc định gpt-4o-mini
///   TRANSLATE_CONTEXT     — số câu trước dùng làm ngữ cảnh (mặc định 5)
/// </summary>
sealed class Translator
{
    private const string DefaultLlmUrl   = "https://api.openai.com/v1/chat/completions";
    private const string DefaultLlmModel = "gpt-4o-mini";

    private readonly HttpClient _http;
    private readonly string _src;
    private readonly string _tgt;
    private readonly string? _llmUrl;
    private readonly string? _llmKey;
    private readonly string  _llmModel;
    private readonly int     _contextSize;
    private readonly LinkedList<(string src, string tgt)> _history = new();

    public string Description => _llmUrl is null
        ? "Google Translate (đặt TRANSLATE_LLM_API_KEY / TRANSLATE_LLM_URL để dùng LLM chính xác hơn)"
        : $"LLM {_llmModel} @ {new Uri(_llmUrl).Host} (ngữ cảnh {_contextSize} câu, fallback Google)";

    public Translator(HttpClient http, string srcLang, string tgtLang)
    {
        _http = http;
        _src  = srcLang;
        _tgt  = tgtLang;

        _llmKey = Env("TRANSLATE_LLM_API_KEY") ?? Env("OPENAI_API_KEY");
        string? url = Env("TRANSLATE_LLM_URL");
        _llmUrl   = url ?? (_llmKey is not null ? DefaultLlmUrl : null);
        _llmModel = Env("TRANSLATE_LLM_MODEL") ?? DefaultLlmModel;
        _contextSize = int.TryParse(Env("TRANSLATE_CONTEXT"), out int n) && n >= 0 ? n : 5;
    }

    public async Task<string> TranslateAsync(string text, CancellationToken ct)
    {
        string? result = null;

        if (_llmUrl is not null)
        {
            try { result = await TranslateWithLlmAsync(text, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Error.WriteLine($"[LLM] lỗi: {ex.Message} → dùng Google Translate");
                Console.ResetColor();
            }
        }

        if (string.IsNullOrWhiteSpace(result))
            result = await TranslateWithGoogleAsync(text, ct);

        Remember(text, result);
        return result;
    }

    // ── LLM ─────────────────────────────────────────────────────────
    private async Task<string?> TranslateWithLlmAsync(string text, CancellationToken ct)
    {
        string srcName = LanguageName(_src);
        string tgtName = LanguageName(_tgt);

        string system =
            $"You are a professional simultaneous interpreter translating {srcName} speech into natural, fluent {tgtName}.\n" +
            "The input comes from automatic speech recognition (ASR) of live audio, so it may:\n" +
            "- be all lowercase and lack punctuation;\n" +
            "- have the first syllable cut off (e.g. Vietnamese \"nhiên\" is usually \"tất nhiên\" or \"đột nhiên\");\n" +
            "- contain homophone or tone-mark errors, or proper names written in lowercase;\n" +
            "- be a fragment that continues the previous sentence.\n" +
            "Use the conversation context to infer the intended meaning, fix obvious ASR errors, keep proper names " +
            "(people, places, brands) correctly capitalized, and preserve the speaker's tone, pronouns and register.\n" +
            "Words already in the target language should be kept as-is.\n" +
            $"Output ONLY the {tgtName} translation of the CURRENT segment — no quotes, notes, explanations, " +
            "or translation of the context.";

        var messages = new List<object> { new { role = "system", content = system } };
        foreach (var (s, t) in _history)
        {
            messages.Add(new { role = "user",      content = s });
            messages.Add(new { role = "assistant", content = t });
        }
        messages.Add(new { role = "user", content = text });

        string body = JsonSerializer.Serialize(new
        {
            model       = _llmModel,
            temperature = 0.2,
            messages,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, _llmUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (_llmKey is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _llmKey);

        using var resp = await _http.SendAsync(req, ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Truncate(json, 200)}");

        using var doc = JsonDocument.Parse(json);
        string? content = doc.RootElement.GetProperty("choices")[0]
                             .GetProperty("message").GetProperty("content").GetString();
        return content?.Trim().Trim('"', '“', '”').Trim();
    }

    // ── Google Translate ────────────────────────────────────────────
    private async Task<string> TranslateWithGoogleAsync(string text, CancellationToken ct)
    {
        string normalized = NormalizeForMt(text);
        Exception? last = null;

        // Endpoint 1: dict-chrome-ex (ít bị rate-limit 429, hỗ trợ POST câu dài)
        // Endpoint 2: gtx/single (dự phòng)
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return attempt == 1
                    ? await GoogleGtxAsync(normalized, ct)
                    : await GoogleDictAsync(normalized, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(400 * (attempt + 1), ct);
            }
        }
        return $"[Translation error: {last?.Message}]";
    }

    private async Task<string> GoogleDictAsync(string text, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://translate.googleapis.com/translate_a/t?client=dict-chrome-ex&sl={_src}&tl={_tgt}")
        {
            Content = new FormUrlEncodedContent([new("q", text)]),
        };
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);

        // Format: ["translated"]  hoặc  [["translated","detectedLang"]]
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement[0];
        string? result = first.ValueKind == JsonValueKind.Array ? first[0].GetString() : first.GetString();
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("Google trả về rỗng");
        return result;
    }

    private async Task<string> GoogleGtxAsync(string text, CancellationToken ct)
    {
        string baseUrl = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={_src}&tl={_tgt}&dt=t";
        string escaped = Uri.EscapeDataString(text);
        // GET cho câu thường; POST khi câu quá dài (URL > ~2000 ký tự)
        using var req = escaped.Length < 1800
            ? new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}&q={escaped}")
            : new HttpRequestMessage(HttpMethod.Post, baseUrl)
              {
                  Content = new FormUrlEncodedContent([new("q", text)]),
              };
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);

        // Format: [[["translated","original",...], ...], ...]
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        foreach (var chunk in doc.RootElement[0].EnumerateArray())
        {
            string? part = chunk[0].GetString();
            if (!string.IsNullOrEmpty(part)) sb.Append(part);
        }
        return sb.ToString();
    }

    /// <summary>
    /// PhoWhisper trả văn bản viết thường, không dấu câu → MT dễ hiểu sai ranh giới câu.
    /// Viết hoa chữ đầu và thêm dấu chấm cuối giúp Google tách câu đúng hơn.
    /// </summary>
    private static string NormalizeForMt(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return text;
        text = char.ToUpper(text[0]) + text[1..];
        if (!".!?…".Contains(text[^1])) text += ".";
        return text;
    }

    private void Remember(string src, string tgt)
    {
        if (_contextSize == 0 || tgt.StartsWith("[Translation error")) return;
        _history.AddLast((src, tgt));
        while (_history.Count > _contextSize) _history.RemoveFirst();
    }

    private static string LanguageName(string code) => code switch
    {
        "vi" => "Vietnamese",
        "en" => "English",
        _    => code,
    };

    private static string? Env(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
