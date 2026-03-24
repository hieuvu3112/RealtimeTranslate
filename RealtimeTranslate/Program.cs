using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PortAudioSharp;
using Whisper.net;

// ── Cấu hình ────────────────────────────────────────────────────
const int SampleRate = 16000;


// ── VAD (Voice Activity Detection) ──────────────────────────────
const float VadEnergyThreshold  = 0.01f;            // ngưỡng RMS phát hiện giọng nói
const int   VadSilenceSamples   = SampleRate * 6 / 10; // 600ms im lặng → kết thúc câu
const int   VadMinSpeechSamples = SampleRate / 4;   // bỏ qua nếu < 250ms speech (giảm từ 500ms)
const int   VadPreBufferSamples = SampleRate / 5;   // 200ms trước câu (tránh cắt đầu chữ)
const int   VadMaxSpeechSamples = SampleRate * 29;  // cắt cứng tối đa ~29s
// ── 1. Đảm bảo model tồn tại và hợp lệ ─────────────────────────
// (modelPath sẽ được xác định sau khi chọn chế độ)

// ── Chọn chế độ ngôn ngữ ─────────────────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║           Chọn chế độ phiên dịch                ║");
Console.WriteLine("╠══════════════════════════════════════════════════╣");
Console.WriteLine("║  [1] 🇻🇳 Tiếng Việt → 🇬🇧 Tiếng Anh  (mặc định)║");
Console.WriteLine("║  [2] 🇬🇧 Tiếng Anh  → 🇻🇳 Tiếng Việt            ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.Write("Chọn (Enter = 1): ");
string modeInput = Console.ReadLine()?.Trim() ?? "1";
Console.WriteLine();

bool   isEnToVi = modeInput == "2";
string srcLang  = isEnToVi ? "en" : "vi";
string tgtLang  = isEnToVi ? "vi" : "en";
string srcFlag  = isEnToVi ? "🇬🇧" : "🇻🇳";
string tgtFlag  = isEnToVi ? "🇻🇳" : "🇬🇧";
string modeDesc = isEnToVi ? "Tiếng Anh → Tiếng Việt" : "Tiếng Việt → Tiếng Anh";
Console.WriteLine($"✅ Chế độ: {modeDesc}\n");

// Chọn model phù hợp:
//   vi→en: PhoWhisper-large (fine-tune tiếng Việt — tốt nhất cho tiếng Việt)
//   en→vi: ggml-small.bin   (Whisper tiêu chuẩn — tốt cho tiếng Anh)
string modelFileName = isEnToVi ? "ggml-small.bin" : "ggml-phowhisper-large.bin";
long   modelMinBytes = isEnToVi ? 100L  * 1024 * 1024   // small.bin ~244 MB
                                : 1_500L * 1024 * 1024;  // PhoWhisper-large >1.5 GB
string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, modelFileName);

Console.ForegroundColor = ConsoleColor.DarkGray;
Console.Error.WriteLine($"[Model] Sử dụng: {modelFileName}");
Console.ResetColor();

await EnsureModelAsync(modelPath, modelMinBytes);

// ── 2. Nạp model Whisper (tự tải lại nếu file bị hỏng) ──────────
Console.WriteLine("🔄 Đang nạp model Whisper...");
WhisperFactory factory;
try
{
    factory = WhisperFactory.FromPath(modelPath);
}
catch (Exception ex) when (ex is WhisperModelLoadException || ex.Message.Contains("Failed to load"))
{
    long sizeMb = new FileInfo(modelPath).Length / 1_048_576;
    Console.Error.WriteLine($"⚠️  Không thể nạp model (kích thước hiện tại: {sizeMb} MB). File có thể bị hỏng.");
    Console.Error.WriteLine("   Đang xóa và tải lại...");
    File.Delete(modelPath);
    await EnsureModelAsync(modelPath, modelMinBytes);
    factory = WhisperFactory.FromPath(modelPath);
}

