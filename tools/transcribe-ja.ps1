<#
.SYNOPSIS
  日语视频/音频转录 SRT（CrispASR + parakeet-tdt-0.6b-ja，CUDA 加速）
.DESCRIPTION
  ffmpeg 提取音频 -> (可选)人声分离 -> CrispASR 识别 -> 同名 .srt
  工具链探测顺序：-CrispExe/-Model/-Ffmpeg 参数 > -CrispDir / %CRISPASR_HOME% >
  脚本目录内 CrispASR\ > PATH。
.EXAMPLE
  .\transcribe-ja.ps1 "D:\動画\ep01.mkv"                # 单文件
  .\transcribe-ja.ps1 "D:\動画"                          # 整个目录
  .\transcribe-ja.ps1 "D:\動画\ep01.mkv" -Vocals         # 先分离人声（BGM 重的番剧建议开）
  .\transcribe-ja.ps1 "D:\動画\ep01.mkv" -NoGpu          # 强制 CPU
  .\transcribe-ja.ps1 "..." -CrispDir "C:\tools\CrispASR"
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Inputs,
    [switch]$Vocals,      # mel-band-roformer 人声分离，抗 BGM
    [switch]$NoGpu,       # 加 --no-gpu
    [switch]$KeepWav,     # 保留中间 wav
    [string]$CrispDir,    # CrispASR 根目录（含 crispasr.exe 与 models\）
    [string]$CrispExe,
    [string]$Model,
    [string]$Ffmpeg
)

$ErrorActionPreference = 'Stop'
$script:Root = $PSScriptRoot

