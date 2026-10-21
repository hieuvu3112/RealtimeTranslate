using System.Text;
using System.Text.Json;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

// ── QuestPDF license ──────────────────────────────────────────────
QuestPDF.Settings.License = LicenseType.Community;

// ── Font Setup ────────────────────────────────────────────────────
string fontDir = Path.Combine(AppContext.BaseDirectory, "fonts");
Directory.CreateDirectory(fontDir);

await EnsureFontAsync(fontDir, "NotoSans-Regular.ttf",
    "https://github.com/googlefonts/noto-fonts/raw/main/hinted/ttf/NotoSans/NotoSans-Regular.ttf");
await EnsureFontAsync(fontDir, "NotoSans-Italic.ttf",
    "https://github.com/googlefonts/noto-fonts/raw/main/hinted/ttf/NotoSans/NotoSans-Italic.ttf");

string regularFontPath = Path.Combine(fontDir, "NotoSans-Regular.ttf");
string italicFontPath  = Path.Combine(fontDir, "NotoSans-Italic.ttf");

if (File.Exists(regularFontPath))
{
    using var reg = File.OpenRead(regularFontPath);
    FontManager.RegisterFont(reg);
}
if (File.Exists(italicFontPath))
{
    using var ita = File.OpenRead(italicFontPath);
    FontManager.RegisterFont(ita);
}

// ── Input / Output ────────────────────────────────────────────────
string inputPath;
if (args.Length > 0)
{
    inputPath = args[0].Trim('"').Trim();
}
else
{
    Console.Write("📂 Nhập đường dẫn file PDF tiếng Anh: ");
    inputPath = Console.ReadLine()?.Trim('"').Trim() ?? "";
}

if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"❌ File không tồn tại: {inputPath}");
    Console.ResetColor();
    return 1;
}

string baseName  = Path.GetFileNameWithoutExtension(inputPath);
string outputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
string outputPath = Path.Combine(outputDir, baseName + "_song-ngu.pdf");

Console.WriteLine();
Console.WriteLine($"📖 Input:  {inputPath}");
Console.WriteLine($"📝 Output: {outputPath}");
Console.WriteLine();

// ── Extract paragraphs ────────────────────────────────────────────
Console.WriteLine("🔍 Đang đọc và phân tích file PDF...");
List<string> paragraphs;
try
{
    paragraphs = ExtractAllParagraphs(inputPath);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"❌ Không thể đọc PDF: {ex.Message}");
    Console.ResetColor();
    return 1;
}

if (paragraphs.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Error.WriteLine("⚠️  Không tìm thấy văn bản trong file PDF (có thể là PDF scan/hình ảnh).");
    Console.ResetColor();
    return 1;
}

Console.WriteLine($"✅ Tìm thấy {paragraphs.Count} đoạn văn bản.\n");

// ── Translate ─────────────────────────────────────────────────────
using var http   = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var       blocks = new List<(string English, string Vietnamese)>();

for (int i = 0; i < paragraphs.Count; i++)
{
    string para    = paragraphs[i];
    string preview = para.Length > 60 ? para[..60].Replace('\n', ' ') + "…" : para.Replace('\n', ' ');

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write($"  [{i + 1:D4}/{paragraphs.Count:D4}] ");
    Console.ResetColor();
    Console.Write($"⏳ {preview}");

    string translated = await TranslateWithRetryAsync(para, http);

    Console.Write("\r");
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write($"  [{i + 1:D4}/{paragraphs.Count:D4}] ");
    Console.ResetColor();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"✅ {preview}");
    Console.ResetColor();

    blocks.Add((para, translated));

    // Throttle to avoid hitting Google Translate rate limits
    await Task.Delay(350);
}

Console.WriteLine($"\n✅ Đã dịch xong {blocks.Count} đoạn.");

// ── Generate PDF ──────────────────────────────────────────────────
Console.WriteLine($"\n🔨 Đang tạo file PDF song ngữ...");
try
{
    GenerateBilingualPdf(outputPath, blocks, baseName);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"❌ Lỗi tạo PDF: {ex.Message}");
    Console.ResetColor();
    return 1;
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"\n🎉 Hoàn thành! → {outputPath}");
Console.ResetColor();
return 0;

// ═══════════════════════════════════════════════════════════════════
// PDF EXTRACTION
// ═══════════════════════════════════════════════════════════════════

static List<string> ExtractAllParagraphs(string pdfPath)
{
    var allParagraphs = new List<string>();

    using var pdf = PdfDocument.Open(pdfPath);
    foreach (var page in pdf.GetPages())
    {
        var pageParagraphs = ExtractPageParagraphs(page);
        allParagraphs.AddRange(pageParagraphs);
    }

    return allParagraphs;
}