using var _ = factory;
var processorBuilder = factory.CreateBuilder()
    .WithLanguage(srcLang)         // vi hoặc en tuỳ chế độ
    .WithNoSpeechThreshold(0.9f);
processorBuilder.WithBeamSearchSamplingStrategy();
using var processor  = processorBuilder.Build();
using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
Console.WriteLine("✅ Model đã sẵn sàng.\n");

// ── Diagnostic: test Whisper pipeline với silence ─────────────
if (Config.Debug)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Error.WriteLine("[Startup] Đang kiểm tra pipeline Whisper...");
    Console.ResetColor();
    var silenceTest = new float[SampleRate * 2];
    using var silenceWav = BuildWavStream(silenceTest, SampleRate);
    int testCount = 0;
    await foreach (var r in processor.ProcessAsync(silenceWav, CancellationToken.None))
    {
        testCount++;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Error.WriteLine($"[Startup] Whisper OK — test output: \"{r.Text.Trim()}\"");
        Console.ResetColor();
    }
    if (testCount == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Error.WriteLine("[Startup] ⚠ Whisper không trả output nào cho test silence (bình thường).");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Error.WriteLine($"[Startup] ✅ Whisper pipeline OK ({testCount} segment).");
        Console.ResetColor();
    }
}

// ── 2. Queue xử lý audio → Whisper (chạy nền) ───────────────────
var processQueue = new BlockingCollection<float[]>(boundedCapacity: 10);
using var cts    = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var processingTask = Task.Run(async () =>
{
    try
    {
        foreach (var segment in processQueue.GetConsumingEnumerable(cts.Token))
        {
            try
            {
                if (Config.Debug)
                {
                    float durationSec = segment.Length / (float)SampleRate;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Error.WriteLine($"\n[Whisper] ⏳ Đang nhận dạng {durationSec:F1}s audio...");
                    Console.ResetColor();
                }

                // Bước 1: Whisper transcribe — in từng phần ngay khi có kết quả
                var srcText     = new StringBuilder();
                bool srcStarted = false;
                using var wavStream = BuildWavStream(segment, SampleRate);

                await foreach (var result in processor.ProcessAsync(wavStream, cts.Token))
                {
                    string part = result.Text.Trim();
                    if (Config.Debug)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Error.WriteLine($"[Whisper] raw: \"{part}\"");
                        Console.ResetColor();
                    }

                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        if (!srcStarted)
                        {
                            // Bắt đầu dòng nguồn mới
                            Console.ForegroundColor = isEnToVi ? ConsoleColor.Cyan : ConsoleColor.DarkYellow;
                            Console.Write($"{srcFlag} ");
                            Console.ResetColor();
                            srcStarted = true;
                        }
                        // In ngay từng phần — tạo hiệu ứng in dần
                        Console.ForegroundColor = isEnToVi ? ConsoleColor.Cyan : ConsoleColor.DarkYellow;
                        Console.Write(part + " ");
                        Console.ResetColor();
                        Console.Out.Flush();
                        srcText.Append(part).Append(' ');
                    }
                }

                string sourceText = srcText.ToString().Trim();
                if (string.IsNullOrWhiteSpace(sourceText))
                {
                    if (srcStarted) Console.WriteLine();
                    if (Config.Debug)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Error.WriteLine("[Whisper] → (kết quả rỗng, bỏ qua)");
                        Console.ResetColor();
                    }
                    continue;
                }

                Console.WriteLine(); // kết thúc dòng nguồn

                // Bước 2: Dịch qua Google Translate
                string translated = await TranslateAsync(sourceText, srcLang, tgtLang, httpClient, cts.Token);

                // In bản dịch ngay dưới — không có dòng trống giữa các segment
                Console.ForegroundColor = isEnToVi ? ConsoleColor.DarkYellow : ConsoleColor.Cyan;
                Console.WriteLine($"{tgtFlag} {translated}");
                Console.ResetColor();
                Console.Out.Flush();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Lỗi xử lý] {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
            }
        }
    }
    catch (OperationCanceledException) { }
});

