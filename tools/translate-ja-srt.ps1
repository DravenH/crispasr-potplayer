<#
.SYNOPSIS
  把日文字幕整片翻成中文（本地 ollama，和 PotPlayer 实时翻译插件用同一个模型）
.DESCRIPTION
  读入 .srt，逐行批量翻译，产出 <影片名>.cn.srt（纯中文，PotPlayer 按影片名自动加载）
  与 <影片名>.ja+cn.srt（日中上下双语）。源文件若是 ep.ja.srt，输出会去掉 .ja 后缀。
  默认请求本机 ollama（http://127.0.0.1:11434/api/chat），全程本地、不联网、不花钱。
  走原生 /api/chat 而不是 v1 兼容层：v1 不认 num_ctx，模型会按上限 131072 预留 KV 缓存
  并溢出到内存，实测每行 6~8 秒；原生口下 num_ctx 生效，每行 0.06 秒（约 100 倍差距）。
  num_ctx 默认压到 4096，就是为了让模型连缓存一起完整待在显存里。
  已翻过的行写进 <输出名>.cache.jsonl，中断后重跑自动续翻，不会重复请求。
  每批按 1..N 编号对齐，模型漏答的行自动降级为逐行重问一次。
  实测（RTX 5090 D + huihui_ai/hy-mt1.5-abliterated，模型常驻显存）：1433 行日幕 77s。
.EXAMPLE
  .\translate-ja-srt.ps1 "D:\動画\ep01.ja.srt"              # 单文件
  .\translate-ja-srt.ps1 "D:\動画"                           # 目录下所有 .srt
  .\translate-ja-srt.ps1 "..." -Batch 20                     # 每次请求翻译 20 行
  .\translate-ja-srt.ps1 "..." -Model qwen3:14b              # 换本地其他模型
  .\translate-ja-srt.ps1 "..." -Force                        # 忽略缓存重翻
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Inputs,
    [string]$Model = 'huihui_ai/hy-mt1.5-abliterated:latest',
    [string]$Server = 'http://127.0.0.1:11434',
    [int]$Batch = 12,
    [int]$NumCtx = 4096,
    [string]$KeepAlive = '10m',
    [int]$TimeoutSec = 300,
    [switch]$NoBilingual,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$encBom = New-Object System.Text.UTF8Encoding($true)
$encRaw = New-Object System.Text.UTF8Encoding($false)

function Sha1([string]$s) {
    $h = New-Object System.Security.Cryptography.SHA1Managed
    $b = [System.Text.Encoding]::UTF8.GetBytes($s)
    -join ($h.ComputeHash($b) | ForEach-Object { $_.ToString('x2') })
}

