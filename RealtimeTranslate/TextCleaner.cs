using System.Text.RegularExpressions;

/// <summary>
/// Làm sạch output của Whisper/PhoWhisper trước khi dịch:
/// bỏ tag không phải lời nói, gộp vòng lặp lặp từ, lọc câu ảo giác phổ biến.
/// </summary>
static partial class TextCleaner
{
    // Câu "ảo giác" Whisper hay sinh ra khi gặp im lặng/nhạc (học từ phụ đề YouTube)
    private static readonly string[] HallucinationPhrases =
    [
        "subscribe",
        "đăng ký kênh",
        "đăng kí kênh",
        "ghiền mì gõ",
        "la la school",
        "cảm ơn các bạn đã theo dõi",
        "cảm ơn các bạn đã xem",
        "hẹn gặp lại các bạn trong những video tiếp theo",
        "like và share",
        "thanks for watching",
        "thank you for watching",
    ];

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = NonSpeechTagRegex().Replace(text, " ");
        text = WhitespaceRegex().Replace(text, " ").Trim();
        text = CollapseRepeats(text);
        return text;
    }

    public static bool IsLikelyHallucination(string text, float probability, float noSpeechProbability)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!text.Any(char.IsLetterOrDigit)) return true;

        string lower = text.ToLowerInvariant();
        if (HallucinationPhrases.Any(p => lower.Contains(p))) return true;

        // Độ tin cậy rất thấp, hoặc model cho rằng đây không phải giọng nói
        if (probability > 0 && probability < 0.25f) return true;
        if (noSpeechProbability > 0.6f && probability < 0.5f) return true;

        return false;
    }

    /// <summary>
    /// Gộp các cụm từ lặp liên tiếp (lỗi lặp vòng của Whisper):
    /// "học tập thể chất vấn đề học tập thể chất vấn đề" → "học tập thể chất vấn đề".
    /// Cụm 1–2 từ chỉ gộp khi lặp ≥ 3 lần (giữ "không không", "dạ dạ" tự nhiên).
    /// </summary>
    public static string CollapseRepeats(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int n = Math.Min(15, words.Count / 2); n >= 1 && !changed; n--)
            {
                int minRepeats = n <= 2 ? 3 : 2;
                for (int i = 0; i + n * minRepeats <= words.Count && !changed; i++)
                {
                    int repeats = 1;
                    while (i + n * (repeats + 1) <= words.Count && SameSpan(words, i, i + n * repeats, n))
                        repeats++;

                    if (repeats >= minRepeats)
                    {
                        words.RemoveRange(i + n, n * (repeats - 1));
                        changed = true;
                    }
                }
            }
        }
        return string.Join(' ', words);
    }

    private static bool SameSpan(List<string> w, int a, int b, int n)
    {
        for (int k = 0; k < n; k++)
            if (!string.Equals(Norm(w[a + k]), Norm(w[b + k]), StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private static string Norm(string w) => w.Trim('.', ',', '!', '?', '…', ';', ':');

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*(nhạc|music|tiếng|applause|vỗ tay|cười|laugh)[^)]*\)|[♪♫]+", RegexOptions.IgnoreCase)]
    private static partial Regex NonSpeechTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
