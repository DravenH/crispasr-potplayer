// Component downloader for the CrispASR-PotPlayer installer (see Installer.cs).
// Downloads crispasr windows zip (build chosen by GPU), Japanese GGUF model and
// optionally ffmpeg into <PotPlayer>\Engine\Whisper-Faster\, with a progress
// window, proxy fallback retries and sha-free but size-checked writes.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
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
    const string ModelHost1 = "https://hf-mirror.com/";
    const string ModelHost2 = "https://huggingface.co/";
    const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static void Run(string engDir, string build, string model, bool wantFfmpeg, string only)
    {
        bool wantCrisp = only == null || only.Contains("crisp");
        bool wantModel = only == null || only.Contains("model");
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
                dlg.SetInfo("查询 CrispASR " + build + " 版下载源…");
                string url = FindAssetUrl(asset);
                Log("asset url: " + url);
                string zip = Path.Combine(tmp, asset);
                Fetch(wc, dlg, url, zip, GhProxy);
                dlg.SetInfo("解压 CrispASR…");
                if (!headless) Application.DoEvents();
                string exeRoot = Path.Combine(engDir, "CrispASR");
                if (Directory.Exists(exeRoot)) Directory.Delete(exeRoot, true);
                string unz = Path.Combine(tmp, "unz");
                ZipFile.ExtractToDirectory(zip, unz);
                string found = FindCrispDir(unz);
                if (found == null) throw new InvalidOperationException("zip 内未找到 crispasr.exe");
                MoveDir(found, exeRoot);
                File.Delete(zip);
            }

            if (wantModel)
            {
                string mdir = Path.Combine(Path.Combine(engDir, "CrispASR"), "models");
                Directory.CreateDirectory(mdir);
                string mfile = Path.Combine(mdir, model);
                if (File.Exists(mfile) && new FileInfo(mfile).Length > 100L * 1024 * 1024)
                    dlg.SetInfo("模型已存在，跳过下载");
                else
                    FetchAny(wc, dlg, new string[] {
                        ModelHost1 + ModelRepo + "/resolve/main/" + model,
                        ModelHost2 + ModelRepo + "/resolve/main/" + model }, mfile);
            }

            if (wantFf)
            {
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

    // newest release containing the asset; regex over API JSON (no JSON lib in csc)
    static string FindAssetUrl(string asset)
    {
        try
        {
            using (var wc = NewClient())
            {
                string json = FetchString(wc, ReleasesApi);
                var m = Regex.Match(json, "\"browser_download_url\":\\s*\"([^\"]*\\/" + Regex.Escape(asset) + ")\"");
                if (m.Success) return m.Groups[1].Value;
                Log("asset not in recent releases, using fallback tag: " + asset);
            }
        }
        catch (Exception e) { Log("release api failed: " + e.Message); }
        // v0.8.36 ships cpu/cpu-legacy/vulkan; windows cuda zips last verified in v0.8.35
        string tag = (asset.Contains("-cuda13.") || asset.EndsWith("-cuda.zip")) ? "v0.8.35" : "v0.8.36";
        return AssetPrefix + tag + "/" + asset;
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