static List<string> ExtractPageParagraphs(Page page)
{
    var words = page.GetWords().ToList();
    if (words.Count == 0) return [];

    // ── Step 1: Sort words top-to-bottom, then left-to-right ──────
    // In PDF coordinate space, Y increases upward, so top of page = higher Y.
    words.Sort((a, b) =>
    {
        double yDiff = b.BoundingBox.Bottom - a.BoundingBox.Bottom;
        if (Math.Abs(yDiff) > 1.0) return yDiff.CompareTo(0) > 0 ? 1 : -1;
        return a.BoundingBox.Left.CompareTo(b.BoundingBox.Left);
    });

    // ── Step 2: Group words into lines ────────────────────────────
    var lineGroups = new List<List<Word>>();
    var currentLine = new List<Word> { words[0] };
    double currentLineY = words[0].BoundingBox.Bottom;

    for (int i = 1; i < words.Count; i++)
    {
        var   word     = words[i];
        double wordY   = word.BoundingBox.Bottom;
        double height  = Math.Max(word.BoundingBox.Height, currentLine[0].BoundingBox.Height);
        double lineTol = height * 0.45; // tolerance for same-line detection

        if (Math.Abs(wordY - currentLineY) <= lineTol)
        {
            currentLine.Add(word);
        }
        else
        {
            // Sort line words left-to-right before adding
            currentLine.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            lineGroups.Add(new List<Word>(currentLine));
            currentLine.Clear();
            currentLine.Add(word);
            currentLineY = wordY;
        }
    }
    // Add last line
    currentLine.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
    lineGroups.Add(currentLine);

    // Sort all lines top-to-bottom (descending Y)
    lineGroups.Sort((a, b) => b[0].BoundingBox.Bottom.CompareTo(a[0].BoundingBox.Bottom));

    // ── Step 3: Compute average line spacing ──────────────────────
    double totalGap = 0;
    int    gapCount = 0;
    for (int i = 1; i < lineGroups.Count; i++)
    {
        double y1  = lineGroups[i - 1].Min(w => w.BoundingBox.Bottom);
        double y2  = lineGroups[i].Max(w => w.BoundingBox.Top);
        double gap = y1 - y2; // gap between bottom of line above and top of line below
        if (gap >= 0) { totalGap += gap; gapCount++; }
    }
    double avgLineSpacing  = gapCount > 0 ? totalGap / gapCount : 4.0;
    double paragraphThresh = Math.Max(avgLineSpacing * 1.8, 8.0);

    // ── Step 4: Group lines into paragraphs ───────────────────────
    var paragraphs    = new List<string>();
    var currentParaLines = new List<string>();

    for (int i = 0; i < lineGroups.Count; i++)
    {
        string lineText = string.Join(" ", lineGroups[i].Select(w => w.Text));
        currentParaLines.Add(lineText);

        bool isLastLine = i == lineGroups.Count - 1;
        bool isParagraphBreak = false;

        if (!isLastLine)
        {
            double bottomOfCurrentLine = lineGroups[i].Min(w => w.BoundingBox.Bottom);
            double topOfNextLine       = lineGroups[i + 1].Max(w => w.BoundingBox.Top);
            double gap = bottomOfCurrentLine - topOfNextLine;
            isParagraphBreak = gap > paragraphThresh;
        }

        if (isLastLine || isParagraphBreak)
        {
            string para = string.Join(" ", currentParaLines).Trim();
            // Filter noise: skip page numbers and very short fragments
            if (para.Length >= 20 && !IsPageNumber(para))
                paragraphs.Add(para);
            currentParaLines.Clear();
        }
    }

    return paragraphs;
}

static bool IsPageNumber(string text)
{
    var trimmed = text.Trim();
    // Pure number or Roman numeral patterns
    return int.TryParse(trimmed, out _)
        || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[ivxlcdmIVXLCDM]{1,8}$");
}

// ═══════════════════════════════════════════════════════════════════
// TRANSLATION
// ═══════════════════════════════════════════════════════════════════

static async Task<string> TranslateWithRetryAsync(
    string text, HttpClient http, int maxRetries = 3, CancellationToken ct = default)
{
    // Google Translate URL limit ≈ 5000 chars — split long paragraphs
    const int MaxChunk = 4000;
    if (text.Length > MaxChunk)
    {
        var parts = SplitIntoChunks(text, MaxChunk);
        var translatedParts = new List<string>();
        foreach (var part in parts)
            translatedParts.Add(await TranslateWithRetryAsync(part, http, maxRetries, ct));
        return string.Join(" ", translatedParts);
    }

    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            string url = "https://translate.googleapis.com/translate_a/single"
                       + $"?client=gtx&sl=en&tl=vi&dt=t&q={Uri.EscapeDataString(text)}";

            string json = await http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            var sb = new StringBuilder();
            foreach (var chunk in doc.RootElement[0].EnumerateArray())
            {
                string? part = chunk[0].GetString();
                if (!string.IsNullOrEmpty(part)) sb.Append(part);
            }
            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (attempt == maxRetries)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine($"\n  ⚠️  Lỗi dịch sau {maxRetries} lần thử: {ex.Message}");
                Console.ResetColor();
                return "[Không thể dịch đoạn này]";
            }
            int delay = 1500 * attempt;
            await Task.Delay(delay, ct);
        }
    }
    return text;
}