// ── 3. Thu âm từ microphone hoặc System Audio (ScreenCaptureKit) ─
const int SystemAudioCaptureRate = 48_000; // AudioCapture tool ghi ở 48 kHz

PortAudio.Initialize();
try
{
    var (deviceIndices, useSystemAudio) = SelectInputDevices();
    if (deviceIndices.Count == 0 && !useSystemAudio)
    {
        Console.Error.WriteLine("❌ Không có thiết bị nào được chọn.");
        cts.Cancel();
    }
    else
    {
        Console.WriteLine("   VAD: tự phát hiện giọng nói, dịch ngay khi ngừng nói...");
        Console.WriteLine("   Nhấn Ctrl+C để dừng.\n");

        var streams   = new List<PortAudioSharp.Stream>();
        var vadStates = new List<VadState>();
        var bgTasks   = new List<Task>();

        // ── System Audio via ScreenCaptureKit ────────────────────
        if (useSystemAudio)
        {
            string captureToolPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "AudioCapture");

            if (!File.Exists(captureToolPath))
            {
                string buildScript = Path.GetFullPath(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                 "../../../../build-audio-capture.sh"));
                Console.Error.WriteLine($"❌ Không tìm thấy AudioCapture binary.");
                Console.Error.WriteLine($"   Chạy: bash {buildScript}");
            }
            else
            {
                var vadState = new VadState();
                vadStates.Add(vadState);
                bgTasks.Add(CaptureSystemAudioAsync(
                    processQueue, vadState, captureToolPath,
                    SystemAudioCaptureRate, SampleRate, cts.Token));
            }
        }

        // ── PortAudio microphone streams ─────────────────────────
        foreach (int deviceIndex in deviceIndices)
        {
            var info     = PortAudio.GetDeviceInfo(deviceIndex);
            var vadState = new VadState();
            vadStates.Add(vadState);

            var inParams = new StreamParameters
            {
                device           = deviceIndex,
                channelCount     = 1,
                sampleFormat     = SampleFormat.Float32,
                suggestedLatency = info.defaultLowInputLatency
            };

            var capturedVad = vadState;
            var stream = new PortAudioSharp.Stream(
                inParams, outParams: null,
                sampleRate: SampleRate,
                framesPerBuffer: 1024u,
                streamFlags: StreamFlags.ClipOff,
                callback: (input, output, frameCount, ref timeInfo, statusFlags, userData) =>
                {
                    if (input == IntPtr.Zero) return StreamCallbackResult.Continue;
                    var samples = new float[(int)frameCount];
                    Marshal.Copy(input, samples, 0, (int)frameCount);
                    ProcessVad(samples, capturedVad, processQueue);
                    return StreamCallbackResult.Continue;
                },
                userData: IntPtr.Zero
            );
            streams.Add(stream);
            stream.Start();
            Console.WriteLine($"▶ Đang nghe: [{deviceIndex}] {info.name}");
        }
        Console.WriteLine();

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (TaskCanceledException) { }

        foreach (var s in streams) s.Stop();

        // Xử lý phần speech còn dở khi dừng
        foreach (var vs in vadStates)
        {
            float[]? remaining;
            lock (vs.SpeechBuffer)
                remaining = vs.SpeechBuffer.Count > VadMinSpeechSamples
                    ? vs.SpeechBuffer.ToArray() : null;
            if (remaining != null) processQueue.TryAdd(remaining);
        }

        foreach (var s in streams) s.Dispose();
        // bg tasks (AudioCapture process) đã được cancel qua cts
        await Task.WhenAll(bgTasks);
    }
}
finally
{
    processQueue.CompleteAdding();
    await processingTask;
    PortAudio.Terminate();
    Console.WriteLine("\n✅ Đã dừng.");
}

