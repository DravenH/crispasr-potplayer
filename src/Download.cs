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

static class Dl
{
    const string ReleasesApi = "https://api.github.com/repos/CrispStrobe/CrispASR/releases?per_page=15";
    const string AssetPrefix = "https://github.com/CrispStrobe/CrispASR/releases/download/";
    const string GhProxy = "https://gh-proxy.org/"; // retry prefix when GitHub direct is blocked
    const string ModelRepo = "cstr/parakeet-tdt-0.6b-ja-GGUF";
    const string SepRepo = "cstr/mel-band-roformer-vocals-GGUF"; // optional vocals pre-pass
    const string ModelHost1 = "https://hf-mirror.com/";
    const string ModelHost2 = "https://huggingface.co/";
    const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

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

    // version: null -> PinnedTag (hash-verified), "latest" -> newest release,
    // any other value -> that exact upstream tag (verified only when it is PinnedTag).
    public static void Run(string engDir, string build, string model, bool wantFfmpeg, bool sep, string only, string version)
    {
        bool wantCrisp = only == null || only.Contains("crisp");
        bool wantModel = only == null || only.Contains("model");
        bool wantSep = only == null ? sep : only.Contains("sep"); // listing it in /only: requests it
        bool wantFf = only == null ? wantFfmpeg : wantFfmpeg && only.Contains("ffmpeg");

        var dlg = new Dlg();
        bool headless = only != null; // /only: scripted -> no progress window
        if (!headless) { dlg.Show(); Application.DoEvents(); }

        string tmp = Path.Combine(Path.GetTempPath(), "crispasr-dl-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(tmp);
        var wc = NewClient();
        try
        {
            if (wantCrisp)
            {
                string asset = "crispasr-windows-x86_64-" + build + ".zip";
                string sha;
                string url = FindAssetUrl(asset, version, out sha);
                dlg.SetInfo("查询 CrispASR " + build + " 版下载源…");
                if (sha == null) Log("WARNING: " + url + " is not hash-pinned (base " + PinnedTag + ")");
                Log("asset url: " + url);
                string zip = Path.Combine(tmp, asset);
                string dlUrl = url;
                CheckSha(wc, dlg, delegate { Fetch(wc, dlg, dlUrl, zip, GhProxy); }, zip, sha);
                dlg.SetInfo("解压 CrispASR…");
                if (!headless) Application.DoEvents();
                string exeRoot = Path.Combine(engDir, "CrispASR");
                // models/ lives inside CrispASR but is ours to keep: move it aside
                // so re-installing the runtime never throws away a 642 MB download
                string models = Path.Combine(exeRoot, "models");
                string keep = Path.Combine(tmp, "keepmodels");
                bool kept = false;
                if (Directory.Exists(models)) { MoveDir(models, keep); kept = true; }
                if (Directory.Exists(exeRoot)) Directory.Delete(exeRoot, true);
                string unz = Path.Combine(tmp, "unz");
                ZipFile.ExtractToDirectory(zip, unz);
                string found = FindCrispDir(unz);
                if (found == null) throw new InvalidOperationException("zip 内未找到 crispasr.exe");
                MoveDir(found, exeRoot);
                if (kept) MoveDir(keep, models);
                File.Delete(zip);
            }

            if (wantModel) GetModel(wc, dlg, engDir, model);
            if (wantSep) GetModel(wc, dlg, engDir, SepModel);

            if (wantFf)
            {
                // gyan.dev's "release-essentials" is a rolling URL, so no hash can be
                // pinned; the zip is still size-checked and we only copy the two exes out.
                string zip = Path.Combine(tmp, "ffmpeg.zip");
                Fetch(wc, dlg, FfmpegUrl, zip, GhProxy);
                dlg.SetInfo("解压 ffmpeg…");
                if (!headless) Application.DoEvents();
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
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
            try { dlg.Close(); } catch { }
        }
        if (dlg.Cancelled) throw new InvalidOperationException("已取消下载");
    }

    // Fetch one GGUF into <engDir>\CrispASR\models\ unless an intact copy is
    // already there (size check first, then the pinned LFS oid).
    static void GetModel(WebClient wc, Dlg dlg, string engDir, string model)
    {
        string mdir = Path.Combine(Path.Combine(engDir, "CrispASR"), "models");
        Directory.CreateDirectory(mdir);
        string mfile = Path.Combine(mdir, model);
        string msha; ModelSha.TryGetValue(model, out msha);
        long msize; ModelSize.TryGetValue(model, out msize);
        bool have = false;
        if (File.Exists(mfile))
        {
            have = msize > 0 ? new FileInfo(mfile).Length == msize
                             : new FileInfo(mfile).Length > 100L * 1024 * 1024;
            if (have)
            {
                dlg.SetInfo("校验已有模型 " + model + " …");
                have = Verify(mfile, msha);   // unknown model name -> no hash -> keep it
                if (!have) dlg.SetInfo("已有模型校验不过，重新下载…");
            }
        }
        if (have)
            dlg.SetInfo("模型已存在且校验通过，跳过下载");
        else
        {
            string repo = RepoOf(model);
            string[] murls = new string[] {
                ModelHost1 + repo + "/resolve/main/" + model,
                ModelHost2 + repo + "/resolve/main/" + model };
            CheckSha(wc, dlg, delegate { FetchAny(wc, dlg, murls, mfile); }, mfile, msha);
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

    // download with optional proxy retry; progress shown via dlg
    static void Fetch(WebClient wc, Dlg dlg, string url, string file, string proxyPrefix)
    {
        try
        {
            FetchOnce(wc, dlg, url, file);
        }
        catch (Exception e)
        {
            if (dlg.Cancelled) throw;
            if (proxyPrefix == null) throw;
            Log("direct failed (" + e.Message + "), retry via proxy");
            dlg.SetInfo("直连失败，改用代理重试…");
            try { File.Delete(file + ".part"); } catch { }
            FetchOnce(wc, dlg, proxyPrefix + url, file);
        }
    }

    static void FetchAny(WebClient wc, Dlg dlg, string[] urls, string file)
    {
        Exception last = null;
        foreach (var u in urls)
        {
            try { FetchOnce(wc, dlg, u, file); return; }
            catch (Exception e)
            {
                if (dlg.Cancelled) throw;
                last = e;
                Log("url failed " + u + " : " + e.Message);
            }
        }
        throw last ?? new InvalidOperationException("所有下载源均失败: " + file);
    }

    // Manual redirect following: WebClient/HttpWebRequest on .NET Framework do NOT
    // auto-follow 307/308, and hf-mirror answers /resolve/ with 308.
    static void FetchOnce(WebClient wc, Dlg dlg, string url, string file)
    {
        dlg.SetInfo("下载 " + Path.GetFileName(file) + " …");
        dlg.Bar.Value = 0;

        for (int hop = 0; hop <= 6; hop++)
        {
            HttpWebResponse resp = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "crispasr-potplayer-setup";
                req.AllowAutoRedirect = false;
                req.Timeout = 30000;
                req.ReadWriteTimeout = 120000;
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
                if (code >= 400) throw new InvalidOperationException("HTTP " + code + ": " + url);

                long total = resp.ContentLength;
                string part = file + ".part";
                using (var input = resp.GetResponseStream())
                using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[81920];
                    long received = 0;
                    int n;
                    DateTime lastTick = DateTime.MinValue;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, n);
                        received += n;
                        DateTime now = DateTime.Now;
                        if ((now - lastTick).Milliseconds >= 50)
                        {
                            lastTick = now;
                            if (total > 0)
                            {
                                try { dlg.Bar.Value = (int)Math.Min(100, received * 100 / total); } catch { }
                                dlg.SetInfo("下载 " + Path.GetFileName(file) + "  "
                                    + (received / 1048576) + " / " + (total / 1048576) + " MB");
                            }
                            else dlg.SetInfo("下载 " + Path.GetFileName(file) + "  " + (received / 1048576) + " MB");
                            Application.DoEvents();
                            if (dlg.Cancelled) { try { resp.Close(); } catch { } break; }
                        }
                    }
                }
                resp.Close();
                if (dlg.Cancelled) throw new InvalidOperationException("已取消下载");
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

    class Dlg : Form
    {
        public Label Info;
        public ProgressBar Bar;
        public bool Cancelled;

        public Dlg()
        {
            Text = "CrispASR for PotPlayer - 下载组件";
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
