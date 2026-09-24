// CrispASR-PotPlayer installer core: extracts whisper-faster.exe (+ shim.ini
// template) into <PotPlayer>\Engine\Whisper-Faster. Supports 32/64-bit PotPlayer.
// Optionally auto-downloads runtime components (see Download.cs) after a version
// picker dialog with GPU-aware hints.
// GUI double-click flow, plus silent CLI:
//   Setup.exe ["X:\PotPlayer"] /quiet [/download] [/build:cpu|cuda|cuda13|vulkan]
//             [/model:f16] [/only:crisp,model,ffmpeg,sep] [/version:v0.8.37|latest] [/sep]
// /sep (人声分离) 默认关；勾选/传入后会连带下载 ffmpeg 并把 shim.ini 的 vocals 写成 1。
// Downloads default to the hash-pinned release; /version: opts out of that pin.
// Exit codes: 0 ok, 2 PotPlayer not found, 3 write failed, 4 download failed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

static class Installer
{
    const string ModelQ8 = "parakeet-tdt-0.6b-ja-q8_0.gguf";   // ~642 MB
    const string ModelF16 = "parakeet-tdt-0.6b-ja.gguf";       // ~1190 MB

    static string ExeDir = AppDomain.CurrentDomain.BaseDirectory;
    static string LogPath = Path.Combine(Path.GetTempPath(), "crispasr-potplayer-setup.log");