// ── Helper: bắt system audio từ AudioCapture (ScreenCaptureKit) ─
static async Task CaptureSystemAudioAsync(
    BlockingCollection<float[]> queue,
    VadState vadState,
    string captureToolPath,
    int inputRate,
    int outputRate,
    CancellationToken ct)
{
    using var process = new System.Diagnostics.Process();
    process.StartInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName             = captureToolPath,
        UseShellExecute      = false,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        CreateNoWindow       = true,
    };
    process.ErrorDataReceived += (_, e) =>
    {
        if (e.Data is null) return;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Error.WriteLine($"[AudioCapture] {e.Data}");
        Console.ResetColor();
    };

    process.Start();
    process.BeginErrorReadLine();
    Console.WriteLine("▶ Đang nghe: System Audio (ScreenCaptureKit — không cần BlackHole)");
    Console.WriteLine("   (Xem log [AudioCapture] phía trên để biết trạng thái quyền)");

    var rawStream   = process.StandardOutput.BaseStream;
    // 100ms worth of float32 samples at inputRate
    int chunkFrames = inputRate / 10;
    var readBuf     = new byte[chunkFrames * 4 + 3]; // +3 để buffer phần dư
    var remainder   = new byte[3];
    int remLen      = 0;

    // Cảnh báo nếu không nhận data trong 5 giây đầu
    bool firstDataReceived = false;
    Task.Run(async () =>
    {
        await Task.Delay(5000).ConfigureAwait(false);
        if (!firstDataReceived && !ct.IsCancellationRequested)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine("\n⚠️  AudioCapture không gửi data sau 5 giây!");
            Console.Error.WriteLine("   Kiểm tra log [AudioCapture] ở trên để biết lý do.");
            Console.Error.WriteLine("   Nếu thấy lỗi quyền, hãy:");
            Console.Error.WriteLine($"   1) Chạy thủ công: {captureToolPath}");
            Console.Error.WriteLine("   2) Cấp quyền trong System Settings → Privacy & Security");
            Console.Error.WriteLine("      → Screen & System Audio Recording → Bật AudioCapture");
            Console.Error.WriteLine("   3) Khởi động lại ứng dụng này.");
            Console.ResetColor();
        }
    });

    try
    {
        while (!ct.IsCancellationRequested)
        {
            // Đặt phần dư của lần trước vào đầu buffer
            if (remLen > 0)
                Buffer.BlockCopy(remainder, 0, readBuf, 0, remLen);

            int bytesRead = await rawStream.ReadAsync(readBuf.AsMemory(remLen), ct);
            if (bytesRead == 0) break;
            if (!firstDataReceived)
            {
                firstDataReceived = true;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Error.WriteLine("[AudioCapture] ✅ Đang nhận audio data từ ScreenCaptureKit...");
                Console.ResetColor();
            }

            int totalBytes = remLen + bytesRead;
            int floatCount = totalBytes / 4;
            remLen         = totalBytes % 4;

            if (remLen > 0)
                Buffer.BlockCopy(readBuf, floatCount * 4, remainder, 0, remLen);

            if (floatCount == 0) continue;

            var samples = new float[floatCount];
            Buffer.BlockCopy(readBuf, 0, samples, 0, floatCount * 4);

            // Resample inputRate → outputRate (nearest-neighbour, đủ nhanh cho realtime)
            var resampled = ResampleNearest(samples, inputRate, outputRate);
            ProcessVad(resampled, vadState, queue);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    }
}

// ── Helper: resample nearest-neighbour ───────────────────────────
static float[] ResampleNearest(float[] input, int fromRate, int toRate)
{
    if (fromRate == toRate) return input;
    double ratio  = (double)fromRate / toRate;
    int    outLen = (int)(input.Length / ratio);
    var    output = new float[outLen];
    for (int i = 0; i < outLen; i++)
        output[i] = input[(int)(i * ratio)];
    return output;
}

