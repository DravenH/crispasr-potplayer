# crispasr-potplayer

把 [CrispASR](https://github.com/CrispStrobe/CrispASR) + NVIDIA **parakeet-tdt-0.6b-ja**（日语 GGUF 模型）
接入 PotPlayer 内置的"声音生成字幕"功能：在 PotPlayer 引擎下拉里选 **Whisper-Faster**，
即用一个极小的 C# 垫片（~14KB）把 PotPlayer 的 Purfview 引擎命令行转译成 crispasr 命令，
直接把整部影片的日语音轨转成 SRT 字幕。

实测（RTX 5090，CUDA 构建，2 小时 27 分电影 / 283MB WAV）：**约 36 秒完成，≈246× 实时**，
1400+ 条字幕，等待体验接近即时。

> 非官方第三方集成：本项目与 PotPlayer（Kakao/Daum）、CrispASR 及其作者、NVIDIA 均无关联，
> 也未获任何背书。全部第三方组件由用户自行从官方源下载，本仓库不内置、不重新分发任何
> 二进制或模型权重（详见文末 [第三方许可与致谢](#第三方许可与致谢)）。

## 背景：PotPlayer 是怎么调引擎的

PotPlayer"声音生成字幕"（离线菜单）以**子进程 + 命令行**方式调用引擎，
引擎槽位是硬编码的两个目录（无自定义引擎配置项）：

| 下拉里的引擎名 | PotPlayer 实际运行的文件 |
|---|---|
| Whisper-Faster | `Engine\Whisper-Faster\whisper-faster.exe` |
| （另一槽位） | `Engine\Faster-Whisper-XXL\faster-whisper-xxl.exe`（本项目不使用） |

调用形态（从 PotPlayer64.dll 还原）：

```
whisper-faster.exe "<导出到%TEMP%的整片音频>.tmp" --model large-v3 --model_dir "..." --output_dir "%TEMP%" --compute_type float16 --language Japanese --beep_off --output_format srt
```

本项目的垫片伪装成其中一个 exe，解析上述参数后转成：

```
crispasr -m <model.gguf> -f <audio> -l ja --vad -osrt -of <out>\<base> --split-on-punct --flush-after 1
```

并把产出的 SRT 放到 PotPlayer 期望的位置（`<输出目录>\<音频同名>.srt`）。

> ⚠️ "声音生成字幕**（实时）**"菜单不走任何 exe——它在进程内直接调用
> `Module\Whisper\whisper64.dll`（whisper.cpp C ABI），无法挂接 CrispASR。
> 但离线模式在 GPU 上是 200×+ 实时，等一次全片转录只要几十秒，实际不缺"实时"。

## 依赖（需自行下载，本仓库不含）

1. **crispasr.exe** — [CrispASR Releases](https://github.com/CrispStrobe/CrispASR/releases)
   - NVIDIA RTX 50 系（sm_120）需 **cuda13** 构建；老卡选对应 CUDA 版或 CPU 版
   - 国内直连 GitHub 慢时可用 `https://gh-proxy.org/<原链接>`
2. **日语模型 GGUF** — `parakeet-tdt-0.6b-ja-GGUF`（f16 / q8_0）
   - huggingface.co 直连被墙时用镜像：`https://hf-mirror.com/cstr/parakeet-tdt-0.6b-ja-GGUF`
   - 注意：nvidia 官方仓库只有 `.nemo` 格式，crispasr 用不了，必须用 GGUF 转换版
3. **ffmpeg**（可选）— CrispASR 优先用内置 miniaudio 解码，PotPlayer 导出的标准 WAV
   用不到 ffmpeg；它只在 miniaudio 解不了时（非常规编码、少数损坏的 dump）被 crispasr
   作为最后兜底调用。仅 PotPlayer 内使用可以不装；若还要跑本仓库的
   `tools\transcribe-ja.ps1` 批量脚本（从 mkv/mp4 抽音轨），则**必须**有 ffmpeg。
   放进 `shim.ini` 的 `ffmpeg_dir`（或同目录 `ffmpeg\`）即可注入兜底。
4. 首次运行 `--vad` 会自动下载 silero VAD 模型到 `%USERPROFILE%\.cache\crispasr\`；
   下载失败时手动放置 `ggml-silero-v6.2.0.bin`
   （可从 `https://hf-mirror.com/Freeda/ggml-silero-v6.2.0.bin` 获取）

> 不想手动下载？安装器能自动拉取 1–3（见下文"安装"），全部跳过本节。

## 安装

**方式一：图形安装器（推荐）** —— 下载 **`CrispASR-PotPlayer-Setup.exe`**
（[Releases 页](https://github.com/DravenH/crispasr-potplayer/releases/latest)，
国内直连慢时可走 `https://gh-proxy.org/https://github.com/DravenH/crispasr-potplayer/releases/latest/download/CrispASR-PotPlayer-Setup.exe`）
后双击运行；clone 本仓库的话直接双击 `bin\CrispASR-PotPlayer-Setup.exe` 也一样：

- 自动扫描 PotPlayer 安装目录（注册表卸载项 32/64 位视图 + 常见安装路径，
  认 32/64 位全部四种主程序名），找不到时弹出文件夹选择框
- 把垫片写入 `<PotPlayer>\Engine\Whisper-Faster\`：
  若该目录已有**官方 whisper-faster 引擎**（安装器通过 exe 内容识别，非我们发的垫片），
  自动改名为 `whisper-faster.real.exe` 备份、绝不删除——想换回官方引擎时改回原名即可；
  已装有自己配置过的旧版垫片时**覆盖 exe、保留你的 shim.ini**（其余情况 shim.ini 重新生成）
- 新生成的 `shim.ini` **按你选择的安装目录自动写好组件路径**（默认指向
  `Engine\Whisper-Faster\CrispASR\`），全程无需手动编辑配置文件
- 未检测到 CrispASR 时可**自动下载全部组件**：安装器会用 `nvidia-smi` 读取显卡
  计算能力并预选版本，弹框里可手动改：
  | 选项 | 提示 |
  |---|---|
  | CrispASR CUDA13 版 | RTX 50 系（Blackwell）等新 N 卡 |
  | CrispASR CUDA12 版 | GTX 10 / RTX 20~40 等更早的 N 卡 |
  | CrispASR Vulkan 版 | AMD / Intel 显卡，或 CUDA 版报错时 |
  | CrispASR CPU 版（含 legacy） | 无独显；legacy 供不支持 AVX2 的老 CPU |
  | 模型 q8_0 ≈642MB（推荐）/ f16 ≈1190MB | 精度几乎无差别，q8_0 加载更快 |
  | ffmpeg ≈115MB（可选勾选） | crispasr 内置解码失败时的兜底解码器；仅 PotPlayer 内用可不装 |
  默认**只下载已验证固定的上游版本 v0.8.36**，下载完逐个核对内置 SHA-256
  （不匹配会重试一次后报错，绝不解压安装），**不会自动跟随“最新版”**；
  确需升级时在弹框勾选“检查最新版”（此路径不校验哈希）或用 `/version:<tag>` 指定；
  直连失败走 `gh-proxy.org`，模型走 hf-mirror → huggingface 回退，
  落盘到 `<PotPlayer>\Engine\Whisper-Faster\CrispASR\`，并自动写入 `shim.ini`
- PotPlayer 装在 `Program Files` 等受保护目录时，请右键 → **以管理员身份运行**
- 也支持静默安装（脚本/无人值守）：

  ```
  CrispASR-PotPlayer-Setup.exe /quiet "X:\Path\To\PotPlayer"                :: 只装垫片
  CrispASR-PotPlayer-Setup.exe /quiet "X:\..." /download                     :: 按显卡自动选版下载组件
  ... /download /build:cuda13 /model:f16                                     :: 指定版本（可加 /only:crisp+model）
  ... /download /version:latest                                              :: 显式跟最新版（跳过哈希校验）
  ```

  退出码：0 成功 / 2 未找到 PotPlayer / 3 写入失败 / 4 下载失败

**方式二：手动** —— 把 `bin\whisper-faster.exe` 放进 `<PotPlayer>\Engine\Whisper-Faster\`，
并把下载好的 CrispASR 放在同目录 `CrispASR\` 子目录（即 `CrispASR\crispasr.exe`、
`CrispASR\models\<模型>.gguf`，与安装器布局一致）——这种布局下 `shim.ini` 会自动生成
且路径正确，无需编辑。若你把组件放在别的位置，则复制 `config\shim.ini.example` 为
`shim.ini` 并填写 `crispasr` / `model` 路径。装完重启 PotPlayer 即可。

32 位与 64 位 PotPlayer 通用（垫片是 AnyCPU 的 .NET 4 程序）；若你已在用正版
whisper-faster / faster-whisper-xxl 引擎，互不冲突：本项目只占
`Engine\Whisper-Faster` 目录，原版 XXL 槽位不受影响。

## 版本与校验值

安装器内置了下面这张表（`src\Download.cs` 的 `PinnedTag` / `AssetSha` / `ModelSha`）：
默认只从 **v0.8.36** 下载，落盘后按 SHA-256 校验，不匹配就重试一次并报错终止，
**不会解压安装未通过校验的文件**。模型值取自 HuggingFace 的 LFS oid（即文件本体 sha256），
镜像站 hf-mirror 提供的是同一份字节，因此同样适用。

```
crispasr-windows-x86_64-cpu.zip         1d8c853d102671f4036ccf4da8573a6d9ed3d45ae4530aa07573760a4bc93dc1
crispasr-windows-x86_64-cpu-legacy.zip  fb0b8555343daf434533e53d4eca2d10726f991f5014008e50af64607f184bd1
crispasr-windows-x86_64-vulkan.zip      659e6cc1d3d0c7d65e1ce2df61efd7295c5b017e8a95c4d340c20ba70793d9cc
crispasr-windows-x86_64-cuda13.zip      d81795954af9b9f08ccd43ab875e6db8d538881fef3b910e7b6bddd07617a98f
crispasr-windows-x86_64-cuda.zip        4d14ce34cbc089259e897bed369214f6f920efa31e3236845bb6c7464ed7fba0
parakeet-tdt-0.6b-ja-q8_0.gguf          5a61e6c7d956c3c72a76fafcd798cac0c9ea66d0e29b3910cd04865a1e42cc17
parakeet-tdt-0.6b-ja.gguf               374eb0132eebaec4df77a9631cbbeb03790be48a4a517f6cc8e8bdb38fe9a584
parakeet-tdt-0.6b-ja-q4_k.gguf          9a9bdfec5a1f119983a00367d33fb310759d67619309f02500f649c5328ab825
```

两点已知例外：

- **ffmpeg 不校验**：gyan.dev 的 `ffmpeg-release-essentials.zip` 是滚动地址，
  内容随版本变化，无法固定哈希；安装器只取其中的 `ffmpeg.exe` / `ffprobe.exe`。
- **升级路径不校验**：勾选"检查最新版"或传 `/version:<其它 tag>` 时，
  下载的是本表之外的文件，自然无哈希可比。

维护者升级流程：换 `build` 实测新版可用 → 更新 `PinnedTag` 与 `AssetSha`
（`sha256sum` 或 `Get-FileHash` 自行取值）→ 重新编译发布。

## 使用

PotPlayer 播放影片 → 右键菜单 / 字幕菜单 → **声音生成字幕** →
引擎选 **Whisper-Faster**（模型、语言下拉随意，垫片固定用 ini 里的模型、语言固定 `ja`，
因为 parakeet-tdt-0.6b-ja 是日语专用模型）。

转录过程中 `%TEMP%\<音频名>.srt` 会渐进增长（`--flush-after 1` 的效果），可当作进度条。

## shim.ini 配置项

| 键 | 说明 | 默认（未写此项时自动探测） |
|---|---|---|
| `crispasr` | crispasr.exe 路径 | 垫片同目录 `CrispASR\crispasr.exe`，再退 `crispasr.exe` |
| `model` | GGUF 模型路径 | 依序找 `CrispASR\models\` 与 `models\` 下的 q8_0 → f16 |
| `language` | 兜底语言（ISO） | `ja` |
| `extra` | 追加给 crispasr 的参数 | `--split-on-punct --flush-after 1` |
| `nogpu` | `1` 强制 CPU | `0` |
| `log` | `1` 记录到 `%TEMP%\crispasr-xxl-shim.log` | `1` |
| `ffmpeg_dir` | 注入 crispasr 子进程 PATH 的目录 | 同目录 `ffmpeg\` 存在则自动启用 |

安装器生成的 `shim.ini` 已按所选安装目录写好 `crispasr` / `model` 绝对路径；
手动部署时若目录结构与安装器一致（`CrispASR\` + `CrispASR\models\`），
**连 shim.ini 都可以不放**，垫片会按上表默认自动探测。

## 故障排查

- **Windows SmartScreen 拦截**（"已保护你的电脑 / Windows 已阻止启动未识别的应用"）：
  安装器与垫片都**未做代码签名**（个人项目不购买证书），且安装器会从 GitHub 下载并运行
  crispasr.exe，这两点合起来容易触发 SmartScreen 与部分杀软的启发式告警。
  在拦截弹窗上点 **更多信息 → 仍要运行** 即可；不放心的话先自行核对文件哈希再运行，
  也可以完全跳过安装器、按 [方式二](#安装) 手动复制文件。
- **弹窗报错/无字幕**：先看 `%TEMP%\crispasr-xxl-shim.log`，每行含时间戳；
  `=== invoked:` 是 PotPlayer 传来的原始命令，`crispasr cmd:` 是转译结果。
- `miniaudio and ffmpeg both failed`：垫片已内置对策（等待文件写稳 → 探测可读性 →
  私有 `.wav` 副本喂给 crispasr），若仍出现，基本是 crispasr 到 ffmpeg 兜底也失败，
  检查 `ffmpeg_dir` 与音频是否损坏。
- `exit=20`：多为语言名不被 crispasr 接受；垫片会把 `Japanese/中文/...` 全名映射为 ISO 码。
- 换其他语言/模型：改 `shim.ini` 的 `model` 与 `language` 即可（任何 crispasr 支持的 GGUF 都行），
  未知语言全名当前兜底为 `ja`，见 `src\XxlShim.cs` 的 `LangMap`。

## 独立批量转录（不依赖 PotPlayer）

`tools\transcribe-ja.ps1`：ffmpeg 抽音轨 →（可选 `-Vocals` mel-band-roformer 人声分离，
抗 BGM）→ crispasr → 同名 `.srt`。

```powershell
.\transcribe-ja.ps1 "D:\動画\ep01.mkv"           # 单文件
.\transcribe-ja.ps1 "D:\動画"                     # 整个目录
.\transcribe-ja.ps1 "D:\動画\ep01.mkv" -Vocals    # 先分离人声
.\transcribe-ja.ps1 "..." -CrispDir "C:\tools\CrispASR"   # 指定工具链目录
```

工具链按 `-CrispDir` → `%CRISPASR_HOME%` → 脚本目录内的 `CrispASR\` 逐级探测，
ffmpeg 另探测 PATH；也可用 `-CrispExe/-Model/-Ffmpeg` 精确指定。

## 从源码构建

```
src\build.bat
```

仅需 .NET Framework 4.x（Win10/11 自带），无任何第三方依赖。
一次产出两个文件：`bin\whisper-faster.exe`（垫片）与
`bin\CrispASR-PotPlayer-Setup.exe`（安装器，垫片与 ini 模板以资源形式内嵌）。
`bin\` 内已附最新预编译产物。

## License

本项目自身代码（`src\`、`config\`、`tools\`）采用 **MIT**，见 `LICENSE`。

## 第三方许可与致谢

本仓库**不含**下列任何组件的二进制或模型权重；安装器只是引导你的机器从官方源下载，
下载后各文件的上游许可证（crispasr 的 zip 里自带 `LICENSE` 与 `THIRD_PARTY_NOTICES.txt`）
会原样保留在安装目录中。

| 组件 | 用途 | 许可证 | 来源 |
|---|---|---|---|
| [CrispASR](https://github.com/CrispStrobe/CrispASR) | 实际执行识别的引擎 | **MIT** | GitHub Releases |
| [parakeet-tdt_ctc-0.6b-ja](https://huggingface.co/nvidia/parakeet-tdt_ctc-0.6b-ja) | NVIDIA 日语语音模型 | **CC-BY-4.0** | Hugging Face |
| [parakeet-tdt-0.6b-ja-GGUF](https://huggingface.co/cstr/parakeet-tdt-0.6b-ja-GGUF) | 上述模型的 GGUF 转换版（本项目实际下载） | **CC-BY-4.0** | Hugging Face（国内走 hf-mirror 镜像） |
| [silero VAD](https://github.com/snakers4/silero-vad) | crispasr `--vad` 首次运行时自动下载的静音检测模型 | 见上游仓库声明 | 由 crispasr 自行下载 |
| [ffmpeg](https://www.gyan.dev/ffmpeg/builds/) | 解码兜底 / 批量脚本抽音轨 | **GPL-3.0**（gyan 构建） | gyan.dev |
| [PotPlayer](https://potplayer.daum.net/) | 宿主播放器，提供引擎槽位 | 专有免费软件 | 官方站点 |

感谢 CrispASR、NVIDIA（Parakeet 模型）、ggml/whisper.cpp 与 silero 的作者们把工具和模型开源。
CC-BY-4.0 要求再分发模型时保留署名——你若把模型文件复制给他人，请连同本表格一并转达出处。

## 免责说明

- 转录他人享有版权的音视频（包括番剧、电影、播客）是否合规，**由使用者自行负责**；
  本项目只提供技术链路，不构成任何版权方面的建议或授权。
- 安装器默认只下载**已验证固定的上游版本**并逐个核对内置 SHA-256（见
  [版本与校验值](#版本与校验值)），不会静默跟随最新版；
  勾选"检查最新版"或用 `/version:latest` 时不做哈希校验，请自行确认来源可信。
  若你介意供应链风险，也可用 [方式二](#安装) 手动下载、自行核对哈希后部署。
- 软件按"现状"提供，不含任何明示或暗示的保证；因使用本项目导致的数据丢失、
  系统问题等后果，作者不承担责任。
