// Component downloader for the CrispASR-PotPlayer installer (see Installer.cs).
// Downloads crispasr windows zip (build chosen by GPU), Japanese GGUF model and
// optionally ffmpeg into <PotPlayer>\Engine\Whisper-Faster\, with a progress
// window, proxy fallback retries and SHA-256 verification of pinned assets.
// Default source is a known-good release tag (PinnedTag) whose asset hashes are
// compiled in; only an explicit /version: or the "check for updates" checkbox makes
// the installer follow the newest release, in which case no hash can be verified.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// The four pieces the installer deals with. Each is probed and downloaded on its
// own, so a healthy runtime never masks a deleted model (and vice versa).
[Flags]
enum Comp { None = 0, Crisp = 1, Model = 2, Sep = 4, Ffmpeg = 8 }

static class Dl
{
    const string ReleasesApi = "https://api.github.com/repos/CrispStrobe/CrispASR/releases?per_page=15";
    const string AssetPrefix = "https://github.com/CrispStrobe/CrispASR/releases/download/";
    const string GhProxy = "https://gh-proxy.org/"; // retry prefix when GitHub direct is blocked
    const string ModelRepo = "cstr/parakeet-tdt-0.6b-ja-GGUF";
    const string SepRepo = "cstr/mel-band-roformer-vocals-GGUF"; // optional vocals pre-pass
    const string ModelHost1 = "https://hf-mirror.com/";
    const string ModelHost2 = "https://huggingface.co/";

    // ffmpeg: gyan.dev's rolling zip measured ~900 B/s from CN networks, so the
    // default is the same build mirrored as a fixed-version gzip (28 MB, hashable)
    // on two fast hosts; gyan.dev stays as the last-resort source.
    const string FfmpegTag = "b6.1.1";
    const string FfmpegCdn = "https://cdn.npmmirror.com/binaries/ffmpeg-static/" + FfmpegTag + "/";
    const string FfmpegGithub = "https://github.com/eugeneware/ffmpeg-static/releases/download/" + FfmpegTag + "/";
    const string FfmpegGzSha = "8883a3dffbd0a16cf4ef95206ea05283f78908dbfb118f73c83f4951dcc06d77";
    const string FfmpegExeSha = "04e1307997530f9cf2fe35cba2ca7e8875ca91da02f89d6c7243df819c94ad00";
    const long FfmpegExeBytes = 82797568L;
    const string FfprobeGzSha = "f309e6223ad89d2fe54bccd420a7709b66fd27540674e92309578ed491a43c8d";
    const string FfprobeExeSha = "3a7e2dc003dc2cd1472827e4c7c4f056ae1ae0ae7c5bbc580c99b49827351ba4";
    const long FfprobeExeBytes = 82668032L;
    const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    // Upstream release the maintainer verified by hand; every asset below is
    // sha256-checked against this tag. Bump PinnedTag + refresh the table only
    // after re-testing a newer release.
    public const string PinnedTag = "v0.8.36";