// ── Helper: tạo WAV in-memory từ mảng float PCM 16-bit ──────────
static MemoryStream BuildWavStream(float[] samples, int sampleRate)
{
    var ms = new MemoryStream();
    var w  = new BinaryWriter(ms, System.Text.Encoding.Latin1, leaveOpen: true);

    int dataBytes = samples.Length * sizeof(short);

    // RIFF header
    w.Write((byte)'R'); w.Write((byte)'I'); w.Write((byte)'F'); w.Write((byte)'F');
    w.Write(36 + dataBytes);
    w.Write((byte)'W'); w.Write((byte)'A'); w.Write((byte)'V'); w.Write((byte)'E');
    // fmt chunk
    w.Write((byte)'f'); w.Write((byte)'m'); w.Write((byte)'t'); w.Write((byte)' ');
    w.Write(16);               // SubchunkSize
    w.Write((short)1);         // PCM
    w.Write((short)1);         // Mono
    w.Write(sampleRate);
    w.Write(sampleRate * 2);   // ByteRate
    w.Write((short)2);         // BlockAlign
    w.Write((short)16);        // BitsPerSample
    // data chunk
    w.Write((byte)'d'); w.Write((byte)'a'); w.Write((byte)'t'); w.Write((byte)'a');
    w.Write(dataBytes);

    foreach (var s in samples)
        w.Write((short)Math.Clamp((int)(s * 32767f), short.MinValue, short.MaxValue));

    w.Flush();
    ms.Position = 0;
    return ms;
}

// ── Helper: đảm bảo model GGML tồn tại ──────────────────────────
static async Task EnsureModelAsync(string modelPath, long modelMinBytes = 1_500L * 1024 * 1024)
{
    if (File.Exists(modelPath))
    {
        long size = new FileInfo(modelPath).Length;
        if (size >= modelMinBytes)
            return; // file hợp lệ

        Console.Error.WriteLine($"⚠️  File model quá nhỏ ({size / 1024.0 / 1024:F0} MB) — có thể là placeholder.");
        Console.Error.WriteLine("   Đang xóa...");
        File.Delete(modelPath);
    }

    string fileName = Path.GetFileName(modelPath);

    // ggml-small.bin: tải thẳng từ Hugging Face (không cần convert)
    if (fileName == "ggml-small.bin")
    {
        Console.WriteLine($"📥 Đang tải {fileName} từ Hugging Face (~244 MB)...");
        string url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        using var resp   = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var src  = await resp.Content.ReadAsStreamAsync();
        await using var dest = File.Create(modelPath);
        var   buf       = new byte[81920];
        long  downloaded = 0;
        int   n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, n));
            downloaded += n;
            if (total.HasValue)
            {
                int pct = (int)(downloaded * 100 / total.Value);
                Console.Write($"\r   {pct}% ({downloaded / 1024 / 1024} MB / {total.Value / 1024 / 1024} MB)   ");
            }
        }
        Console.WriteLine("\n✅ Tải xong!");
        return;
    }

    // PhoWhisper và các model khác: cần convert thủ công
    string scriptPath = Path.GetFullPath(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../convert-phowhisper.sh"));

    Console.Error.WriteLine($"""
        ❌ Chưa có file model GGML: {fileName}

        vinai/PhoWhisper-large cần được convert sang GGML trước khi dùng.
        Chạy script sau (yêu cầu Python 3 + ~10 GB dung lượng trống):

            bash {scriptPath}

        Script sẽ tự động:
          1. Cài torch + transformers
          2. Clone whisper.cpp
          3. Download PhoWhisper-large từ HuggingFace (~3 GB)
          4. Convert sang GGML → lưu vào thư mục bin/

        Sau khi script chạy xong, khởi động lại ứng dụng.
        """);

    throw new FileNotFoundException("Model GGML chưa được tạo. Xem hướng dẫn ở trên.");
}