    [STAThread]
    static int Main(string[] args)
    {
        try { Application.EnableVisualStyles(); } catch { }

        string dir = null, buildOverride = null, modelOverride = null, only = null, versionOverride = null;
        bool quiet = false, downloadFlag = false, sepFlag = false;
        foreach (var a in args)
        {
            if (a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("/s", StringComparison.OrdinalIgnoreCase)) quiet = true;
            else if (a.Equals("/download", StringComparison.OrdinalIgnoreCase)) downloadFlag = true;
            else if (a.Equals("/sep", StringComparison.OrdinalIgnoreCase)) sepFlag = true;
            else if (a.StartsWith("/build:", StringComparison.OrdinalIgnoreCase)) buildOverride = a.Substring(7).ToLowerInvariant();
            else if (a.StartsWith("/model:", StringComparison.OrdinalIgnoreCase)) modelOverride = a.Substring(7).ToLowerInvariant();
            else if (a.StartsWith("/only:", StringComparison.OrdinalIgnoreCase)) only = a.Substring(6).ToLowerInvariant();
            else if (a.StartsWith("/version:", StringComparison.OrdinalIgnoreCase)) versionOverride = a.Substring(9).Trim().Trim('"');
            else if (!a.StartsWith("/")) dir = a.Trim('"');
        }

        if (!string.IsNullOrEmpty(dir) && !IsPotPlayer(dir))
        {
            if (quiet) { Log("bad potplayer dir: " + dir); return 2; }
            MessageBox.Show("该目录下找不到 PotPlayer 主程序：\n" + dir,
                "CrispASR for PotPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            dir = null;
        }
        if (string.IsNullOrEmpty(dir)) dir = Probe();
        if (string.IsNullOrEmpty(dir) && !quiet) dir = AskFolder();
        if (string.IsNullOrEmpty(dir))
        {
            if (!quiet)
                MessageBox.Show("未能定位 PotPlayer 安装目录。\n请把本程序所在路径作为参数运行：\nSetup.exe \"X:\\Path\\To\\PotPlayer\"",
                    "CrispASR for PotPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        string engDir = Path.Combine(dir, @"Engine\Whisper-Faster");
        string iniPath = Path.Combine(engDir, "shim.ini");
        InstallRes res = null;
        try
        {
            res = Install(dir);
        }
        catch (Exception e)
        {
            string hint = (e is UnauthorizedAccessException || e is System.Security.SecurityException)
                ? "\n目标目录受系统保护（如 Program Files），请右键本程序选择\"以管理员身份运行\"。"
                : "";
            if (!quiet)
                MessageBox.Show("安装失败：" + e.Message + hint,
                    "CrispASR for PotPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }

        // ---- optional component download ----
        string dlError = null;
        Exception dlEx = null;
        string iniNote = null;
        bool didDownload = false;
        try
        {
            string crisp = ResolveCrispasr(iniPath, engDir);
            bool missing = crisp == null;
            bool wantDl;
            if (quiet) wantDl = downloadFlag;
            else if (downloadFlag) wantDl = true;
            else wantDl = missing && AskYesNo("未检测到 CrispASR。是否自动下载所需组件？\n"
                + "将写入 " + engDir + @"\CrispASR\（下一步可选择具体版本）。");

            if (wantDl)
            {
                string build, model, version;
                bool ff, sep;
                PickComponents(quiet, buildOverride, modelOverride, only, versionOverride, sepFlag,
                               out build, out model, out ff, out sep, out version);
                Dl.Run(engDir, build, model, ff, sep, only, version);
                didDownload = true;
                if (!UpdateIniAfterDownload(iniPath, engDir, sep))
                    iniNote = "组件已下载完成，但 shim.ini 更新失败，请查看日志：" + LogPath;
            }
            else if (sepFlag)
            {
                // /sep without a download run (components already present): the
                // checkbox never appeared, so honour the flag where it lives -- shim.ini
                string sepFile = Path.Combine(engDir, @"CrispASR\models\" + Dl.SepModel);
                iniNote = !UpdateIniAfterDownload(iniPath, engDir, true)
                    ? "/sep 未能写入 shim.ini（见日志），请手动把 vocals 改成 1"
                    : File.Exists(sepFile)
                        ? "已按 /sep 把 shim.ini 的 vocals 写成 1。"
                        : "已按 /sep 把 vocals 写成 1，但目录里还没有分离模型 " + Dl.SepModel
                          + "，运行时会跳过分离；补下：Setup.exe /quiet \"<PotPlayer 目录>\" /download /only:sep";
                Log(iniNote);
            }
        }
        catch (Exception e)
        {
            dlError = e.Message;
            dlEx = e;
            Log("download failed: " + e);
        }

        if (quiet) return dlError == null ? 0 : 4;
        if (dlError != null)
        {
            bool cancelled = Dl.WasCancelled(dlEx);
            MessageBox.Show((cancelled ? "已取消下载（垫片本身已安装成功）：\n"
                                       : "组件下载失败（垫片本身已安装成功）：\n")
                + dlError
                + "\n\n可参考 README 手动下载对应组件，放进目录后重跑安装器即可（已下好的会跳过）。"
                + "\n详细日志：" + LogPath,
                "CrispASR for PotPlayer", MessageBoxButtons.OK,
                cancelled ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        string tail = didDownload ? "\n组件已下载完成，shim.ini 路径已按安装目录自动配置。"
            : (File.Exists(Path.Combine(engDir, @"CrispASR\crispasr.exe"))
                ? "\n已检测到 CrispASR，shim.ini 路径已自动配置。"
                : "\nshim.ini 已按安装目录预置组件路径：" + engDir + "\\CrispASR\\"
                  + "\n稍后把 CrispASR（含 models 目录里的模型）放进该目录即可直接使用，无需再改配置。");
        if (res != null && res.BackupNote != null)
            tail += "\n" + res.BackupNote + "，把它改回 whisper-faster.exe 即可还原官方引擎。";
        if (iniNote != null) tail += "\n" + iniNote;
        MessageBox.Show(
            "安装完成：\n" + Path.Combine(engDir, "whisper-faster.exe") + tail
            + "\n\n重启 PotPlayer 后，在『声音生成字幕』引擎下拉选 Whisper-Faster 即可。",
            "CrispASR for PotPlayer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return dlError == null ? 0 : 4;
    }

    static bool AskYesNo(string text)
    {
        return MessageBox.Show(text, "CrispASR for PotPlayer",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    static bool IsPotPlayer(string dir)
    {
        return File.Exists(Path.Combine(dir, "PotPlayerMini64.exe"))
            || File.Exists(Path.Combine(dir, "PotPlayer64.exe"))      // 64-bit 通用启动器
            || File.Exists(Path.Combine(dir, "PotPlayerMini.exe"))    // 32-bit
            || File.Exists(Path.Combine(dir, "PotPlayer.exe"));
    }

    // registry uninstall entries (both 32/64 views, HKLM+HKCU), then common paths
    static string Probe()
    {
        RegistryHive[] hives = { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
        RegistryView[] views = { RegistryView.Registry64, RegistryView.Registry32 };
        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                    using (var un = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (un == null) continue;
                        foreach (var name in un.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = un.OpenSubKey(name))
                                {
                                    object disp = k == null ? null : k.GetValue("DisplayName");
                                    if (disp == null) continue;
                                    string dn = disp.ToString();
                                    if (string.IsNullOrEmpty(dn) || dn.IndexOf("PotPlayer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    object loc = k.GetValue("DisplayInstallLocation");
                                    if (loc == null) continue;
                                    string l = loc.ToString().TrimEnd('\\');
                                    if (l.Length > 0 && IsPotPlayer(l)) return l;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] cands = {
            Path.Combine(pf, @"DAUM\PotPlayer"),
            Path.Combine(pf86, @"DAUM\PotPlayer"),
            Path.Combine(local, @"Programs\PotPlayer"),
        };
        foreach (var c in cands)
            if (Directory.Exists(c) && IsPotPlayer(c)) return c;
        return null;
    }

    static string AskFolder()
    {
        using (var dlg = new FolderBrowserDialog())
        {
            dlg.Description = "选择 PotPlayer 安装目录";
            // keep re-prompting until a valid dir is picked or the user gives up
            while (true)
            {
                if (dlg.ShowDialog() != DialogResult.OK) return null;
                if (IsPotPlayer(dlg.SelectedPath)) return dlg.SelectedPath;
                MessageBox.Show("所选目录里没有 PotPlayer 主程序，请重新选择。", "CrispASR for PotPlayer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    static byte[] Res(string name)
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) throw new InvalidOperationException("embedded resource missing: " + name);
            var b = new byte[s.Length];
            s.Read(b, 0, b.Length);
            return b;
        }
    }

    // our shim build always embeds this log filename; .NET string literals live in
    // the #US heap as UTF-16, so match that encoding. The genuine whisper-faster
    // engine does not contain it -> reliable "is this ours?" marker.
    static bool ContainsMarker(byte[] buf)
    {
        byte[] pat = Encoding.Unicode.GetBytes("crispasr-xxl-shim.log");
        for (int i = 0; i + pat.Length <= buf.Length; i++)
        {
            int j = 0;
            while (j < pat.Length && buf[i + j] == pat[j]) j++;
            if (j == pat.Length) return true;
        }
        return false;
    }

    static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // never overwrite a previous backup: <stem><ext>, then a stamped sibling
    static string UniquePath(string dir, string stem, string ext)
    {
        string p = Path.Combine(dir, stem + ext);
        if (!File.Exists(p)) return p;
        return Path.Combine(dir, stem + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ext);
    }

    static string Sha256Prefix(byte[] buf)
    {
        try { return Dl.Sha256Bytes(buf).Substring(0, 16); }
        catch { return "unknown"; }
    }

    // installs the shim into the slot and returns a note about any pre-existing
    // engine exe (null when there was nothing to say)
    static InstallRes Install(string ppDir)
    {
        string dst = Path.Combine(ppDir, @"Engine\Whisper-Faster");
        Directory.CreateDirectory(dst);

        string exePath = Path.Combine(dst, "whisper-faster.exe");
        string iniPath = Path.Combine(dst, "shim.ini");
        byte[] exe = Res("wf.exe");

        // Anything already in the slot is classified before it is touched: byte-identical
        // to what we ship, or carrying our marker -> ours, overwrite it. Everything else
        // (the official engine, or a file we cannot read) is only ever renamed, never
        // deleted -- we cannot hash-match "official" because upstream ships new builds
        // continuously, so "not ours" is the safe, decidable question.
        InstallRes r = new InstallRes();
        bool ours = false;
        if (File.Exists(exePath))
        {
            byte[] cur = null;
            try { cur = File.ReadAllBytes(exePath); } catch { }
            ours = cur != null && (SameBytes(cur, exe) || ContainsMarker(cur));
            if (!ours)
            {
                string real = UniquePath(dst, "whisper-faster.real", ".exe");
                File.Move(exePath, real);
                // that shim.ini was written for the engine we just moved aside; set it
                // aside too -- this installer renames user files, it never deletes one
                string iniBak = null;
                if (File.Exists(iniPath))
                {
                    iniBak = UniquePath(dst, "shim.ini.bak", "");
                    File.Move(iniPath, iniBak);
                }
                r.BackupNote = "原有引擎已备份为 " + Path.GetFileName(real)
                    + "（" + (cur == null ? "无法读取" : cur.Length + " 字节，SHA-256 " + Sha256Prefix(cur) + "…") + "）"
                    + (iniBak == null ? "" : "，原 shim.ini 另存为 " + Path.GetFileName(iniBak));
                Log("backed up non-shim engine: " + real
                    + (iniBak == null ? "" : " ; shim.ini -> " + iniBak));
            }
        }

        File.WriteAllBytes(exePath, exe);
        if (ours && File.Exists(iniPath)) return r; // upgrade in place, keep user's ini

        string tpl = Encoding.UTF8.GetString(Res("ini.txt"));
        if (tpl.Length > 0 && tpl[0] == '\uFEFF') tpl = tpl.Substring(1);

        string crisp = AutoFindCrispasr(dst);
        if (crisp != null)
        {
            string model = AutoFindModel(crisp);
            tpl = ReplaceKey(tpl, "crispasr", crisp);
            if (model != null) tpl = ReplaceKey(tpl, "model", model);
        }
        else
        {
            // nothing found: point at the installer's canonical layout under the
            // chosen directory, so later drops/downloads need no ini editing
            tpl = ReplaceKey(tpl, "crispasr", Path.Combine(dst, @"CrispASR\crispasr.exe"));
            tpl = ReplaceKey(tpl, "model", Path.Combine(dst, @"CrispASR\models\" + ModelQ8));
        }
        string ffdir = Path.Combine(dst, "ffmpeg");
        if (Directory.Exists(ffdir) && File.Exists(Path.Combine(ffdir, "ffmpeg.exe")))
            tpl = ReplaceKey(tpl, "ffmpeg_dir", ffdir);
        File.WriteAllText(iniPath, tpl, new UTF8Encoding(false));
        r.IniWritten = true;
        return r;
    }

    class InstallRes
    {
        public bool IniWritten;       // shim.ini generated fresh this run
        public string BackupNote;     // set when a non-shim engine exe was preserved
    }

    static string ReplaceKey(string text, string key, string value)
    {
        string prefix = key + "=";
        var lines = text.Split('\n');
        bool found = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string ln = lines[i].TrimEnd('\r');
            if (ln.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = prefix + value;
                found = true;
                break;
            }
        }
        // a shim.ini from an older build may predate the key: appending beats
        // silently doing nothing
        if (!found)
        {
            Array.Resize(ref lines, lines.Length + 1);
            lines[lines.Length - 1] = prefix + value;
        }
        return string.Join(Environment.NewLine, lines);
    }

    // crispasr from shim.ini (when that path exists), else common locations
    static string ResolveCrispasr(string iniPath, string engDir)
    {
        try
        {
            foreach (var line in File.ReadAllLines(iniPath, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i > 0 && line.Substring(0, i).Trim().Equals("crispasr", StringComparison.OrdinalIgnoreCase))
                {
                    string v = line.Substring(i + 1).Trim();
                    if (v.Length > 0 && File.Exists(v)) return v;
                }
            }
        }
        catch { }
        return AutoFindCrispasr(engDir);
    }

    static string AutoFindCrispasr(string engDir)
    {
        string[] dirs = {
            Environment.GetEnvironmentVariable("CRISPASR_HOME"),
            Path.Combine(engDir, "CrispASR"),   // placed by this installer
            Path.Combine(ExeDir, "CrispASR"),   // portable layout next to Setup.exe
        };
        foreach (var d in dirs)
        {
            if (string.IsNullOrEmpty(d)) continue;
            string e = Path.Combine(d, "crispasr.exe");
            if (File.Exists(e)) return e;
        }
        return null;
    }

    static string AutoFindModel(string crispasrPath)
    {
        string home = Path.GetDirectoryName(crispasrPath);
        string[] names = { ModelQ8, ModelF16 };
        foreach (var n in names)
        {
            string p = Path.Combine(Path.Combine(home, "models"), n);
            if (File.Exists(p)) return p;
            p = Path.Combine(home, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // refresh the paths shim.ini points at after a download; returns false when
    // shim.ini could not be read or written
    static bool UpdateIniAfterDownload(string iniPath, string engDir, bool sep)
    {
        try
        {
            string text = File.ReadAllText(iniPath, Encoding.UTF8);
            string crisp = Path.Combine(Path.Combine(engDir, "CrispASR"), "crispasr.exe");
            if (File.Exists(crisp))
            {
                text = ReplaceKey(text, "crispasr", crisp);
                string model = AutoFindModel(crisp);
                if (model != null) text = ReplaceKey(text, "model", model);
                string ffdir = Path.Combine(engDir, "ffmpeg");
                if (File.Exists(Path.Combine(ffdir, "ffmpeg.exe"))) text = ReplaceKey(text, "ffmpeg_dir", ffdir);
            }
            // enable only on an explicit "yes" in this run — never silently switch a
            // user's own vocals=1 back off on re-install
            if (sep) text = ReplaceKey(text, "vocals", "1");
            File.WriteAllText(iniPath, text, new UTF8Encoding(false));
            return true;
        }
        catch (Exception e) { Log("ini update failed: " + e.Message); return false; }
    }

    // ============ GPU-aware version picker ============

    static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8); } catch { }
    }

    // sets computeCap (0 when unknown); returns GPU description for the dialog
    public static string DetectGpu(out double computeCap)
    {
        computeCap = 0;
        string name = null;
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,compute_cap --format=csv,noheader")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                var ro = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } }
                string outp = ro.Wait(300) ? ro.Result : "";
                foreach (var raw in outp.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    string[] parts = line.Split(',');
                    name = parts[0].Trim();
                    if (parts.Length > 1)
                    {
                        double d;
                        if (double.TryParse(parts[1].Trim().Trim('"'), out d)) computeCap = d;
                    }
                    break;
                }
            }
        }
        catch { }
        if (name != null)
        {
            if (computeCap >= 12.0) return name + "（计算能力 " + computeCap.ToString("0.0") + "，RTX 50 系/Blackwell）";
            return name + (computeCap > 0 ? "（计算能力 " + computeCap.ToString("0.0") + "）" : "（NVIDIA 显卡）");
        }
        return "未检测到 NVIDIA 显卡（nvidia-smi 不可用）";
    }

    static bool IsValidBuild(string b)
    {
        return b == "cuda13" || b == "cuda" || b == "vulkan" || b == "cpu" || b == "cpu-legacy";
    }

    static void PickComponents(bool quiet, string buildOverride, string modelOverride, string only,
                               string versionOverride, bool sepFlag,
                               out string build, out string model, out bool ff, out bool sep, out string version)
    {
        double cc;
        string gpuDesc = DetectGpu(out cc);
        bool hasNvidia = gpuDesc.IndexOf("未检测到") < 0;
        string suggest = !hasNvidia ? "cpu" : (cc >= 12.0 ? "cuda13" : "cuda");
        build = !string.IsNullOrEmpty(buildOverride) && IsValidBuild(buildOverride) ? buildOverride : suggest;
        model = modelOverride == "f16" ? ModelF16 : ModelQ8;
        ff = only != null && only.Contains("ffmpeg");
        sep = only == null ? sepFlag : only.Contains("sep");
        version = string.IsNullOrEmpty(versionOverride) ? null : versionOverride;
        // scripted modes (quiet, or explicit /build:/only:) skip the dialog
        if (quiet || only != null || buildOverride != null) return;

        using (var dlg = new PickDlg(gpuDesc, build, model, ff, sep))
        {
            if (dlg.ShowDialog() != DialogResult.OK) throw new InvalidOperationException("已取消");
            build = dlg.SelectedBuild;
            model = dlg.SelectedModel;
            ff = dlg.Ffmpeg;
            sep = dlg.Sep;
            // an explicit /version: on the command line outranks the checkbox
            if (string.IsNullOrEmpty(version)) version = dlg.Latest ? "latest" : null;
        }
    }

    class PickDlg : Form
    {
        Dictionary<string, RadioButton> buildRadios = new Dictionary<string, RadioButton>();
        RadioButton q8, f16;
        CheckBox ffBox, sepBox, latestBox;
        public string SelectedBuild, SelectedModel;
        public bool Ffmpeg, Sep, Latest;

        public PickDlg(string gpuDesc, string suggestBuild, string suggestModel, bool suggestFf, bool suggestSep)
        {
            Text = "CrispASR for PotPlayer - 选择要下载的组件";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 494);
            Font = new Font("Microsoft YaHei UI", 9f);

            var gpu = new Label();
            gpu.SetBounds(12, 10, 536, 18);
            gpu.Text = "检测到显卡：" + gpuDesc;
            Controls.Add(gpu);

            var gb1 = new GroupBox();
            gb1.Text = "CrispASR 版本";
            gb1.SetBounds(12, 34, 536, 192);
            Controls.Add(gb1);
            AddBuild(gb1, "cuda13", "CUDA 13 版 — RTX 50 系（Blackwell，如 5060~5090）等新卡", 24);
            AddBuild(gb1, "cuda", "CUDA 12 版 — GTX 10 / RTX 20~40 等更早的 NVIDIA 显卡", 52);
            AddBuild(gb1, "vulkan", "Vulkan 版 — 非 N 卡（AMD / Intel Arc 等）或 CUDA 版报错时", 80);
            AddBuild(gb1, "cpu", "CPU 版 — 无独立显卡；需 CPU 支持 AVX2（Intel 2013+ / AMD 2015+）", 108);
            AddBuild(gb1, "cpu-legacy", "CPU 兼容版 — 更老的 CPU（无 AVX2），速度较慢", 136);
            var tip1 = new Label();
            tip1.SetBounds(10, 164, 520, 18);
            tip1.Text = "不确定就按推荐选；运行失败还可在 shim.ini 设 nogpu=1 回退 CPU。";
            tip1.ForeColor = Color.Gray;
            gb1.Controls.Add(tip1);

            latestBox = new CheckBox();
            latestBox.Text = "检查最新版（默认只装已验证的 " + Dl.PinnedTag + " + SHA-256，勾选则不校验）";
            latestBox.SetBounds(14, 230, 536, 20);
            latestBox.CheckedChanged += delegate { Latest = latestBox.Checked; };
            Controls.Add(latestBox);

            var gb2 = new GroupBox();
            gb2.Text = "日语模型 parakeet-tdt-0.6b-ja（GGUF）";
            gb2.SetBounds(12, 256, 536, 88);
            Controls.Add(gb2);
            q8 = new RadioButton();
            q8.Text = "q8_0（≈642 MB，推荐）— 精度与 f16 几乎无差别，加载更快";
            q8.SetBounds(10, 22, 520, 22);
            f16 = new RadioButton();
            f16.Text = "f16（≈1190 MB）— 原始半精度，追求理论最高精度时选";
            f16.SetBounds(10, 48, 520, 22);
            gb2.Controls.Add(q8); gb2.Controls.Add(f16);

            ffBox = new CheckBox();
            ffBox.Text = "下载 ffmpeg（可选 ≈115 MB）— crispasr 内置解码失败时的兜底解码器；仅 PotPlayer 内用可不装";
            ffBox.SetBounds(14, 352, 536, 22);
            Controls.Add(ffBox);

            sepBox = new CheckBox();
            sepBox.Text = "启用人声分离（默认关闭）— 下载分离模型 ≈436 MB，并自动附带 ffmpeg";
            sepBox.SetBounds(14, 378, 536, 22);
            sepBox.CheckedChanged += delegate
            {
                Sep = sepBox.Checked;
                if (sepBox.Checked) { ffBox.Checked = true; ffBox.Enabled = false; }
                else ffBox.Enabled = true;
            };
            Controls.Add(sepBox);

            var tip2 = new Label();
            tip2.SetBounds(14, 404, 536, 36);
            tip2.ForeColor = Color.Gray;
            tip2.Text = "开启后 PotPlayer 转写前先分离人声，整体耗时约 3 倍（实测 60 秒音频 2.4s → 7.2s），清晰对白"
                      + "素材提升不明显；勾选会在 shim.ini 写入 vocals=1。";
            Controls.Add(tip2);

            var ok = new Button();
            ok.Text = "开始下载";
            ok.SetBounds(330, 450, 100, 32);
            ok.DialogResult = DialogResult.OK;
            var cancel = new Button();
            cancel.Text = "跳过下载";
            cancel.SetBounds(440, 450, 100, 32);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            RadioButton pre;
            buildRadios.TryGetValue(suggestBuild, out pre);
            if (pre != null) pre.Checked = true; else buildRadios["cpu"].Checked = true;
            if (suggestModel == ModelF16) f16.Checked = true; else q8.Checked = true;
            ffBox.Checked = suggestFf || suggestSep;   // separation needs ffmpeg to resample
            ffBox.Enabled = !suggestSep;
            sepBox.Checked = suggestSep;

            SelectedBuild = suggestBuild; SelectedModel = suggestModel;
            Ffmpeg = suggestFf || suggestSep; Sep = suggestSep;
            Latest = false;
        }

        void AddBuild(GroupBox gb, string id, string text, int y)
        {
            var r = new RadioButton();
            r.Text = text;
            r.SetBounds(10, y, 520, 24);
            gb.Controls.Add(r);
            buildRadios[id] = r;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                foreach (var kv in buildRadios)
                    if (kv.Value.Checked) SelectedBuild = kv.Key;
                SelectedModel = f16.Checked ? ModelF16 : ModelQ8;
                Ffmpeg = ffBox.Checked;
                Sep = sepBox.Checked;
            }
            base.OnFormClosing(e);
        }
    }
}
