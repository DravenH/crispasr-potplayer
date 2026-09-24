// whisper-faster.exe shim -> CrispASR (parakeet-tdt-0.6b-ja, CUDA)
// Lets PotPlayer "声音生成字幕" use CrispASR by mimicking the Purfview engine CLI:
// <audio> --model --model_dir --language --output_format srt --output_dir --compute_type --beep_off
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

static class XxlShim
{
    static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string IniPath = Path.Combine(ExeDir, "shim.ini");
    static string LogPath = Path.Combine(Path.GetTempPath(), "crispasr-xxl-shim.log");
    static bool LogEnabled = true;

    static readonly HashSet<string> ValuedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--model","--model_dir","--device","--compute_type","--output_format","--output_dir",
        "--language","--temperature","--best_of","--beam_size","--patience","--length_penalty",
        "--repetition_penalty","--no_repeat_ngram_size","--suppress_blank","--suppress_tokens",
        "--initial_prompt","--prefix","--prompt_reset_on_temperature","--max_initial_timestamp",
        "--temperature_increment_on_fallback","--compression_ratio_threshold","--logprob_threshold",
        "--no_speech_threshold","--hallucination_silence_threshold","--clip_timestamps",
        "--max_new_tokens","--chunk_length","--hotwords","--batch_size","--vad_filter",
        "--vad_threshold","--vad_min_speech_duration_ms","--vad_max_speech_duration_s",
        "--vad_min_silence_duration_ms","--vad_speech_pad_ms","--vad_window_size_samples",
        "--vad_method","--vad_device","--mdx_chunk","--voc_device","--diarize","--diarize_device",
        "--diarize_threads","--speaker","--max_comma","--max_gap","--max_line_width",
        "--max_line_count","--reprompt","--threads","--min_dist_to_end","--japanese","--one_word"
    };

    static readonly string[] AudioExts = { ".wav", ".m4a", ".mp3", ".opus", ".ogg", ".flac", ".aac", ".wma" };

    static void Log(string msg)
    {
        if (!LogEnabled) return;
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine, Encoding.UTF8); } catch { }
    }

    static string Ini(string key, string def)
    {
        try
        {
            foreach (var line in File.ReadAllLines(IniPath, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i > 0 && line.Substring(0, i).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(i + 1).Trim();
            }
        }
        catch { }
        return def;
    }

    // first existing path among candidates; last one as the error-message fallback
    static string PickExisting(params string[] cands)
    {
        foreach (var c in cands)
            if (File.Exists(c)) return c;
        return cands[cands.Length - 1];
    }

    static readonly Dictionary<string, string> LangMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        {"Japanese","ja"},{"日本語","ja"},
        {"English","en"},{"Chinese","zh"},{"中文","zh"},{"Cantonese","yue"},
        {"Korean","ko"},{"French","fr"},{"German","de"},{"Spanish","es"},{"Portuguese","pt"},
        {"Italian","it"},{"Russian","ru"},{"Thai","th"},{"Vietnamese","vi"},{"Arabic","ar"},
        {"Hindi","hi"},{"Indonesian","id"},{"Turkish","tr"},{"Polish","pl"},{"Dutch","nl"},
        {"Swedish","sv"},{"Norwegian","no"},{"Danish","da"},{"Finnish","fi"},{"Czech","cs"},
        {"Greek","el"},{"Hebrew","he"},{"Hungarian","hu"},{"Romanian","ro"},{"Ukrainian","uk"},
        {"Mandarin","zh"},{"Catalan","ca"},{"Bulgarian","bg"},{"Croatian","hr"},{"Serbian","sr"},
        {"Slovak","sk"},{"Slovenian","sl"},{"Latvian","lv"},{"Lithuanian","lt"},{"Estonian","et"}
    };

    static string NormLang(string s)
    {
        if (string.IsNullOrEmpty(s) || s == "auto") return null;
        if (s.Length <= 3) return s; // 已是 ISO 码 (ja/zh/yue...)
        string v;
        return LangMap.TryGetValue(s, out v) ? v : "ja"; // 未知全名兜底日语（模型本身仅支持 ja）
    }

    static bool IsDigits(string s)
    {
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return s.Length > 0;
    }

    static string Get(Dictionary<string, List<string>> opts, string f)
    {
        List<string> v;
        return opts.TryGetValue(f, out v) && v.Count > 0 ? v[0] : null;
    }

    // PotPlayer may still be writing the dumped audio when it starts the engine;
    // a truncated WAV makes both crispasr decoders fail. Wait until size settles.
    static void WaitForStableInput(string path)
    {
        long prev = -1;
        int stable = 0, waited = 0;
        while (waited < 300000)
        {
            long cur;
            try { cur = new FileInfo(path).Length; } catch { cur = -1; }
            if (cur > 0 && cur == prev) stable++; else stable = 0;
            prev = cur;
            if (stable >= 2) return;
            Thread.Sleep(400);
            waited += 400;
        }
        Log("warn: input size still changing after 300s, proceeding anyway");
    }

    // true when we can open the file for reading alongside any current holder
    static bool TryOpenReadable(string path, out string err)
    {
        err = null;
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(-16, SeekOrigin.End);
                fs.Read(new byte[16], 0, 16);
            }
            return true;
        }
        catch (Exception e) { err = e.GetType().Name + ": " + e.Message; return false; }
    }

    static bool CopyShared(string src, string dst, out string err)
    {
        err = null;
        try
        {
            using (var si = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var so = new FileStream(dst, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[1 << 20];
                int n;
                while ((n = si.Read(buf, 0, buf.Length)) > 0) so.Write(buf, 0, n);
            }
            return true;
        }
        catch (Exception e) { err = e.GetType().Name + ": " + e.Message; try { File.Delete(dst); } catch { } return false; }
    }

    // an ini-supplied path can hold characters Path.Combine rejects ('|', a stray tab
    // from copy-pasting); that must read as "not here", never as an engine crash
    static string TryCombine(string dir, string file)
    {
        try { return Path.Combine(dir, file); } catch { return null; }
    }

    static string FindFfmpeg(string ffmpegDir)
    {
        if (!string.IsNullOrEmpty(ffmpegDir))
        {
            string p = TryCombine(ffmpegDir, "ffmpeg.exe");
            if (p != null && File.Exists(p)) return p;
        }
        string local = Path.Combine(ExeDir, @"ffmpeg\ffmpeg.exe");
        if (File.Exists(local)) return local;
        string envDir = Ini("ffmpeg_dir", "");
        if (envDir.Length > 0)
        {
            local = TryCombine(envDir, "ffmpeg.exe");
            if (local != null && File.Exists(local)) return local;
        }
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (d.Length == 0) continue;
            string p = TryCombine(d.Trim(), "ffmpeg.exe");
            if (p != null && File.Exists(p)) return p;
        }
        return null;
    }

    // Run a helper process to completion, capturing output for the log.
    static int RunCapture(string exe, string args, int ms, out string err)
    {
        err = null;
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                var sb = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.Append(e.Data).Append(' '); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.Append(e.Data).Append(' '); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(ms))
                {
                    try { p.Kill(); } catch { }
                    err = "timeout after " + ms + " ms";
                    return -1;
                }
                p.WaitForExit();
                err = sb.ToString().Trim();
                if (err.Length > 400) err = err.Substring(err.Length - 400);
                return p.ExitCode;
            }
        }
        catch (Exception e) { err = e.GetType().Name + ": " + e.Message; return -1; }
    }

    // Optional pre-pass: crispasr's own --separate (mel-band-roformer) writes
    // <input>_<stem>.wav. It expects 44.1 kHz stereo, which PotPlayer's dump
    // usually is not, so resample first. Returns null to mean "transcribe as-is";
    // workDir is then always safe to delete.
    static string SeparateVoices(string crispasr, string sepModel, string ffmpeg, string input,
                                 bool useGpu, out string workDir)
    {
        workDir = Path.Combine(Path.GetTempPath(), "crispasr-voc-" + DateTime.Now.ToString("HHmmss"));
        string full = Path.Combine(workDir, "src441.wav");
        string outDir = Path.Combine(workDir, "stems");
        try
        {
            Directory.CreateDirectory(outDir);
            string err;
            if (RunCapture(ffmpeg, "-y -hide_banner -loglevel error -i \"" + input +
                           "\" -vn -ac 2 -ar 44100 -c:a pcm_s16le \"" + full + "\"", 600000, out err) != 0)
            {
                Log("vocals: ffmpeg resample failed: " + err);
                return null;
            }
            string sep = string.Format("--separate -m \"{0}\" -f \"{1}\" --stems vocals --sep-output-dir \"{2}\"",
                                       sepModel, full, outDir);
            if (!useGpu) sep += " --no-gpu";
            Log("crispasr separate cmd: " + sep);
            if (RunCapture(crispasr, sep, 3600000, out err) != 0)
                Log("vocals: separate did not exit cleanly: " + err);
            string vocal = Path.Combine(outDir, Path.GetFileNameWithoutExtension(full) + "_vocals.wav");
            if (!File.Exists(vocal) || new FileInfo(vocal).Length < 100000)
            {
                Log("vocals: expected output missing or tiny: " + vocal);
                return null;
            }
            Log("vocals: separated -> " + vocal + " (" + new FileInfo(vocal).Length + " bytes)");
            return vocal;
        }
        catch (Exception e) { Log("vocals: failed: " + e.Message); return null; }
    }

    static int RunCrisp(string crispasr, string model, string input, string lang, string of,
                        string extra, bool useGpu, string partPath, string ffmpegDir)
    {
        var cmd = new StringBuilder();
        cmd.Append(string.Format("-m \"{0}\" -f \"{1}\" -l {2} --vad -osrt -of \"{3}\"", model, input, lang, of));
        if (!string.IsNullOrEmpty(extra)) cmd.Append(" " + extra);
        if (!useGpu) cmd.Append(" --no-gpu");
        Log("crispasr cmd: " + cmd);

        var psi = new ProcessStartInfo(crispasr, cmd.ToString())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        try
        {
            if (!string.IsNullOrEmpty(ffmpegDir) && Directory.Exists(ffmpegDir))
                psi.EnvironmentVariables["PATH"] = psi.EnvironmentVariables["PATH"] + ";" + ffmpegDir;
        }
        catch { }

        using (var proc = Process.Start(psi))
        {
            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Console.Error.WriteLine(e.Data);
            };
            proc.BeginErrorReadLine();

            // crispasr long/streaming path prints SRT blocks on stdout; capture & flush per entry
            using (var w = new StreamWriter(partPath, false, new UTF8Encoding(false)))
            {
                int expect = 0; // 0=index 1=timing 2=text-until-blank
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    Console.Out.WriteLine(line);
                    Console.Out.Flush();
                    string t = line.Trim();
                    if (expect == 0 && t.Length > 0 && t.Length < 8 && IsDigits(t))
                    { w.WriteLine(line); expect = 1; }
                    else if (expect == 1 && line.Contains("-->"))
                    { w.WriteLine(line); expect = 2; }
                    else if (expect == 2)
                    {
                        w.WriteLine(line);
                        if (t.Length == 0) { w.Flush(); expect = 0; }
                    }
                }
            }
            proc.WaitForExit();
            return proc.ExitCode;
        }
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log("=== invoked: " + string.Join(" ", args));

        var opts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        string curFlag = null;

        foreach (var a in args)
        {
            if (a.StartsWith("--"))
            {
                curFlag = a;
                if (!opts.ContainsKey(curFlag)) opts[curFlag] = new List<string>();
            }
            else if (curFlag != null && ValuedFlags.Contains(curFlag))
            {
                opts[curFlag].Add(a);
                // most options take one value; --output_format is nargs='+'
                if (!curFlag.Equals("--output_format", StringComparison.OrdinalIgnoreCase))
                    curFlag = null;
            }
            else
            {
                positional.Add(a);
                curFlag = null;
            }
        }

        // defaults: next to the shim exe, or under .\CrispASR\ (installer layout);
        // an explicit shim.ini value wins as long as the file it names is there
        LogEnabled = Ini("log", "1") != "0";
        string crispasr = Ini("crispasr", "");
        if (crispasr.Length == 0)
            crispasr = PickExisting(
                Path.Combine(ExeDir, @"CrispASR\crispasr.exe"),
                Path.Combine(ExeDir, "crispasr.exe"));
        string model = Ini("model", "");
        // a configured path that no longer exists (deleted or renamed model file) is a
        // mistake rather than a preference: search again, and say so in the log
        if (model.Length > 0 && !File.Exists(model))
        {
            Log("shim: configured model missing, searching again: " + model);
            model = "";
        }
        if (model.Length == 0)
            model = PickExisting(
                Path.Combine(ExeDir, @"CrispASR\models\parakeet-tdt-0.6b-ja-q8_0.gguf"),
                Path.Combine(ExeDir, @"CrispASR\models\parakeet-tdt-0.6b-ja.gguf"),
                Path.Combine(ExeDir, @"models\parakeet-tdt-0.6b-ja-q8_0.gguf"),
                Path.Combine(ExeDir, @"models\parakeet-tdt-0.6b-ja.gguf"));
        string extra    = Ini("extra",    "--split-on-punct --flush-after 1");
        bool useGpu     = Ini("nogpu", "0") != "1";
        bool vocals     = Ini("vocals", "0") == "1";
        string sepModel = Ini("separation_model", "");
        if (sepModel.Length > 0 && !File.Exists(sepModel))
        {
            Log("shim: configured separation_model missing, searching again: " + sepModel);
            sepModel = "";
        }
        if (sepModel.Length == 0)
            sepModel = PickExisting(
                Path.Combine(ExeDir, @"CrispASR\models\mel-band-roformer-vocals-f16.gguf"),
                Path.Combine(ExeDir, @"models\mel-band-roformer-vocals-f16.gguf"));
        string ffmpegDir = Ini("ffmpeg_dir", "");
        if (ffmpegDir.Length == 0 && File.Exists(Path.Combine(ExeDir, @"ffmpeg\ffmpeg.exe")))
            ffmpegDir = Path.Combine(ExeDir, "ffmpeg");

        if (!File.Exists(crispasr)) { Console.Error.WriteLine("shim: crispasr not found: " + crispasr); return 2; }
        // not a hard stop: crispasr may still resolve something we cannot see locally
        if (!File.Exists(model))
            Log("shim: no local model file at " + model
                + " ; put the .gguf under " + Path.Combine(ExeDir, @"CrispASR\models")
                + " or set 'model' in shim.ini");

        string input = null;
        foreach (var p in positional)
        {
            foreach (var e in AudioExts)
                if (p.EndsWith(e, StringComparison.OrdinalIgnoreCase) && File.Exists(p)) { input = p; break; }
            if (input != null) break;
        }
        if (input == null)
            foreach (var p in positional)
                if (File.Exists(p) && !p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) input = p;
        if (input == null)
        {
            // PotPlayer may spawn us before the dumped .tmp exists at all
            foreach (var p in positional)
            {
                if (p.StartsWith("--") || p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                int waited = 0;
                while (!File.Exists(p) && waited < 30000) { Thread.Sleep(500); waited += 500; }
                if (File.Exists(p)) { input = p; break; }
            }
        }
        if (input == null) { Console.Error.WriteLine("shim: no input audio file in command line"); return 2; }
        WaitForStableInput(input);

        string lang = Get(opts, "--language");
        lang = NormLang(lang);
        if (string.IsNullOrEmpty(lang)) lang = Ini("language", "ja");

        string outDir = Get(opts, "--output_dir");
        if (string.IsNullOrEmpty(outDir)) outDir = Path.GetDirectoryName(Path.GetFullPath(input));
        string baseNoExt = Path.GetFileNameWithoutExtension(input);
        string targetSrt = Path.Combine(outDir, baseNoExt + ".srt");

        try { Directory.CreateDirectory(outDir); } catch { }
        string of = Path.Combine(outDir, baseNoExt);
        string partPath = targetSrt + ".part";

        // PotPlayer can keep its freshly dumped file open (sharing violation /
        // oplock / AV scan) while the engine runs, which breaks crispasr's own
        // open even though the bytes are complete. Feed crispasr a private .wav
        // copy when we can read it; fall back to the original path otherwise.
        string effInput = input;
        string copyPath = Path.Combine(Path.GetTempPath(),
            "crispasr-in-" + DateTime.Now.ToString("HHmmss") + "-" + baseNoExt + ".wav");
        string err;
        if (!TryOpenReadable(input, out err))
        {
            Log("input not openable yet (" + err + "), waiting up to 120s");
            int waited = 0;
            while (!TryOpenReadable(input, out err) && waited < 120000) { Thread.Sleep(1000); waited += 1000; }
            if (err != null && waited >= 120000) Log("still not openable after 120s: " + err);
        }
        bool copied = false;
        if (CopyShared(input, copyPath, out err))
        {
            effInput = copyPath;
            copied = true;
            Log("using private .wav copy: " + copyPath);
        }
        else
        {
            Log("direct input, copy unavailable (" + err + ")");
        }

        // opt-in noise-robustness pre-pass; falls back to the raw audio on any failure
        string vocalWav = null, vocalDir = null;
        if (vocals)
        {
            if (!File.Exists(sepModel)) Log("vocals=1 but separation model not found: " + sepModel);
            else
            {
                string ff = FindFfmpeg(ffmpegDir);
                if (ff == null) Log("vocals=1 but no ffmpeg on PATH/in ffmpeg_dir; skipping separation");
                else vocalWav = SeparateVoices(crispasr, sepModel, ff, effInput, useGpu, out vocalDir);
            }
        }

        int code = RunCrisp(crispasr, model, vocalWav != null ? vocalWav : effInput,
                            lang, of, extra, useGpu, partPath, ffmpegDir);

        bool haveOutput =
            (File.Exists(targetSrt) && new FileInfo(targetSrt).Length > 0)
            || (File.Exists(of + ".srt") && new FileInfo(of + ".srt").Length > 0)
            || (File.Exists(partPath) && new FileInfo(partPath).Length > 0);
        if (!haveOutput && (copied || vocalWav != null))
        {
            Log("first attempt produced nothing (exit=" + code + "), retrying with unmodified input");
            code = RunCrisp(crispasr, model, input, lang, of, extra, useGpu, partPath, ffmpegDir);
        }

        // prefer crispasr's own file writer when it produced a non-empty srt
        string nativeSrt = of + ".srt";
        bool haveNative = File.Exists(nativeSrt) && new FileInfo(nativeSrt).Length > 0;
        bool havePart = File.Exists(partPath) && new FileInfo(partPath).Length > 0;
        if (haveNative)
        {
            if (nativeSrt != targetSrt) File.Copy(nativeSrt, targetSrt, true);
        }
        else if (havePart)
        {
            if (File.Exists(targetSrt)) File.Delete(targetSrt);
            File.Move(partPath, targetSrt);
        }
        try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
        try { if (copied && File.Exists(copyPath)) File.Delete(copyPath); } catch { }
        try { if (vocalDir != null && Directory.Exists(vocalDir)) Directory.Delete(vocalDir, true); } catch { }
        // safety copy from input-adjacent output
        string adjSrt = Path.ChangeExtension(input, ".srt");
        if (!File.Exists(targetSrt) && File.Exists(adjSrt))
        {
            try { File.Copy(adjSrt, targetSrt, true); } catch { }
        }

        Log("=== exit=" + code + " srt=" + (File.Exists(targetSrt) ? targetSrt : "MISSING"));
        Console.Error.WriteLine("crispasr-xxl-shim: srt -> " + (File.Exists(targetSrt) ? targetSrt : "FAILED"));
        return File.Exists(targetSrt) ? 0 : code;
    }
}