// ── Helper: dịch tiếng Việt → tiếng Anh (Google Translate) ──────
static async Task<string> TranslateAsync(
    string text, string srcLang, string tgtLang,
    HttpClient http, CancellationToken ct = default)
{
    // Google Translate unofficial endpoint — không cần API key
    string url = "https://translate.googleapis.com/translate_a/single"
               + $"?client=gtx&sl={srcLang}&tl={tgtLang}&dt=t&q={Uri.EscapeDataString(text)}";
    try
    {
        string json = await http.GetStringAsync(url, ct);
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
    catch (Exception ex)
    {
        return $"[Translation error: {ex.Message}]";
    }
}


// ── Helper: liệt kê & chọn thiết bị audio input ─────────────────
// Trả về: (danh sách PortAudio device index, dùng ScreenCaptureKit hay không)
static (List<int> devices, bool useSystemAudio) SelectInputDevices()
{
    var mics      = new List<(int paIndex, string name)>();
    var loopbacks = new List<(int paIndex, string name)>();

    for (int i = 0; i < PortAudio.DeviceCount; i++)
    {
        var info = PortAudio.GetDeviceInfo(i);
        if (info.maxInputChannels <= 0) continue;

        bool isLoopback = info.name.Contains("BlackHole",    StringComparison.OrdinalIgnoreCase)
                       || info.name.Contains("Loopback",     StringComparison.OrdinalIgnoreCase)
                       || info.name.Contains("Soundflower",  StringComparison.OrdinalIgnoreCase)
                       || info.name.Contains("Multi-Output", StringComparison.OrdinalIgnoreCase);

        if (isLoopback) loopbacks.Add((i, info.name));
        else            mics.Add((i, info.name));
    }

    string captureToolPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AudioCapture");
    bool   hasSCKit        = File.Exists(captureToolPath);

    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║          Chọn nguồn âm thanh đầu vào            ║");
    Console.WriteLine("╠══════════════════════════════════════════════════╣");

    var all = new List<(int paIndex, string name)>();

    Console.WriteLine("║  🎤 MICROPHONE                                   ║");
    foreach (var d in mics)
    {
        all.Add((d.paIndex, d.name));
        bool   isDefault = d.paIndex == PortAudio.DefaultInputDevice;
        string label     = $"  [{all.Count - 1}] {d.name}";
        string tag       = isDefault ? " ← mặc định" : "";
        Console.WriteLine($"║  {label,-44}{tag,-6}║");
    }

    Console.WriteLine("║                                                  ║");
    Console.WriteLine("║  🔊 SYSTEM AUDIO (Loa / Tai nghe)                ║");

    if (hasSCKit)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("║  [s] System Audio — ScreenCaptureKit (macOS 13+)║");
        Console.WriteLine("║      Không cần BlackHole hay driver ảo           ║");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("║  [s] System Audio — ScreenCaptureKit (macOS 13+)║");
        Console.WriteLine("║      ⚠  Chưa build: bash build-audio-capture.sh ║");
        Console.ResetColor();
    }

    if (loopbacks.Count > 0)
    {
        Console.WriteLine("║  ─── hoặc dùng loopback (BlackHole, ...) ───     ║");
        foreach (var d in loopbacks)
        {
            all.Add((d.paIndex, d.name));
            Console.WriteLine($"║    [{all.Count - 1}] {d.name,-45}║");
        }
    }

    Console.WriteLine("╠══════════════════════════════════════════════════╣");
    Console.WriteLine("║  m = mic mặc định  |  s = system audio           ║");
    Console.WriteLine("║  số đơn: 0         |  nhiều: 0,1                 ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.Write("Chọn: ");

    string raw = Console.ReadLine()?.Trim().ToLower() ?? "";
    Console.WriteLine();

    if (raw == "" || raw == "m")
        return (new List<int> { PortAudio.DefaultInputDevice }, false);

    if (raw == "s")
    {
        if (!hasSCKit)
        {
            Console.Error.WriteLine("⚠  Chưa build AudioCapture. Chạy: bash build-audio-capture.sh");
            return (new List<int> { PortAudio.DefaultInputDevice }, false);
        }
        return (new List<int>(), true);
    }

    var result = new List<int>();
    foreach (var part in raw.Split(','))
    {
        if (int.TryParse(part.Trim(), out int idx)
            && idx >= 0 && idx < all.Count
            && !result.Contains(all[idx].paIndex))
            result.Add(all[idx].paIndex);
    }
    return result.Count > 0
        ? (result, false)
        : (new List<int> { PortAudio.DefaultInputDevice }, false);
}

// ── Helper: xử lý VAD cho một chunk audio ────────────────────────
static void ProcessVad(float[] samples, VadState state, BlockingCollection<float[]> queue)
{
    float rms = 0f;
    foreach (var s in samples) rms += s * s;
    rms = MathF.Sqrt(rms / samples.Length);
    bool hasSpeech = rms > VadEnergyThreshold;

    lock (state.SpeechBuffer)
    {
        foreach (var s in samples)
        {
            state.PreBuffer.Enqueue(s);
            if (state.PreBuffer.Count > VadPreBufferSamples)
                state.PreBuffer.Dequeue();
        }

        if (hasSpeech)
        {
            if (!state.IsSpeaking)
            {
                state.IsSpeaking = true;
                state.SpeechBuffer.AddRange(state.PreBuffer);
                if (Config.Debug)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"🎙[rms:{rms:F3}] ");
                    Console.ResetColor();
                    Console.Out.Flush();
                }
            }
            state.SpeechBuffer.AddRange(samples);
            state.SilenceCount = 0;
        }
        else if (state.IsSpeaking)
        {
            state.SpeechBuffer.AddRange(samples);
            state.SilenceCount += samples.Length;

            bool endOfUtterance = state.SilenceCount >= VadSilenceSamples
                                  && state.SpeechBuffer.Count >= VadMinSpeechSamples;
            bool maxReached     = state.SpeechBuffer.Count >= VadMaxSpeechSamples;

            if (endOfUtterance || maxReached)
            {
                float[] clip    = state.SpeechBuffer.ToArray();
                float   clipSec = clip.Length / (float)SampleRate;
                state.SpeechBuffer.Clear();
                state.IsSpeaking   = false;
                state.SilenceCount = 0;

                if (Config.Debug)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Error.WriteLine($"\n[VAD] ✂ Đã cắt {clipSec:F1}s — đưa vào queue (size={queue.Count})");
                    Console.ResetColor();
                }

                // ── Dump WAV cho lần đầu tiên để debug ───────────
                if (state.DebugDumpCount < 2)
                {
                    state.DebugDumpCount++;
                    string wavPath = $"/tmp/debug_segment_{state.DebugDumpCount}.wav";
                    try
                    {
                        using var dumpWav = BuildWavStream(clip, SampleRate);
                        File.WriteAllBytes(wavPath, dumpWav.ToArray());
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Error.WriteLine($"[VAD] 💾 Đã lưu segment #{state.DebugDumpCount} → {wavPath}");
                        Console.Error.WriteLine($"      Kiểm tra bằng: afplay {wavPath}");
                        Console.ResetColor();
                    }
                    catch { /* ignore */ }
                }

                if (!queue.TryAdd(clip))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine("[VAD] ⚠ Queue đầy, bỏ qua segment này.");
                    Console.ResetColor();
                }
            }
        }
        else
        {
            // Log RMS mỗi ~3s khi im lặng để confirm audio đang chạy
            if (Config.Debug)
            {
                state.SilenceLogCount += samples.Length;
                if (state.SilenceLogCount >= SampleRate * 3)
                {
                    state.SilenceLogCount = 0;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Error.Write($"[VAD] rms={rms:F4} ");
                    Console.ResetColor();
                    Console.Out.Flush();
                }
            }
        }
    }
}

// ── VAD state — một instance per thiết bị ────────────────────────
sealed class VadState
{
    public readonly List<float>  SpeechBuffer = new();
    public readonly Queue<float> PreBuffer    = new();
    public int  SilenceCount    = 0;
    public int  SilenceLogCount = 0;
    public int  DebugDumpCount  = 0;
    public bool IsSpeaking      = false;
}

// ── Cấu hình runtime (bật debug bằng env: TRANSLATE_DEBUG=1) ─────
static class Config
{
    public static readonly bool Debug = Environment.GetEnvironmentVariable("TRANSLATE_DEBUG") == "1";
}