function Post-Text([string]$prompt) {
    $obj = @{ model = $Model; stream = $false; keep_alive = $KeepAlive;
              options = @{ temperature = 0; num_ctx = $NumCtx };
              messages = @(@{ role = 'user'; content = $prompt }) }
    $json = $obj | ConvertTo-Json -Depth 6 -Compress
    # 走 ollama 原生 /api/chat：v1 兼容层同样的内容慢 10~20 倍；
    # 也不用 Invoke-RestMethod：它把响应按本地代码页解码，中文会变乱码。
    $req = [System.Net.WebRequest]::Create($Server + '/api/chat')
    $req.Method = 'POST'
    $req.ContentType = 'application/json; charset=utf-8'
    $req.Proxy = $null
    $req.Timeout = $TimeoutSec * 1000
    $req.ReadWriteTimeout = $TimeoutSec * 1000
    $body = [System.Text.Encoding]::UTF8.GetBytes($json)
    $req.ContentLength = $body.Length
    $rs = $req.GetRequestStream(); $rs.Write($body, 0, $body.Length); $rs.Close()
    $resp = $req.GetResponse()
    $ms = New-Object System.IO.MemoryStream
    $resp.GetResponseStream().CopyTo($ms)
    $resp.Close()
    $text = [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
    return [string](($text | ConvertFrom-Json).message.content)
}

# 一批（最多 $count 行，编号 1..$count）→ 编号->译文
function Request-Batch([string[]]$lines, [string[]]$ctx, [int]$count) {
    $prompt = "你是一个日语字幕翻译引擎，专门将日语影视字幕翻译为自然、克制的中文。`n"
    $prompt += "请将以下带编号的日语字幕逐行翻译为中文。`n"
    $prompt += "严格遵守以下规则：`n"
    $prompt += "1. 只输出结果行，每行格式为「编号. 中文译文」，编号与输入一一对应，不得增删行、不得合并。`n"
    $prompt += "2. 不要输出任何解释、前言、注释或说明。`n"
    $prompt += "3. 不要擅自补充主语（如我/你/他/她），除非日语原文明确出现。`n"
    $prompt += "4. 保留原句的暧昧性和未说完的感觉，不要把含糊表达翻译得过于确定。`n"
    $prompt += "5. 正确体现语气词和情绪（如さ、ね、よ、ぞ、か），用中文语气而非直译。`n"
    $prompt += "6. 敬语翻译为克制、礼貌的中文，而不是书面或官腔表达。`n"
    $prompt += "7. 语言风格以自然口语为主，符合日剧对白节奏。`n"
    if ($ctx.Length -gt 0) {
        $prompt += "`n以下对白仅用于理解人物与语气，不要翻译：`n'''" + ($ctx -join "`n") + "`n'''`n"
    }
    $prompt += "`n待翻译日语字幕：`n'''" + ($lines -join "`n") + "`n'''"

    $resp = Post-Text $prompt
    $out = @{}
    foreach ($l in ($resp -split "`r?`n")) {
        if ($l -match '^\s*(\d+)\s*[\.、\)]\s*(.*)$') {
            $n = [int]$Matches[1]
            if ($n -ge 1 -and $n -le $count -and -not $out.ContainsKey($n)) { $out[$n] = $Matches[2].Trim() }
        }
    }
    return $out
}

# ---- 收集待处理文件 ----
$files = @()
foreach ($in in $Inputs) {
    if (Test-Path -LiteralPath $in -PathType Container) {
        $files += @(Get-ChildItem -LiteralPath $in -Filter '*.srt' -Recurse | ForEach-Object { $_.FullName })
    } elseif (Test-Path -LiteralPath $in -PathType Leaf) {
        $files += @(Resolve-Path -LiteralPath $in).Path
    } else {
        Write-Warning "找不到: $in"
    }
}
$files = @($files | Where-Object { $_ -notmatch '\.cn\.srt$' -and $_ -notmatch '\.ja\+cn\.srt$' } | Select-Object -Unique)
if ($files.Count -eq 0) { Write-Warning '没有可处理的 .srt'; return }

# ---- 服务预检 ----
try {
    $g = [System.Net.WebRequest]::Create($Server.TrimEnd('/') + '/api/tags')
    $g.Proxy = $null
    $g.Timeout = 10000
    $gr = $g.GetResponse()
    $gms = New-Object System.IO.MemoryStream
    $gr.GetResponseStream().CopyTo($gms); $gr.Close()
    $tags = [System.Text.Encoding]::UTF8.GetString($gms.ToArray()) | ConvertFrom-Json
    $names = @($tags.models | ForEach-Object { $_.name })
    $hit = @($names | Where-Object { $_ -eq $Model -or $Model -like ($_ + ':*') -or $_ -like ($Model + ':*') })
    if ($hit.Count -eq 0) {
        Write-Warning ("ollama 里没有模型 " + $Model + "，先执行: ollama pull " + $Model)
        Write-Host ("本地已有: " + ($names -join ', '))
        return
    }
} catch {
    Write-Warning ("连不上 " + $Server + "（" + $_.Exception.Message + "），请先运行 ollama serve")
    return
}

foreach ($file in $files) {
    $dir = Split-Path -Parent $file
    $stem = [IO.Path]::GetFileNameWithoutExtension($file)
    # ep.ja.srt -> 输出 ep.cn.srt（PotPlayer 按影片名自动加载），双语叫 ep.ja+cn.srt
    if ($stem -match '(?i)\.(ja|jp|jpn|japanese|日语|日本语)$') { $stem = $stem -replace '(?i)\.(ja|jp|jpn|japanese|日语|日本语)$', '' }
    $outCn = Join-Path $dir ($stem + '.cn.srt')
    $outBi = Join-Path $dir ($stem + '.ja+cn.srt')
    $cacheFile = Join-Path $dir ($stem + '.cn.srt.cache.jsonl')
    $cache = @{}
    if (-not $Force -and (Test-Path -LiteralPath $cacheFile)) {
        foreach ($l in [IO.File]::ReadAllLines($cacheFile, $encRaw)) {
            if ($l.Trim()) {
                try { $o = $l | ConvertFrom-Json; $cache[(Sha1 $o.s)] = $o.t } catch { }
            }
        }
    }

    # ---- 解析 SRT：块 = 序号 / 时间 / 若干文本行 ----
    $raw = [IO.File]::ReadAllText($file, $encBom) -replace "`r", ''
    $blocks = @($raw -split "`n`n+" | Where-Object { $_.Trim() })
    $cues = @()
    foreach ($b in $blocks) {
        $ls = $b -split "`n"
        $ti = -1
        for ($k = 0; $k -lt $ls.Count; $k++) { if ($ls[$k] -match '-->') { $ti = $k; break } }
        if ($ti -lt 0) { continue }
        $src = @($ls[($ti + 1)..($ls.Count - 1)] | Where-Object { $_.Trim() -ne '' })
        if ($src.Count -eq 0) { continue }
        $cues += [pscustomobject]@{ head = ($ls[0..($ti - 1)] -join "`n"); time = $ls[$ti]; src = $src }
    }
    if ($cues.Count -eq 0) { Write-Warning ("解析不到字幕块: " + $file); continue }

    $lines = @(); $owner = @()
    for ($c = 0; $c -lt $cues.Count; $c++) {
        for ($i = 0; $i -lt $cues[$c].src.Count; $i++) { $lines += $cues[$c].src[$i]; $owner += $c }
    }
    $todo = @()
    for ($i = 0; $i -lt $lines.Count; $i++) { if (-not $cache.ContainsKey((Sha1 $lines[$i]))) { $todo += $i } }
    Write-Host ("{0}  字幕块 {1} / 文本行 {2}，需翻译 {3} 行" -f (Split-Path -Leaf $file), $cues.Count, $lines.Count, $todo.Count)

    $cacheStream = New-Object System.IO.StreamWriter($cacheFile, $true, $encRaw)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $reqs = 0
    $translated = 0
    try {
        $cursor = 0
        $ctx = @()
        while ($cursor -lt $todo.Count) {
            $end = $todo.Count - 1
            if ($todo.Count - $cursor -ge $Batch) { $end = $cursor + $Batch - 1 }
            $take = @($todo[$cursor..$end])
            $cursor += $take.Count
            $got = @{}
            try {
                $got = Request-Batch (@(0..($take.Count - 1) | ForEach-Object { "{0}. {1}" -f ($_ + 1), $lines[$take[$_]] })) $ctx $take.Count
                $reqs++
            } catch {
                Write-Warning ("批量请求失败: " + $_.Exception.Message)
            }
            for ($k = 0; $k -lt $take.Count; $k++) {
                $li = $take[$k]
                $tr = $null
                if ($got.ContainsKey($k + 1)) { $tr = [string]$got[$k + 1] }
                if (-not $tr) {
                    try {
                        $one = Request-Batch @("1. " + $lines[$li]) $ctx 1
                        $reqs++
                        if ($one.ContainsKey(1)) { $tr = [string]$one[1] }
                    } catch { }
                }
                if (-not $tr) { $tr = ''; Write-Warning ("第 {0} 行无译文，保留空行" -f ($li + 1)) }
                $h = Sha1 $lines[$li]
                if (-not $cache.ContainsKey($h)) { $cache[$h] = $tr }
                $cacheStream.WriteLine((@{ s = $lines[$li]; t = $tr } | ConvertTo-Json -Compress))
                $cacheStream.Flush()
                $ctx = @('日语: ' + $lines[$li], '中文: ' + $tr)
                $translated++
            }
            $el = $sw.Elapsed.TotalSeconds
            $eta = if ($translated -gt 0) { $el / $translated * ($todo.Count - $translated) } else { 0 }
            Write-Host ("  {0}/{1} 行  已用 {2:N0}s  预计还需 {3:N0}s" -f $translated, $todo.Count, $el, $eta)
        }
    } finally {
        $cacheStream.Dispose()
    }

    # ---- 写出 ----
    $sbCn = New-Object System.Text.StringBuilder
    $sbBi = New-Object System.Text.StringBuilder
    foreach ($cue in $cues) {
        $zh = @($cue.src | ForEach-Object { $cache[(Sha1 $_)] })
        $sbCn.AppendLine($cue.head); $sbCn.AppendLine($cue.time); $sbCn.AppendLine(($zh -join "`n")); $sbCn.AppendLine() | Out-Null
        $sbBi.AppendLine($cue.head); $sbBi.AppendLine($cue.time)
        for ($i = 0; $i -lt $cue.src.Count; $i++) { $sbBi.AppendLine($cue.src[$i]); $sbBi.AppendLine([string]$zh[$i]) }
        $sbBi.AppendLine() | Out-Null
    }
    [IO.File]::WriteAllText($outCn, $sbCn.ToString(), $encBom)
    Write-Host ("-> " + $outCn)
    if (-not $NoBilingual) {
        [IO.File]::WriteAllText($outBi, $sbBi.ToString(), $encBom)
        Write-Host ("-> " + $outBi)
    }
    Write-Host ("完成：请求 {0} 次，用时 {1:N0}s" -f $reqs, $sw.Elapsed.TotalSeconds)
}