function FirstExists([string[]]$candidates) {
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

# Join-Path that tolerates a $null parent
function SJoin([string]$a, [string]$b) { if ($a) { Join-Path $a $b } }

# ---- 工具链根目录探测 ----
$script:CrispHome = FirstExists @(
    $CrispDir,
    $env:CRISPASR_HOME,
    (Join-Path $script:Root 'CrispASR')
)

# ---- crispasr.exe ----
if (-not $CrispExe) {
    $local = @(Get-ChildItem -Path $script:Root -Recurse -Filter 'crispasr.exe' -ErrorAction SilentlyContinue)
    $localGpu = @($local | Where-Object { $_.FullName -match '\\gpu' })
    $localExe = if ($localGpu) { $localGpu[0].FullName } elseif ($local) { $local[0].FullName } else { $null }
    $CrispExe = FirstExists @((SJoin $script:CrispHome 'crispasr.exe'), $localExe)
}
# ---- ffmpeg ----
if (-not $Ffmpeg) {
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    $Ffmpeg = FirstExists @($cmd.Source,
                            (SJoin $script:CrispHome 'ffmpeg\ffmpeg.exe'),
                            (SJoin $script:CrispHome '..\ffmpeg\ffmpeg.exe'))
}
# ---- 模型 ----
if (-not $Model) {
    $Model = FirstExists @((SJoin $script:CrispHome 'models\parakeet-tdt-0.6b-ja-q8_0.gguf'),
                           (SJoin $script:CrispHome 'models\parakeet-tdt-0.6b-ja.gguf'),
                           (Join-Path $script:Root 'parakeet-tdt-0.6b-ja-q8_0.gguf'),
                           (Join-Path $script:Root 'parakeet-tdt-0.6b-ja.gguf'))
}
$SepModel = FirstExists @((SJoin $script:CrispHome 'models\mel-band-roformer-vocals-f16.gguf'))

if (-not $CrispExe) { throw "找不到 crispasr.exe（用 -CrispDir 或 -CrispExe 指定）" }
if (-not $Ffmpeg)   { throw "找不到 ffmpeg.exe（用 -Ffmpeg 指定，或加入 PATH）" }
if (-not $Model)    { throw "找不到日语 GGUF 模型（用 -Model 指定）" }
if ($Vocals -and -not $SepModel) { throw "开了 -Vocals 但找不到 mel-band-roformer 分离模型" }

$script:CrispExe = $CrispExe
$script:Model = $Model
$script:Ffmpeg = $Ffmpeg
$script:SepModel = $SepModel
$script:NoGpu = $NoGpu
$script:KeepWav = $KeepWav

Write-Host "crispasr : $CrispExe"
Write-Host "model    : $Model"
Write-Host "ffmpeg   : $Ffmpeg"
if ($Vocals) { Write-Host "separate : $SepModel" }

$script:AudioExt = @('.wav', '.flac', '.mp3', '.ogg', '.opus', '.m4a', '.aac', '.wma')

function Invoke-Transcription {
    param([IO.FileInfo]$file)
    $base = [IO.Path]::ChangeExtension($file.FullName, $null).TrimEnd('.')
    $srt  = "$base.srt"
    if (Test-Path $srt) { Write-Host "[跳过] 已存在 $srt"; return }

    $tmpFiles = @()
    try {
        # ---- 1. 提取音频 ----
        if ($script:AudioExt -contains $file.Extension.ToLower()) {
            $wav = $file.FullName
        } else {
            $ar = if ($script:Vocals) { '44100' } else { '16000' }
            $ac = if ($script:Vocals) { '2' } else { '1' }
            $wav = Join-Path $env:TEMP "$($file.BaseName)_$ar.wav"
            $tmpFiles += $wav
            Write-Host "[1/3] ffmpeg 提取音频 ($ar Hz / $ac ch)..."
            & $script:Ffmpeg -y -hide_banner -loglevel error -i $file.FullName -vn -ac $ac -ar $ar -c:a pcm_s16le $wav
            if ($LASTEXITCODE -ne 0) { throw "ffmpeg 提取失败: $($file.Name)" }
        }

        # ---- 2. 人声分离（可选） ----
        if ($script:Vocals) {
            Write-Host "[2/3] mel-band-roformer 分离人声..."
            $vocal = [IO.Path]::ChangeExtension($wav, $null).TrimEnd('.') + '_vocals.wav'
            $tmpFiles += $vocal
            & $script:CrispExe --separate -m $script:SepModel -f $wav --stems vocals | Out-Null
            if (-not (Test-Path $vocal)) { throw "人声分离失败（未生成 $vocal）" }
            $wav = $vocal
        } else {
            Write-Host "[2/3] 跳过人声分离"
        }

        # ---- 3. 转录 ----
        Write-Host "[3/3] parakeet 日语转录..."
        $t0 = Get-Date
        $asrArgs = @('-m', $script:Model, '-f', $wav, '-l', 'ja', '--vad', '-osrt', '--split-on-punct', '-of', $base)
        if ($script:NoGpu) { $asrArgs += '--no-gpu' }
        & $script:CrispExe @asrArgs
        Write-Host ("完成，用时 {0:n1}s，exit={1}" -f ((Get-Date) - $t0).TotalSeconds, $LASTEXITCODE)

        if (Test-Path $srt) { Write-Host "[输出] $srt" } else { Write-Warning "未生成 SRT，检查上方 crispasr 输出" }
    }
    finally {
        if (-not $script:KeepWav) { $tmpFiles | ForEach-Object { Remove-Item $_ -ErrorAction SilentlyContinue } }
    }
}

$script:Vocals = $Vocals

# ---- 输入展开：文件 / 目录 / 混合 ----
foreach ($in in $Inputs) {
    if (Test-Path $in -PathType Container) {
        Get-ChildItem $in -File |
            Where-Object { $_.Extension -match '^\.(mkv|mp4|avi|flv|ts|webm|wmv|rmvb|mov|m4v|mpg|mpeg|opus|ogg|mp3|wav|flac|m4a|aac|wma)$' } |
            ForEach-Object { Invoke-Transcription $_ }
    } elseif (Test-Path $in -PathType Leaf) {
        Invoke-Transcription (Get-Item $in)
    } else {
        Write-Warning "路径不存在: $in"
    }
}