static List<string> SplitIntoChunks(string text, int maxLength)
{
    var chunks = new List<string>();
    var sentences = text.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.None);
    var current = new StringBuilder();

    foreach (var sentence in sentences)
    {
        if (current.Length + sentence.Length > maxLength && current.Length > 0)
        {
            chunks.Add(current.ToString().Trim());
            current.Clear();
        }
        current.Append(sentence).Append(". ");
    }
    if (current.Length > 0) chunks.Add(current.ToString().Trim());
    return chunks;
}

// ═══════════════════════════════════════════════════════════════════
// PDF GENERATION
// ═══════════════════════════════════════════════════════════════════

static void GenerateBilingualPdf(
    string outputPath,
    List<(string English, string Vietnamese)> blocks,
    string docTitle)
{
    // Detect if NotoSans was registered
    string fontFamily = "Noto Sans"; // QuestPDF uses embedded font name from TTF

    Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginHorizontal(2.2f, Unit.Centimetre);
            page.MarginVertical(2.0f,  Unit.Centimetre);
            page.DefaultTextStyle(ts => ts
                .FontFamily(fontFamily)
                .FontSize(11)
                .LineHeight(1.45f));

            // ── Header ────────────────────────────────────────────
            page.Header()
                .PaddingBottom(8)
                .BorderBottom(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .Row(row =>
                {
                    row.RelativeItem()
                        .Text(docTitle)
                        .FontSize(9)
                        .FontColor(Colors.Grey.Darken2);

                    row.RelativeItem()
                        .AlignRight()
                        .Text(x =>
                        {
                            x.Span("Trang ").FontSize(9).FontColor(Colors.Grey.Darken2);
                            x.CurrentPageNumber().FontSize(9).Bold();
                            x.Span(" / ").FontSize(9).FontColor(Colors.Grey.Darken2);
                            x.TotalPages().FontSize(9).FontColor(Colors.Grey.Darken2);
                        });
                });

            // ── Content ───────────────────────────────────────────
            page.Content()
                .PaddingTop(14)
                .Column(col =>
                {
                    col.Spacing(14);

                    foreach (var (english, vietnamese) in blocks)
                    {
                        col.Item().Column(inner =>
                        {
                            // English paragraph
                            inner.Item()
                                .Text(english)
                                .FontSize(11)
                                .FontColor(Colors.Black);

                            // Vietnamese translation (if available)
                            if (!string.IsNullOrWhiteSpace(vietnamese) &&
                                !vietnamese.StartsWith("[Không thể dịch"))
                            {
                                inner.Item()
                                    .PaddingTop(5)
                                    .PaddingLeft(14)
                                    .BorderLeft(3f)
                                    .BorderColor("#90CAF9")
                                    .PaddingLeft(8)
                                    .Text(vietnamese)
                                    .FontSize(10.5f)
                                    .FontColor("#1565C0")
                                    .Italic();
                            }
                        });
                    }
                });

            // ── Footer ────────────────────────────────────────────
            page.Footer()
                .AlignCenter()
                .PaddingTop(6)
                .BorderTop(0.5f)
                .BorderColor(Colors.Grey.Lighten2)
                .Text($"BookTranslate • EN → VI • {DateTime.Now:dd/MM/yyyy HH:mm}")
                .FontSize(8)
                .FontColor(Colors.Grey.Medium);
        });
    }).GeneratePdf(outputPath);
}

// ═══════════════════════════════════════════════════════════════════
// FONT HELPERS
// ═══════════════════════════════════════════════════════════════════

static async Task EnsureFontAsync(string dir, string fileName, string url)
{
    string path = Path.Combine(dir, fileName);
    if (File.Exists(path) && new FileInfo(path).Length > 50_000)
        return; // already downloaded

    Console.Write($"⬇️  Đang tải font {fileName}... ");
    try
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BookTranslate/1.0");
        client.Timeout = TimeSpan.FromSeconds(60);

        var bytes = await client.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(path, bytes);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✅ ({bytes.Length / 1024} KB)");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"⚠️  Không tải được font ({ex.Message})");
        Console.WriteLine("   → App sẽ dùng font mặc định (chữ Việt có thể không đúng).");
        Console.ResetColor();
    }
}