    static readonly Dictionary<string, string> AssetSha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "crispasr-windows-x86_64-cpu.zip", "1d8c853d102671f4036ccf4da8573a6d9ed3d45ae4530aa07573760a4bc93dc1" },
        { "crispasr-windows-x86_64-cpu-legacy.zip", "fb0b8555343daf434533e53d4eca2d10726f991f5014008e50af64607f184bd1" },
        { "crispasr-windows-x86_64-vulkan.zip", "659e6cc1d3d0c7d65e1ce2df61efd7295c5b017e8a95c4d340c20ba70793d9cc" },
        { "crispasr-windows-x86_64-cuda13.zip", "d81795954af9b9f08ccd43ab875e6db8d538881fef3b910e7b6bddd07617a98f" },
        { "crispasr-windows-x86_64-cuda.zip", "4d14ce34cbc089259e897bed369214f6f920efa31e3236845bb6c7464ed7fba0" },
    };

    // HF publishes these LFS oids (== sha256 of the served file) in its tree API.
    static readonly Dictionary<string, string> ModelSha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "parakeet-tdt-0.6b-ja-q8_0.gguf", "5a61e6c7d956c3c72a76fafcd798cac0c9ea66d0e29b3910cd04865a1e42cc17" },
        { "parakeet-tdt-0.6b-ja.gguf", "374eb0132eebaec4df77a9631cbbeb03790be48a4a517f6cc8e8bdb38fe9a584" },
        { "parakeet-tdt-0.6b-ja-q4_k.gguf", "9a9bdfec5a1f119983a00367d33fb310759d67619309f02500f649c5328ab825" },
        { "mel-band-roformer-vocals-f16.gguf", "fe94b114bce12653dae4e94968d28cbab36dcdd0c783fc052c3b656218233f24" },
    };

    static readonly Dictionary<string, long> ModelSize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        { "parakeet-tdt-0.6b-ja-q8_0.gguf", 673554880L },
        { "parakeet-tdt-0.6b-ja.gguf", 1246932800L },
        { "parakeet-tdt-0.6b-ja-q4_k.gguf", 405502208L },
        { "mel-band-roformer-vocals-f16.gguf", 457014016L },
    };

    static string RepoOf(string model)
    {
        return model.StartsWith("mel-band-roformer", StringComparison.OrdinalIgnoreCase) ? SepRepo : ModelRepo;
    }

    // file name the shim auto-detects for its opt-in vocals=1 pre-pass
    public const string SepModel = "mel-band-roformer-vocals-f16.gguf";

    static readonly Dictionary<string, bool> GgufProbeCache =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // Is this path a usable GGUF? The pinned size is checked first (a truncated or
    // half-finished file fails there), then the compiled-in LFS oid. File names we
    // have no hash for -- the user's own models -- only get a size sanity check.
    // Results are cached because the installer probes the same 600 MB files
    // repeatedly while narrowing down what is missing.
    public static bool GgufOk(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        bool yes;
        lock (GgufProbeCache) if (GgufProbeCache.TryGetValue(path, out yes)) return yes;
        try
        {
            if (!File.Exists(path)) return false;
            string name = Path.GetFileName(path);
            string sha;
            ModelSha.TryGetValue(name, out sha);
            long pinned;
            yes = ModelSize.TryGetValue(name, out pinned)
                ? new FileInfo(path).Length == pinned
                : new FileInfo(path).Length > 100L * 1024 * 1024;
            if (yes) yes = Verify(path, sha);
        }
        catch (Exception e) { Log("probe failed " + path + " : " + e.Message); yes = false; }
        lock (GgufProbeCache) GgufProbeCache[path] = yes;
        return yes;
    }

    // version: null -> PinnedTag (hash-verified), "latest" -> newest release,
    // any other value -> that exact upstream tag (verified only when it is PinnedTag).
    // want lists which pieces to install; force re-downloads even intact ones.
    public static void Run(string engDir, string build, string model, Comp want, bool force, bool headless, string version)
    {
        var dlg = new Dlg();
        if (!headless) { dlg.Show(); Application.DoEvents(); }

        // stable name: a killed run leaves its .part behind and the next run resumes
        string tmp = Path.Combine(Path.GetTempPath(), "crispasr-dl");
        Directory.CreateDirectory(tmp);
        var wc = NewClient();
        var done = new List<string>();
        try
        {
            if ((want & Comp.Crisp) != 0)
            {
                string what = "CrispASR 运行时（" + build + " 版）";
                Step(what, delegate { GetRuntime(wc, dlg, engDir, tmp, build, version, headless); });
                done.Add(what);
            }
            if ((want & Comp.Model) != 0) { GetModel(wc, dlg, engDir, model, force); done.Add("模型 " + model); }
            if ((want & Comp.Sep) != 0) { GetModel(wc, dlg, engDir, SepModel, force); done.Add("模型 " + SepModel); }
            if ((want & Comp.Ffmpeg) != 0)
            {
                Step("ffmpeg", delegate { GetFfmpeg(wc, dlg, engDir, tmp); });
                done.Add("ffmpeg");
            }
        }
        catch (Exception e)
        {
            // name what already landed: re-running the installer skips it, and the
            // user needs to know which of the four pieces actually broke
            if (done.Count > 0)
                throw new InvalidOperationException(e.Message
                    + "\n\n已完成的组件：" + string.Join("、", done.ToArray())
                    + "\n重新运行安装器会复用已下载好的部分（先核对 SHA-256 再决定是否重下）。", e);
            throw;
        }
        finally
        {
            try { CleanTmp(tmp); } catch { }
            try { dlg.Close(); } catch { }
        }
        if (dlg.Cancelled) throw new Cancelled();
    }

    // the user pressed 取消 -- distinct from a network/server failure so the
    // installer can say "已取消下载" instead of "下载失败"
    class Cancelled : Exception
    {
        public Cancelled() : base("已取消下载") { }
    }

    // the installer asks this to title its dialog correctly for a cancel
    public static bool WasCancelled(Exception e)
    {
        for (; e != null; e = e.InnerException) if (e is Cancelled) return true;
        return false;
    }

    // one labelled stage: any failure names the component in the message the
    // installer shows, instead of a bare "HTTP 404: <url>"
    static void Step(string what, Action body)
    {
        try { body(); }
        catch (Exception e)
        {
            if (e is ComponentError) throw;
            Log(what + " failed: " + e);
            throw new ComponentError(what, e is Cancelled ? "已取消下载" : "下载失败", e);
        }
    }

    class ComponentError : Exception
    {
        public ComponentError(string what, string verb, Exception inner)
            : base("【" + what + "】" + verb
                + (inner is Cancelled
                    ? ""                                   // "已取消下载" already says it all
                    : "\n" + (string.IsNullOrEmpty(inner.Message) ? inner.GetType().Name : inner.Message)),
                  inner) { }
    }

    static void GetRuntime(WebClient wc, Dlg dlg, string engDir, string tmp, string build, string version, bool headless)
    {
        string asset = "crispasr-windows-x86_64-" + build + ".zip";
        string sha;
        string url = FindAssetUrl(asset, version, out sha);
        dlg.SetInfo("查询 CrispASR " + build + " 版下载源…");
        if (sha == null) Log("WARNING: " + url + " is not hash-pinned (base " + PinnedTag + ")");
        Log("asset url: " + url);
        string zip = Path.Combine(tmp, asset);
        // GitHub direct first, then the same URL through gh-proxy
        string[] urls = new string[] { url, GhProxy + url };
        CheckSha(wc, dlg, delegate { FetchSources(wc, dlg, urls, zip, !string.IsNullOrEmpty(sha)); }, zip, sha);
        dlg.SetInfo("解压 CrispASR…");
        if (!headless) Application.DoEvents();
        string exeRoot = Path.Combine(engDir, "CrispASR");
        // models/ lives inside CrispASR but is ours to keep: move it aside so
        // re-installing the runtime never throws away a 642 MB download. The
        // staging dir sits on engDir's own volume -- MoveDir is a plain rename
        // there, whereas %TEMP% on another drive would copy a 1 GB gguf twice
        // (and need the free space for it).
        string models = Path.Combine(exeRoot, "models");
        string keep = Path.Combine(engDir, "models.keep");
        bool kept = false;
        if (Directory.Exists(keep))
        {
            // leftover from a run that died mid-swap; ours to clear
            Log("clearing stale staging dir " + keep);
            try { Directory.Delete(keep, true); } catch { }
        }
        if (Directory.Exists(models)) { MoveDir(models, keep); kept = true; }
        try
        {
            if (Directory.Exists(exeRoot)) Directory.Delete(exeRoot, true);
            string unz = Path.Combine(tmp, "unz");
            ZipFile.ExtractToDirectory(zip, unz);
            string found = FindCrispDir(unz);
            if (found == null) throw new InvalidOperationException("zip 内未找到 crispasr.exe");
            MoveDir(found, exeRoot);
        }
        catch
        {
            if (kept && !Directory.Exists(models))
            {
                try { MoveDir(keep, models); } catch { }   // put the models back, then report
                kept = false;
            }
            throw;
        }
        if (kept) MoveDir(keep, models);
        File.Delete(zip);
    }

    // ffmpeg lands as <engDir>\ffmpeg\ffmpeg.exe (+ ffprobe.exe when it arrives).
    // First the pinned gzip mirrors, verified at both ends; only if every one of
    // them fails does the uncheckable rolling gyan zip get its turn.
    static void GetFfmpeg(WebClient wc, Dlg dlg, string engDir, string tmp)
    {
        string fdir = Path.Combine(engDir, "ffmpeg");
        try
        {
            FetchGzToExe(wc, dlg, GzUrls("ffmpeg-win32-x64.gz"), Path.Combine(tmp, "ffmpeg-" + FfmpegTag + ".gz"),
                         FfmpegGzSha, Path.Combine(fdir, "ffmpeg.exe"), FfmpegExeSha, FfmpegExeBytes);
            try
            {
                FetchGzToExe(wc, dlg, GzUrls("ffprobe-win32-x64.gz"), Path.Combine(tmp, "ffprobe-" + FfmpegTag + ".gz"),
                             FfprobeGzSha, Path.Combine(fdir, "ffprobe.exe"), FfprobeExeSha, FfprobeExeBytes);
            }
            catch (Exception e)
            {
                if (dlg.Cancelled) throw new Cancelled();
                Log("ffprobe skipped: " + e.Message);      // crispasr only ever calls ffmpeg
                dlg.SetInfo("ffprobe 未取到，已跳过（crispasr 只调用 ffmpeg）…");
                try { File.Delete(Path.Combine(fdir, "ffprobe.exe")); } catch { }
            }
            return;
        }
        catch (Cancelled) { throw; }
        catch (Exception e)
        {
            if (dlg.Cancelled) throw new Cancelled();
            Log("mirrored ffmpeg sources failed: " + e);
            dlg.SetInfo("镜像源都不可用，改用 gyan.dev 官方 zip（可能很慢）…");
        }
        GetFfmpegFromZip(wc, dlg, engDir, tmp);
    }

    static string[] GzUrls(string asset)
    {
        return new string[] { FfmpegCdn + asset, GhProxy + FfmpegGithub + asset };
    }

    // download the gzip (hash-checked), gunzip it, then hash the exe it produced
    static void FetchGzToExe(WebClient wc, Dlg dlg, string[] urls, string gzFile, string gzSha,
                             string exeFile, string exeSha, long exeBytes)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            CheckSha(wc, dlg, delegate
            {
                FetchSources(wc, dlg, urls, gzFile, !string.IsNullOrEmpty(gzSha));
            }, gzFile, gzSha);
            dlg.SetInfo("解压 " + Path.GetFileName(gzFile) + " …");
            // only create the target dir once a gzip is actually in hand: a run that
            // never got one should not leave an empty ffmpeg\ behind
            Directory.CreateDirectory(Path.GetDirectoryName(exeFile));
            Gunzip(gzFile, exeFile);
            if (new FileInfo(exeFile).Length == exeBytes && Verify(exeFile, exeSha)) return;
            Log("gunzip verify failed for " + exeFile + ", attempt " + attempt);
            try { File.Delete(gzFile); } catch { }
            try { File.Delete(gzFile + ".part"); } catch { }
        }
        throw new InvalidOperationException("内容校验失败（下载不完整或与固定版本不符）："
            + Path.GetFileName(exeFile) + "\n期望 SHA-256：" + exeSha);
    }

    static void Gunzip(string src, string dst)
    {
        if (File.Exists(dst)) File.Delete(dst);
        using (var f = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var gz = new GZipStream(f, CompressionMode.Decompress))
        using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[1048576];
            int n;
            while ((n = gz.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
        }
    }

    // every source in order, three attempts each; the retries after the first one
    // resume from the .part the previous attempt left behind instead of restarting.
    // `pinned` says the caller checks a SHA-256 afterwards, which makes it safe to
    // carry that .part over to the next host too (HF's CDN drops 600 MB transfers
    // regularly, and hf-mirror and huggingface.co serve the very same bytes) -- a
    // bad merge simply fails the hash and gets redownloaded. Without a hash to check
    // we never mix bytes from two hosts.
    static void FetchSources(WebClient wc, Dlg dlg, string[] urls, string file, bool pinned)
    {
        // half-downloaded files are only ever spliced when a SHA-256 will check the
        // result: a rolling upstream (the gyan zip, /version:latest) can change under us
        if (!pinned) { try { File.Delete(file + ".part"); } catch { } }
        Exception last = null;
        foreach (var u in urls)
        {
            for (int t = 0; t < 3; t++)
            {
                try { FetchOnce(wc, dlg, u, file); return; }
                catch (Exception e)
                {
                    if (dlg.Cancelled) throw e is Cancelled ? e : new Cancelled();
                    last = e;
                    Log("url failed " + u + " : " + e.Message);
                    if (t < 2) dlg.SetInfo("同一源重试（已下部分会续传）…");
                }
            }
            if (!pinned) try { File.Delete(file + ".part"); } catch { }
        }
        if (last != null) throw last;
        throw new InvalidOperationException("所有下载源均失败: " + file);
    }

    // rolling gyan.dev zip: no hash can be pinned, so it is only a fallback
    static void GetFfmpegFromZip(WebClient wc, Dlg dlg, string engDir, string tmp)
    {
        string zip = Path.Combine(tmp, "ffmpeg.zip");
        // gh-proxy only mirrors github.com, so a proxy prefix on this URL is useless
        FetchSources(wc, dlg, new string[] { FfmpegZipUrl }, zip, false);
        dlg.SetInfo("解压 ffmpeg…");
        string unz = Path.Combine(tmp, "ffunz");
        ZipFile.ExtractToDirectory(zip, unz);
        string ff = FindFile(unz, "ffmpeg.exe");
        if (ff == null) throw new InvalidOperationException("ffmpeg zip 内未找到 ffmpeg.exe");
        string fdir = Path.Combine(engDir, "ffmpeg");
        Directory.CreateDirectory(fdir);
        File.Copy(ff, Path.Combine(fdir, "ffmpeg.exe"), true);
        string fp = FindFile(unz, "ffprobe.exe");
        if (fp != null) File.Copy(fp, Path.Combine(fdir, "ffprobe.exe"), true);
    }

    // Fetch one GGUF into <engDir>\CrispASR\models\ unless an intact copy is
    // already there; force (from /force) reinstalls even a good file.
    static void GetModel(WebClient wc, Dlg dlg, string engDir, string model, bool force)
    {
        Step("模型 " + model, delegate { FetchModel(wc, dlg, engDir, model, force); });
    }

    static void FetchModel(WebClient wc, Dlg dlg, string engDir, string model, bool force)
    {
        string mdir = Path.Combine(Path.Combine(engDir, "CrispASR"), "models");
        Directory.CreateDirectory(mdir);
        string mfile = Path.Combine(mdir, model);
        string msha;
        ModelSha.TryGetValue(model, out msha);
        if (force && File.Exists(mfile))
        {
            dlg.SetInfo("按 /force 重装，删除已有 " + model + " …");
            try { File.Delete(mfile); } catch { }
        }
        if (!force && GgufOk(mfile))
            dlg.SetInfo("模型已存在且校验通过，跳过下载");
        else
        {
            string repo = RepoOf(model);
            string[] murls = new string[] {
                ModelHost1 + repo + "/resolve/main/" + model,
                ModelHost2 + repo + "/resolve/main/" + model };
            CheckSha(wc, dlg, delegate { FetchSources(wc, dlg, murls, mfile, msha != null); }, mfile, msha);
        }
    }

    static string FetchString(WebClient wc, string url)
    {
        try { return wc.DownloadString(url); }
        catch (WebException)
        {
            // local proxy may reject the API: retry without any proxy
            var wc2 = NewClient();
            wc2.Proxy = null;
            return wc2.DownloadString(url);
        }
    }

    // null -> PinnedTag + compiled-in sha256; "latest" -> newest release that has
    // the asset (API scan, unverified); any other value -> that exact upstream tag
    // (unverified unless it happens to be PinnedTag).
    static string FindAssetUrl(string asset, string version, out string sha)
    {
        sha = null;
        if (version == null || version.Equals(PinnedTag, StringComparison.OrdinalIgnoreCase))
        {
            string pinned = ShaOf(asset);
            if (pinned != null)
            {
                sha = pinned;
                return AssetPrefix + PinnedTag + "/" + asset;
            }
            Log("no pinned hash for " + asset + ", resolving newest release instead");
            version = "latest";
        }
        if (version.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            string url = NewestAssetUrl(asset);
            if (url == null) throw new InvalidOperationException("上游 releases 中找不到资产：" + asset);
            if (url.Contains("/" + PinnedTag + "/")) sha = ShaOf(asset);
            return url;
        }
        if (!Regex.IsMatch(version, "^[vV]?[0-9]+\\.[0-9]+[0-9A-Za-z.+-]*$"))
            throw new InvalidOperationException("非法的版本号（应为上游 tag，如 v0.8.36）：" + version);
        return AssetPrefix + version.Trim('/') + "/" + asset;
    }

    // newest release containing the asset; regex over API JSON (no JSON lib in csc)
    static string NewestAssetUrl(string asset)
    {
        try
        {
            using (var wc = NewClient())
            {
                string json = FetchString(wc, ReleasesApi);
                var m = Regex.Match(json, "\"browser_download_url\":\\s*\"([^\"]*\\/" + Regex.Escape(asset) + ")\"");
                if (m.Success) return m.Groups[1].Value;
                Log("asset not in recent releases: " + asset);
            }
        }
        catch (Exception e) { Log("release api failed: " + e.Message); }
        return null;
    }

    static string ShaOf(string asset)
    {
        string s;
        return AssetSha.TryGetValue(asset, out s) ? s : null;
    }

    static string Sha256File(string path)
    {
        using (var hash = SHA256.Create())
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var buf = new byte[1048576];
            int n;
            while ((n = f.Read(buf, 0, buf.Length)) > 0)
                hash.TransformBlock(buf, 0, n, null, 0);
            hash.TransformFinalBlock(new byte[0], 0, 0);
            var sb = new StringBuilder(64);
            foreach (var b in hash.Hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    static bool Verify(string file, string sha)
    {
        if (string.IsNullOrEmpty(sha)) return true;
        string got = Sha256File(file);
        bool ok = got.Equals(sha, StringComparison.OrdinalIgnoreCase);
        Log((ok ? "sha ok   " : "sha FAIL ") + Path.GetFileName(file) + " " + got);
        return ok;
    }

    public static string Sha256Bytes(byte[] data)
    {
        using (var hash = SHA256.Create())
        {
            var sb = new StringBuilder(64);
            foreach (var b in hash.ComputeHash(data)) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // download, then refuse to install anything whose hash doesn't match; a
    // mismatch (broken mirror, truncated transfer) gets exactly one retry.
    static void CheckSha(WebClient wc, Dlg dlg, Action download, string file, string sha)
    {
        download();
        if (string.IsNullOrEmpty(sha)) return;
        dlg.SetInfo("校验 " + Path.GetFileName(file) + " 的 SHA-256…");
        if (Verify(file, sha)) return;
        Log("sha mismatch, retrying: " + file);
        dlg.SetInfo("SHA-256 校验失败，重新下载…");
        try { File.Delete(file); } catch { }
        // start the retry from scratch: resuming the very bytes that just failed
        // (a mirror that serves something else, a splice across two hosts) cannot
        // come out right the second time
        try { File.Delete(file + ".part"); } catch { }
        download();
        if (!Verify(file, sha))
            throw new InvalidOperationException("SHA-256 校验失败，文件可能损坏或被篡改：\n"
                + Path.GetFileName(file) + "\n期望：" + sha + "\n实际：" + Sha256File(file)
                + "\n若上游确实更新了该文件，请改用手动下载（见 README）或调整 /version: 后再试。");
    }

    static Dl()
    {
        try
        {
            ServicePointManager.SecurityProtocol =
                ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072 | (SecurityProtocolType)768 | (SecurityProtocolType)192;
            ServicePointManager.DefaultConnectionLimit = 8;
        }
        catch { }
    }

    static WebClient NewClient()
    {
        var wc = new WebClient();
        wc.Headers["User-Agent"] = "crispasr-potplayer-setup";
        ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | (SecurityProtocolType)4080;
        return wc;
    }

    static void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "crispasr-potplayer-setup.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [dl] " + msg + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    static string FindCrispDir(string root)
    {
        string exe = FindFile(root, "crispasr.exe");
        return exe == null ? null : Path.GetDirectoryName(exe);
    }

    static string FindFile(string root, string name)
    {
        try
        {
            foreach (var f in Directory.GetFiles(root, name, SearchOption.AllDirectories))
                return f;
        }
        catch { }
        return null;
    }

    static void MoveDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.GetDirectories(src))
            Directory.Move(d, Path.Combine(dst, Path.GetFileName(d)));
        foreach (var f in Directory.GetFiles(src))
            File.Move(f, Path.Combine(dst, Path.GetFileName(f)));
        try { Directory.Delete(src, true); } catch { }
    }

    // Finished downloads and extraction dirs go, but a half-file stays: the next run
    // of the installer resumes it with a Range request instead of paying for a
    // several-hundred-megabyte transfer twice after one dropped connection.
    static void CleanTmp(string tmp)
    {
        foreach (var f in Directory.GetFiles(tmp, "*", SearchOption.AllDirectories))
            if (!f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            { try { File.Delete(f); } catch { } }
        foreach (var d in Directory.GetDirectories(tmp))
        { try { Directory.Delete(d, true); } catch { } }
    }

    // Manual redirect following: WebClient/HttpWebRequest on .NET Framework do NOT
    // auto-follow 307/308, and hf-mirror answers /resolve/ with 308. A leftover
    // .part is resumed with a Range request, and a link that has effectively stalled
    // is abandoned so the next source gets a chance instead of hanging for hours.
    static void FetchOnce(WebClient wc, Dlg dlg, string url, string file)
    {
        dlg.SetInfo("下载 " + Path.GetFileName(file) + " …");
        dlg.Bar.Value = 0;

        for (int hop = 0; hop <= 6; hop++)
        {
            HttpWebResponse resp = null;
            try
            {
                string part = file + ".part";
                long start = 0;
                try { if (File.Exists(part)) start = new FileInfo(part).Length; } catch { }

                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "crispasr-potplayer-setup";
                req.AllowAutoRedirect = false;
                req.Timeout = 30000;
                req.ReadWriteTimeout = 120000;
                if (start > 0) req.AddRange(start);
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException we)
                {
                    resp = we.Response as HttpWebResponse;
                    if (resp == null) throw;
                }

                int code = (int)resp.StatusCode;
                if (code >= 300 && code < 400)
                {
                    string loc = resp.Headers["Location"];
                    Uri baseUri = resp.ResponseUri;
                    resp.Close();
                    if (string.IsNullOrEmpty(loc)) throw new InvalidOperationException("重定向无 Location: " + url);
                    url = new Uri(baseUri, loc).AbsoluteUri;
                    Log("follow redirect " + code + " -> " + url);
                    continue;
                }
                if (code == 416 && start > 0)      // .part no longer applies: start over
                {
                    resp.Close();
                    try { File.Delete(part); } catch { }
                    Log("range rejected by " + HostOf(url) + ", restarting");
                    continue;
                }
                if (code >= 400) throw new InvalidOperationException("HTTP " + code + ": " + url);

                bool resumed = code == 206 && start > 0;
                long body = resp.ContentLength;
                long total = resumed && body > 0 ? start + body : body;
                if (resumed) dlg.SetInfo("续传 " + Path.GetFileName(file) + "，已有 " + (start / 1048576) + " MB…");
                Log((resumed ? "resume " : "fresh ") + Path.GetFileName(file) + " from " + HostOf(url));

                double stalled = -1;
                using (var input = resp.GetResponseStream())
                using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create,
                                                   FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[81920];
                    long received = resumed ? start : 0;
                    int n;
                    DateTime lastTick = DateTime.MinValue;
                    DateTime begin = DateTime.Now, mark = begin;
                    long markBytes = received;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, n);
                        received += n;
                        DateTime now = DateTime.Now;
                        if ((now - lastTick).Milliseconds >= 200)
                        {
                            lastTick = now;
                            double secs = (now - mark).TotalSeconds;
                            double speed = secs > 0.2 ? (received - markBytes) / secs : 0;
                            if (secs >= 15) { mark = now; markBytes = received; }
                            string extra = total > 0
                                ? (received / 1048576) + " / " + (total / 1048576) + " MB"
                                : (received / 1048576) + " MB";
                            if (received > 1048576 && speed > 0) extra += "  " + FmtSpeed(speed) + "/s";
                            dlg.SetInfo("下载 " + Path.GetFileName(file) + "  " + extra);
                            if (total > 0)
                            {
                                try { dlg.Bar.Value = (int)Math.Min(100, received * 100 / total); } catch { }
                            }
                            Application.DoEvents();
                            if (dlg.Cancelled) { try { resp.Close(); } catch { } break; }
                            // a pinned-size file that is not moving is a dead source, not
                            // a slow one; keep the .part so a retry can pick it up again
                            if (secs >= 15 && (now - begin).TotalSeconds > 30 && speed < 10240)
                            {
                                stalled = speed;
                                try { resp.Close(); } catch { }
                                break;
                            }
                        }
                    }
                }
                resp.Close();
                if (dlg.Cancelled) throw new Cancelled();
                if (stalled >= 0)
                    throw new InvalidOperationException("下载太慢（" + FmtSpeed(stalled) + "/s），已放弃该源："
                        + HostOf(url) + "。可稍后重试，或按 README 手动下载后重跑安装器。");
                if (!File.Exists(part) || new FileInfo(part).Length == 0)
                    throw new InvalidOperationException("下载未完成: " + url);
                if (File.Exists(file)) File.Delete(file);
                File.Move(part, file);
                return;
            }
            finally
            {
                try { if (resp != null) resp.Close(); } catch { }
            }
        }
        throw new InvalidOperationException("重定向次数过多: " + file);
    }

    static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    static string FmtSpeed(double bps)
    {
        if (bps >= 1048576) return (bps / 1048576).ToString("0.0") + " MB";
        if (bps >= 1024) return (bps / 1024).ToString("0") + " KB";
        return (int)bps + " B";
    }

    class Dlg : Form
    {
        public Label Info;
        public ProgressBar Bar;
        public bool Cancelled;

        public Dlg()
        {
            Text = "CrispASR for PotPlayer - 下载组件";
            Icon = Installer.AppIcon();     // taskbar/title icon: -win32icon covers the file only
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 120);
            Font = new Font("Microsoft YaHei UI", 9f);
            Bar = new ProgressBar();
            Bar.SetBounds(14, 16, 472, 22);
            Info = new Label();
            Info.SetBounds(14, 48, 360, 50);
            var cancel = new Button();
            cancel.Text = "取消";
            cancel.SetBounds(400, 78, 86, 30);
            cancel.Click += delegate { Cancelled = true; try { Close(); } catch { } };
            Controls.Add(Bar); Controls.Add(Info); Controls.Add(cancel);
        }

        public void SetInfo(string s) { try { Info.Text = s; } catch { } }
    }
}
