// CrispASR-PotPlayer installer core: extracts whisper-faster.exe (+ shim.ini
// template) into <PotPlayer>\Engine\Whisper-Faster. Supports 32/64-bit PotPlayer.
// Optionally auto-downloads runtime components (see Download.cs) after a version
// picker dialog with GPU-aware hints.
// The runtime, the model files and shim.ini are checked independently on every
// run: re-running the installer repairs whatever went missing since (a deleted
// model, a renamed folder, a stale ini path) without re-downloading what is fine.
// GUI double-click flow, plus silent CLI:
//   Setup.exe ["X:\PotPlayer"] /quiet [/download] [/build:cpu|cuda|cuda13|vulkan]
//             [/model:f16] [/only:crisp,model,ffmpeg,sep] [/version:v0.8.37|latest]
//             [/sep] [/force]
// /sep (人声分离) 默认关；勾选/传入后会连带下载 ffmpeg 并把 shim.ini 的 vocals 写成 1。
// /force 忽略"已装好就跳过"，强制重下请求到的每个组件。
// Downloads default to the hash-pinned release; /version: opts out of that pin.
// A deliberate skip/cancel is not an error: the shim is installed and shim.ini is
// still repaired, so such a run exits 0.
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
        bool quiet = false, downloadFlag = false, sepFlag = false, forceFlag = false;
        foreach (var a in args)
        {
            if (a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("/s", StringComparison.OrdinalIgnoreCase)) quiet = true;
            else if (a.Equals("/download", StringComparison.OrdinalIgnoreCase)) downloadFlag = true;
            else if (a.Equals("/sep", StringComparison.OrdinalIgnoreCase)) sepFlag = true;
            else if (a.Equals("/force", StringComparison.OrdinalIgnoreCase)) forceFlag = true;
            else if (a.StartsWith("/build:", StringComparison.OrdinalIgnoreCase)) buildOverride = a.Substring(7).ToLowerInvariant();
            else if (a.StartsWith("/model:", StringComparison.OrdinalIgnoreCase)) modelOverride = a.Substring(7).ToLowerInvariant();
            else if (a.StartsWith("/only:", StringComparison.OrdinalIgnoreCase)) only = a.Substring(6).ToLowerInvariant();
            else if (a.StartsWith("/version:", StringComparison.OrdinalIgnoreCase)) versionOverride = a.Substring(9).Trim().Trim('"');
            else if (!a.StartsWith("/")) dir = a.Trim('"');
        }

        if (!string.IsNullOrEmpty(dir) && !IsPotPlayer(dir))
        {
            if (quiet) { Log("bad potplayer dir: " + dir); return 2; }
            MessageBox.Show(Owner(), "该目录下找不到 PotPlayer 主程序：\n" + dir,
                "CrispASR for PotPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            dir = null;
        }
        if (string.IsNullOrEmpty(dir)) dir = Probe();
        if (string.IsNullOrEmpty(dir) && !quiet) dir = AskFolder();
        if (string.IsNullOrEmpty(dir))
        {
            if (!quiet)
                MessageBox.Show(Owner(), "未能定位 PotPlayer 安装目录。\n请把本程序所在路径作为参数运行：\nSetup.exe \"X:\\Path\\To\\PotPlayer\"",
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
                MessageBox.Show(Owner(), "安装失败：" + e.Message + hint,
                    "CrispASR for PotPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }

        // ---- what is actually missing, judged piece by piece ----
        // The runtime, the speech model and (when vocals are on) the separation
        // model + ffmpeg are probed separately, and shim.ini is repaired on its own
        // afterwards: finding a working crispasr.exe must never skip the other two.
        string dlError = null;
        Exception dlEx = null;
        bool didDownload = false;
        var notes = new List<string>();
        bool vocals1 = sepFlag;             // write vocals=1 for the /sep path
        try
        {
            Comp miss = Missing(engDir, iniPath, sepFlag);
            Log("audit: missing = " + Describe(miss));

            bool wantDl;
            if (quiet) wantDl = downloadFlag;
            else if (downloadFlag) wantDl = true;
            else if (miss == Comp.None) wantDl = false;
            else wantDl = AskYesNo("检测到缺少以下组件：\n" + DescribeLines(miss)
                + "\n是否自动下载？\n将写入 " + engDir + @"\CrispASR\（下一步可确认具体版本）。");

            if (wantDl)
            {
                string build, model, version;
                Comp want = PickComponents(quiet, buildOverride, modelOverride, only, versionOverride,
                                           sepFlag, forceFlag, engDir, iniPath,
                                           out build, out model, out version, out vocals1);
                if (want == Comp.None)
                    notes.Add("所需组件都已在位并通过 SHA-256 校验，未重复下载。"
                            + "确要重装请加 /force（或在下一屏勾选\"重新下载运行时\"）。");
                else
                {
                    Log("download: " + Describe(want));
                    Dl.Run(engDir, build, model, want, forceFlag, only != null, version);
                    didDownload = true;
                }
            }
        }
        catch (Exception e)
        {
            dlError = e.Message;
            dlEx = e;
            Log("download failed: " + e);
        }

        // The ini pass runs whatever happened to the download: it only points keys at
        // files that exist right now, so skipping or failing a download still repairs a
        // stale path that would make the shim exit 2.
        try
        {
            string repaired = FixIni(iniPath, engDir, vocals1);
            if (repaired == null)
                notes.Add((didDownload ? "组件已下载完成，但 shim.ini 更新失败" : "shim.ini 检查失败")
                    + "，请手动确认其中的路径（日志：" + LogPath + "）");
            else if (repaired.Length > 0)
                notes.Add("已修正 shim.ini 中失效的配置项：" + repaired + "。");
        }
        catch (Exception e)
        {
            Log("ini pass failed: " + e.Message);
            notes.Add("shim.ini 检查失败，请手动确认其中的路径（日志：" + LogPath + "）");
        }
        // SepShortfall answers for itself: null unless vocals=1 is really set
        string shortfall = SepShortfall(engDir, iniPath);
        if (shortfall != null) notes.Add(shortfall);

        // silent mode has no dialog to carry these, so the log is the record
        foreach (var n in notes) Log("note: " + n);

        if (quiet) return dlEx is Skipped || dlError == null ? 0 : 4;
        if (dlError != null)
        {
            bool skipped = dlEx is Skipped;               // the picker's 跳过下载 button
            bool cancelled = !skipped && Dl.WasCancelled(dlEx);
            MessageBox.Show(Owner(), (skipped ? "已跳过下载（垫片本身已安装成功）。"
                          : (cancelled ? "已取消下载（垫片本身已安装成功）：\n" : "组件下载失败（垫片本身已安装成功）：\n")
                             + dlError + "。")
                + "\n\n可参考 README 手动下载对应组件，放进目录后重跑安装器即可（只补真正缺的那部分）。"
                + "\n详细日志：" + LogPath,
                "CrispASR for PotPlayer", MessageBoxButtons.OK,
                skipped || cancelled ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        Comp still = Missing(engDir, iniPath, sepFlag);
        string tail = didDownload ? "\n组件已下载完成，shim.ini 路径已按安装目录自动配置。"
            : (still == Comp.None
                ? "\n各组件均已就位（已逐项核对），本次未下载。"
                : "\n仍未就绪：" + Describe(still) + "。\n放进 " + engDir + @"\CrispASR\ 或重跑本安装器下载即可，shim.ini 无需手改。");
        if (res != null && res.BackupNote != null)
            tail += "\n" + res.BackupNote + "，把它改回 whisper-faster.exe 即可还原官方引擎。";
        foreach (var n in notes) tail += "\n" + n;
        MessageBox.Show(Owner(),
            "安装完成：\n" + Path.Combine(engDir, "whisper-faster.exe") + tail
            + "\n\n重启 PotPlayer 后，在『声音生成字幕』引擎下拉选 Whisper-Faster 即可。",
            "CrispASR for PotPlayer", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return dlEx is Skipped || dlError == null ? 0 : 4;
    }

    static bool AskYesNo(string text)
    {
        return MessageBox.Show(Owner(), text, "CrispASR for PotPlayer",
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
                if (dlg.ShowDialog(Owner()) != DialogResult.OK) return null;
                if (IsPotPlayer(dlg.SelectedPath)) return dlg.SelectedPath;
                MessageBox.Show(Owner(), "所选目录里没有 PotPlayer 主程序，请重新选择。", "CrispASR for PotPlayer",
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

    // ============ window and taskbar icon ============
    // -win32icon only styles the exe file in Explorer. Every window we open still has
    // to be handed the icon itself, and a bare MessageBox takes its owner's.
    static Icon _icon;
    public static Icon AppIcon()
    {
        if (_icon == null)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (s != null) _icon = new Icon(s);
            }
            catch (Exception e) { Log("icon unavailable: " + e.Message); }
        }
        return _icon;
    }

    static Form _owner;
    // never shown, just handle-created: the shell draws a dialog's taskbar button from its
    // root owner's icon (a #32770 caption has no icon of its own), and owning it also
    // centres the dialog on screen instead of dumping it on the top-left corner
    static IWin32Window Owner()
    {
        if (_owner == null || _owner.IsDisposed)
        {
            _owner = new Form();
            _owner.Icon = AppIcon();
            _owner.ShowInTaskbar = false;
            _owner.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            _owner.StartPosition = FormStartPosition.CenterScreen;
            _owner.Size = new Size(3, 3);
            var handle = _owner.Handle;
        }
        return _owner;
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
            // an empty key is what makes the shim search on its own; leaving the
            // template's example path behind would instead fail on a dead path
            tpl = ReplaceKey(tpl, "crispasr", crisp);
            tpl = ReplaceKey(tpl, "model", AutoFindModel(Path.GetDirectoryName(crisp)) ?? "");
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

    class Skipped : Exception          // the picker's 跳过下载 button: a deliberate no
    {
        public Skipped() : base("已跳过下载") { }
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
        string v = IniVal(ReadIni(iniPath), "crispasr");
        if (v.Length > 0 && File.Exists(v)) return v;
        return AutoFindCrispasr(engDir);
    }

    static string ReadIni(string iniPath)
    {
        try { return File.ReadAllText(iniPath, Encoding.UTF8); }
        catch { return ""; }
    }

    // same key=value lookup the shim itself uses (no inline comments: the shim
    // takes everything after '=' as the value)
    static string IniVal(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf('=');
            if (i > 0 && line.Substring(0, i).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line.Substring(i + 1).Trim();
        }
        return "";
    }

    static string ModelDir(string engDir) { return Path.Combine(Path.Combine(engDir, "CrispASR"), "models"); }
    static string SepPath(string engDir) { return Path.Combine(ModelDir(engDir), Dl.SepModel); }

    static string CrispHome(string iniPath, string engDir)
    {
        string c = ResolveCrispasr(iniPath, engDir);
        return c == null ? null : Path.GetDirectoryName(c);
    }

    // ---- per-piece probes: each component answers for itself ----
    static bool CrispOk(string iniPath, string engDir) { return ResolveCrispasr(iniPath, engDir) != null; }

    // the exact quant the download step would fetch, where it would land (or where
    // shim.ini already points at it)
    static bool ModelOk(string iniPath, string engDir, string model)
    {
        string p = IniVal(ReadIni(iniPath), "model");
        if (Path.GetFileName(p).Equals(model, StringComparison.OrdinalIgnoreCase) && Dl.GgufOk(p)) return true;
        string home = CrispHome(iniPath, engDir);
        if (home != null && Dl.GgufOk(Path.Combine(Path.Combine(home, "models"), model))) return true;
        return Dl.GgufOk(Path.Combine(ModelDir(engDir), model));
    }

    // any usable speech model counts: the shim only needs one to run
    static bool AnySpeechModelOk(string iniPath, string engDir)
    {
        if (Dl.GgufOk(IniVal(ReadIni(iniPath), "model"))) return true;
        string home = CrispHome(iniPath, engDir);
        return home != null && AutoFindModel(home) != null;
    }

    static bool FfmpegOk(string iniPath, string engDir)
    {
        return FindFfmpegDir(iniPath, engDir) != null;
    }

    // mirrors the shim's own lookup: ffmpeg_dir, the installer's ffmpeg\ folder, PATH
    static string FindFfmpegDir(string iniPath, string engDir)
    {
        string d = IniVal(ReadIni(iniPath), "ffmpeg_dir");
        if (HasExe(d, "ffmpeg.exe")) return d;
        d = Path.Combine(engDir, "ffmpeg");
        if (HasExe(d, "ffmpeg.exe")) return d;
        foreach (var e in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrEmpty(e)) continue;
            if (HasExe(e.Trim(), "ffmpeg.exe")) return e.Trim();
        }
        return null;
    }

    // shim.ini values are hand-edited and can hold characters Path.Combine rejects
    // ('|', a stray tab from copy-pasting). A path we cannot even build is simply absent.
    static bool HasExe(string dir, string exe)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        try { return File.Exists(Path.Combine(dir, exe)); }
        catch { return false; }
    }

    static bool SepOk(string iniPath, string engDir)
    {
        string p = IniVal(ReadIni(iniPath), "separation_model");
        return Dl.GgufOk(p) || Dl.GgufOk(SepPath(engDir));
    }

    // Which pieces are absent or damaged. vocals=1 (or /sep) drags in the separation
    // model and ffmpeg; with vocals=0 neither is required, so neither is offered.
    static Comp Missing(string engDir, string iniPath, bool sepFlag)
    {
        Comp c = Comp.None;
        if (!CrispOk(iniPath, engDir)) c |= Comp.Crisp;
        if (!AnySpeechModelOk(iniPath, engDir)) c |= Comp.Model;
        if (sepFlag || IniVal(ReadIni(iniPath), "vocals") == "1")
        {
            if (!SepOk(iniPath, engDir)) c |= Comp.Sep;
            if (!FfmpegOk(iniPath, engDir)) c |= Comp.Ffmpeg;
        }
        return c;
    }

    static string Describe(Comp c)
    {
        var l = new List<string>();
        if ((c & Comp.Crisp) != 0) l.Add("CrispASR 运行时");
        if ((c & Comp.Model) != 0) l.Add("日语模型 parakeet-tdt-0.6b-ja");
        if ((c & Comp.Sep) != 0) l.Add("人声分离模型");
        if ((c & Comp.Ffmpeg) != 0) l.Add("ffmpeg");
        return string.Join("、", l.ToArray());
    }

    static string DescribeLines(Comp c)
    {
        var l = new List<string>();
        if ((c & Comp.Crisp) != 0) l.Add("· CrispASR 运行时（≈100 MB）");
        if ((c & Comp.Model) != 0) l.Add("· 日语模型 parakeet-tdt-0.6b-ja（q8_0 ≈642 MB）");
        if ((c & Comp.Sep) != 0) l.Add("· 人声分离模型（≈436 MB）");
        if ((c & Comp.Ffmpeg) != 0) l.Add("· ffmpeg（分离需要，≈115 MB）");
        return string.Join("\n", l.ToArray()) + "\n";
    }

    // vocals=1 without the files it needs is not an error -- the shim just skips
    // separation -- but the user should hear about it here rather than in a log.
    static string SepShortfall(string engDir, string iniPath)
    {
        if (IniVal(ReadIni(iniPath), "vocals") != "1") return null;
        Comp c = Comp.None;
        if (!SepOk(iniPath, engDir)) c |= Comp.Sep;
        if (!FfmpegOk(iniPath, engDir)) c |= Comp.Ffmpeg;
        if (c == Comp.None) return null;
        // /only:ffmpeg pulls just ffmpeg; /only:sep pulls the sep model plus its ffmpeg
        string what = c == Comp.Ffmpeg ? "ffmpeg" : "sep";
        return "注意：shim.ini 里 vocals=1，但缺少 " + Describe(c)
            + "，运行时会跳过人声分离。补下：Setup.exe /quiet \"<PotPlayer 目录>\" /download /only:" + what;
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

    // first intact speech model next to a crispasr install; `home` is that exe's folder
    static string AutoFindModel(string home)
    {
        if (string.IsNullOrEmpty(home)) return null;
        string[] names = { ModelQ8, ModelF16 };
        foreach (var n in names)
        {
            string p = Path.Combine(Path.Combine(home, "models"), n);
            if (Dl.GgufOk(p)) return p;
            p = Path.Combine(home, n);
            if (Dl.GgufOk(p)) return p;
        }
        return null;
    }

    // shim.ini repair: touch ONLY keys whose configured path no longer resolves
    // (deleted file, renamed folder, PotPlayer moved to another drive), and only when
    // there is something real to point at instead. A live hand-edited path -- a model
    // on another drive, CRISPASR_HOME -- is never rewritten, so re-running the
    // installer can't clobber a working config. Returns the changed key names
    // ("" when the file was already fine, null when it could not be read or written).
    static string FixIni(string iniPath, string engDir, bool vocals1)
    {
        string text;
        try { text = File.ReadAllText(iniPath, Encoding.UTF8); }
        catch (Exception e) { Log("ini repair: cannot read " + iniPath + " : " + e.Message); return null; }
        var changed = new List<string>();

        string crisp = IniVal(text, "crispasr");
        string wantCrisp = File.Exists(crisp) ? crisp : AutoFindCrispasr(engDir);
        if (wantCrisp != null && wantCrisp != crisp)
        {
            text = ReplaceKey(text, "crispasr", wantCrisp);
            changed.Add("crispasr");
            crisp = wantCrisp;
        }
        if (crisp.Length > 0)
        {
            string m = IniVal(text, "model");
            string wantM = Dl.GgufOk(m) ? m : AutoFindModel(Path.GetDirectoryName(crisp));
            if (wantM != null && wantM != m)
            {
                text = ReplaceKey(text, "model", wantM);
                changed.Add("model");
            }
        }

        string sep = IniVal(text, "separation_model");
        if (sep.Length > 0 && !Dl.GgufOk(sep))
        {
            string sp = SepPath(engDir);
            text = ReplaceKey(text, "separation_model", Dl.GgufOk(sp) ? sp : "");
            changed.Add("separation_model");
        }

        string ffdir = IniVal(text, "ffmpeg_dir");
        if (ffdir.Length > 0 && !HasExe(ffdir, "ffmpeg.exe"))
        {
            string cand = Path.Combine(engDir, "ffmpeg");
            text = ReplaceKey(text, "ffmpeg_dir", HasExe(cand, "ffmpeg.exe") ? cand : "");
            changed.Add("ffmpeg_dir");
        }

        // vocals is latched on by this run's own choice only -- never switched back off
        if (vocals1 && IniVal(text, "vocals") != "1")
        {
            text = ReplaceKey(text, "vocals", "1");
            changed.Add("vocals");
        }

        if (changed.Count == 0) return "";
        try { File.WriteAllText(iniPath, text, new UTF8Encoding(false)); }
        catch (Exception e) { Log("ini repair: cannot write " + iniPath + " : " + e.Message); return null; }
        string s = string.Join("、", changed.ToArray());
        Log("shim.ini repaired: " + s);
        return s;
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

    // Ask what the user wants, then hand back what is actually worth downloading.
    // Returning Comp.None means "everything you asked for is already in place" --
    // Main reports that instead of silently sitting through a re-download.
    static Comp PickComponents(bool quiet, string buildOverride, string modelOverride, string only,
                               string versionOverride, bool sepFlag, bool force, string engDir, string iniPath,
                               out string build, out string model, out string version, out bool vocals1)
    {
        double cc;
        string gpuDesc = DetectGpu(out cc);
        bool hasNvidia = gpuDesc.IndexOf("未检测到") < 0;
        string suggest = !hasNvidia ? "cpu" : (cc >= 12.0 ? "cuda13" : "cuda");
        build = !string.IsNullOrEmpty(buildOverride) && IsValidBuild(buildOverride) ? buildOverride : suggest;
        model = modelOverride == "f16" ? ModelF16 : ModelQ8;
        // no quant asked for explicitly: keep the one that is already intact on disk
        if (string.IsNullOrEmpty(modelOverride)) model = SuggestModel(iniPath, engDir, model);
        version = string.IsNullOrEmpty(versionOverride) ? null : versionOverride;

        Comp want, forced = Comp.None;
        bool sep;
        if (only != null)
        {
            // /only: names exactly the pieces to install -> an explicit request, honoured
            sep = only.Contains("sep");
            want = Comp.None;
            if (only.Contains("crisp")) want |= Comp.Crisp;
            if (only.Contains("model")) want |= Comp.Model;
            if (sep) want |= Comp.Sep;
            if (only.Contains("ffmpeg") || sep) want |= Comp.Ffmpeg;
            forced = want;
        }
        else
        {
            sep = sepFlag;
            want = Comp.Crisp | Comp.Model;
            if (sep) want |= Comp.Sep | Comp.Ffmpeg;   // separation resamples via ffmpeg
            // an explicit /build: or /model: means "install this one now", not "check
            // whether I already have something that works"
            if (!string.IsNullOrEmpty(buildOverride)) forced |= Comp.Crisp;
            if (!string.IsNullOrEmpty(modelOverride)) forced |= Comp.Model;
        }

        if (quiet || only != null || buildOverride != null)
            return Narrow(want, forced, force, engDir, iniPath, model, out vocals1);

        using (var dlg = new PickDlg(gpuDesc, build, model, sep,
                                     CrispOk(iniPath, engDir), AnySpeechModelOk(iniPath, engDir)))
        {
            if (dlg.ShowDialog() != DialogResult.OK) throw new Skipped();
            build = dlg.SelectedBuild;
            model = dlg.SelectedModel;
            want = Comp.Crisp | Comp.Model;
            if (dlg.Ffmpeg) want |= Comp.Ffmpeg;
            if (dlg.Sep) want |= Comp.Sep | Comp.Ffmpeg;
            if (dlg.ReinstallRuntime) forced |= Comp.Crisp;
            // an explicit /version: on the command line outranks the checkbox
            if (string.IsNullOrEmpty(version)) version = dlg.Latest ? "latest" : null;
        }
        return Narrow(want, forced, force, engDir, iniPath, model, out vocals1);
    }

    // keep only the pieces that are genuinely absent; force (/force) keeps them all
    static Comp Narrow(Comp want, Comp forced, bool force, string engDir, string iniPath, string model,
                       out bool vocals1)
    {
        vocals1 = (want & Comp.Sep) != 0;
        if (force) return want;
        if ((want & Comp.Crisp) != 0 && (forced & Comp.Crisp) == 0 && CrispOk(iniPath, engDir)) want &= ~Comp.Crisp;
        if ((want & Comp.Model) != 0 && (forced & Comp.Model) == 0 && ModelOk(iniPath, engDir, model)) want &= ~Comp.Model;
        if ((want & Comp.Sep) != 0 && (forced & Comp.Sep) == 0 && SepOk(iniPath, engDir)) want &= ~Comp.Sep;
        if ((want & Comp.Ffmpeg) != 0 && (forced & Comp.Ffmpeg) == 0 && FfmpegOk(iniPath, engDir)) want &= ~Comp.Ffmpeg;
        return want;
    }

    // offer the quant that is already on disk, so a repair run doesn't "switch" models
    static string SuggestModel(string iniPath, string engDir, string def)
    {
        if (ModelOk(iniPath, engDir, def)) return def;
        if (ModelOk(iniPath, engDir, ModelQ8)) return ModelQ8;
        if (ModelOk(iniPath, engDir, ModelF16)) return ModelF16;
        return def;
    }

    class PickDlg : Form
    {
        Dictionary<string, RadioButton> buildRadios = new Dictionary<string, RadioButton>();
        RadioButton q8, f16;
        CheckBox ffBox, sepBox, latestBox, reBox;
        public string SelectedBuild, SelectedModel;
        public bool Ffmpeg, Sep, Latest, ReinstallRuntime;

        public PickDlg(string gpuDesc, string suggestBuild, string suggestModel, bool suggestSep,
                       bool hasRuntime, bool hasModel)
        {
            Text = "CrispASR for PotPlayer - 选择要下载的组件";
            Icon = AppIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 518);
            Font = new Font("Microsoft YaHei UI", 9f);

            var gpu = new Label();
            gpu.SetBounds(12, 10, 536, 18);
            gpu.Text = "检测到显卡：" + gpuDesc;
            Controls.Add(gpu);

            var gb1 = new GroupBox();
            gb1.Text = hasRuntime
                ? "CrispASR 版本（目录里已有运行时，默认不重下）"
                : "CrispASR 版本";
            gb1.SetBounds(12, 34, 536, 214);
            Controls.Add(gb1);
            AddBuild(gb1, "cuda13", "CUDA 13 版 — RTX 50 系（Blackwell，如 5060~5090）等新卡", 24);
            AddBuild(gb1, "cuda", "CUDA 12 版 — GTX 10 / RTX 20~40 等更早的 NVIDIA 显卡", 52);
            AddBuild(gb1, "vulkan", "Vulkan 版 — 非 N 卡（AMD / Intel Arc 等）或 CUDA 版报错时", 80);
            AddBuild(gb1, "cpu", "CPU 版 — 无独立显卡；需 CPU 支持 AVX2（Intel 2013+ / AMD 2015+）", 108);
            AddBuild(gb1, "cpu-legacy", "CPU 兼容版 — 更老的 CPU（无 AVX2），速度较慢", 136);
            var tip1 = new Label();
            tip1.SetBounds(10, 162, 520, 18);
            tip1.Text = "不确定就按推荐选；运行失败还可在 shim.ini 设 nogpu=1 回退 CPU。";
            tip1.ForeColor = Color.Gray;
            gb1.Controls.Add(tip1);

            reBox = new CheckBox();
            reBox.Text = "重新下载 CrispASR 运行时（≈100 MB）— 换 CUDA/CPU 版本或怀疑装坏了才需要";
            reBox.SetBounds(10, 186, 520, 20);
            reBox.Visible = hasRuntime;
            reBox.CheckedChanged += delegate { ReinstallRuntime = reBox.Checked; };
            gb1.Controls.Add(reBox);

            latestBox = new CheckBox();
            latestBox.Text = "检查最新版（默认只装已验证的 " + Dl.PinnedTag + " + SHA-256，勾选则不校验）";
            latestBox.SetBounds(14, 254, 536, 20);
            latestBox.CheckedChanged += delegate { Latest = latestBox.Checked; };
            Controls.Add(latestBox);

            var gb2 = new GroupBox();
            gb2.Text = hasModel
                ? "日语模型 parakeet-tdt-0.6b-ja（已有校验通过的模型，选中项已存在则跳过）"
                : "日语模型 parakeet-tdt-0.6b-ja（GGUF）";
            gb2.SetBounds(12, 280, 536, 88);
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
            ffBox.SetBounds(14, 376, 536, 22);
            Controls.Add(ffBox);

            sepBox = new CheckBox();
            sepBox.Text = "启用人声分离（默认关闭）— 下载分离模型 ≈436 MB，并自动附带 ffmpeg";
            sepBox.SetBounds(14, 402, 536, 22);
            sepBox.CheckedChanged += delegate
            {
                Sep = sepBox.Checked;
                if (sepBox.Checked) { ffBox.Checked = true; ffBox.Enabled = false; }
                else ffBox.Enabled = true;
            };
            Controls.Add(sepBox);

            var tip2 = new Label();
            tip2.SetBounds(14, 428, 536, 36);
            tip2.ForeColor = Color.Gray;
            tip2.Text = "开启后 PotPlayer 转写前先分离人声，整体耗时约 3 倍（实测 60 秒音频 2.4s → 7.2s），清晰对白"
                      + "素材提升不明显；勾选会在 shim.ini 写入 vocals=1。";
            Controls.Add(tip2);

            var ok = new Button();
            ok.Text = "开始下载";
            ok.SetBounds(330, 474, 100, 32);
            ok.DialogResult = DialogResult.OK;
            var cancel = new Button();
            cancel.Text = "跳过下载";
            cancel.SetBounds(440, 474, 100, 32);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            RadioButton pre;
            buildRadios.TryGetValue(suggestBuild, out pre);
            if (pre != null) pre.Checked = true; else buildRadios["cpu"].Checked = true;
            if (suggestModel == ModelF16) f16.Checked = true; else q8.Checked = true;
            ffBox.Checked = suggestSep;              // separation needs ffmpeg to resample
            ffBox.Enabled = !suggestSep;
            sepBox.Checked = suggestSep;

            SelectedBuild = suggestBuild; SelectedModel = suggestModel;
            Ffmpeg = suggestSep; Sep = suggestSep;
            Latest = false; ReinstallRuntime = false;
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
                ReinstallRuntime = reBox.Checked;
            }
            base.OnFormClosing(e);
        }
    }
}
