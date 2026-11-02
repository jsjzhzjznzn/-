# 重新生成 .dsh/skills 下的技能链接（可重复执行、已存在的会跳过）
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File .dsh/relink-skills.ps1
#   pwsh        -NoProfile -File .dsh/relink-skills.ps1
# 注意：本文件必须保存成「UTF-8 带 BOM」。Windows PowerShell 5.1 会把无 BOM 的 UTF-8 当 GBK 解码，
#       中文注释会把后面的花括号吞掉，直接报 Unexpected token '}'。pwsh 7 两种编码都能读。
$ErrorActionPreference = 'Stop'
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$root   = Split-Path -Parent $PSScriptRoot
$skills = Join-Path $root '.dsh/skills'
$tjSrc  = Join-Path $root '.codely-cli/extensions/TJGenerators/skills'
$tmpSrc = Join-Path $root '.codely-cli/extensions/TMPChineseFont/skills/TMPChineseFont'

if (-not (Test-Path $tjSrc)) { throw "找不到 Codely 技能目录: $tjSrc (请先在 Codely 中安装该扩展)" }
New-Item -ItemType Directory -Force -Path $skills | Out-Null

# 这两个技能的 description 里含有冒号+空格，严格 YAML 解析会失败。
# 改成 DSH 自有副本，并用折叠块标量（>-）重写 frontmatter。
$fixDirs = @('generate_3d_model', 'generate_animated_character')

function ConvertTo-BlockScalar([string]$md) {
    $lines = $md -split "`r?`n"
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        if ($line -match '^description:\s*(.*)$') {
            $out.Add('description: >-')
            $buf = ''
            foreach ($w in ($Matches[1] -split '\s+')) {
                if ($buf.Length -gt 0 -and ($buf.Length + $w.Length + 1) -gt 100) { $out.Add('  ' + $buf); $buf = $w }
                else { $buf = if ($buf) { "$buf $w" } else { $w } }
            }
            if ($buf) { $out.Add('  ' + $buf) }
        } else { $out.Add($line) }
    }
    return ($out -join "`n")
}

foreach ($d in Get-ChildItem -LiteralPath $tjSrc -Directory) {
    $srcMd = Join-Path $d.FullName 'SKILL.md'
    if (-not (Test-Path $srcMd)) { continue }
    $link = Join-Path $skills $d.Name

    if ($fixDirs -contains $d.Name) {
        $dstMd = Join-Path $link 'SKILL.md'
        if (Test-Path $dstMd) {
            $existing = [IO.File]::ReadAllText($dstMd, $Utf8NoBom)
            if ($existing -match '(?m)^description:\s*[>|]') { continue }
        }
        if (Test-Path $link) { Remove-Item -LiteralPath $link -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $link | Out-Null
        Copy-Item -Path (Join-Path $d.FullName '*') -Destination $link -Recurse -Force
        $text = [IO.File]::ReadAllText($dstMd, $Utf8NoBom)
        [IO.File]::WriteAllText($dstMd, (ConvertTo-BlockScalar $text), $Utf8NoBom)
    }
    elseif (-not (Test-Path $link)) {
        cmd /c mklink /J "$link" "$($d.FullName)" | Out-Null
    }
}

# TMP 中文字体：原 name 是 TMPChineseFont，DSH 只接受 kebab-case，改成 tmp-chinese-font。
if (Test-Path $tmpSrc) {
    $dst   = Join-Path $skills 'tmp-chinese-font'
    $dstMd = Join-Path $dst 'SKILL.md'
    $need  = $true
    if (Test-Path $dstMd) {
        $existing = [IO.File]::ReadAllText($dstMd, $Utf8NoBom)
        if ($existing -match '(?m)^name:\s*tmp-chinese-font') { $need = $false }
    }
    if ($need) {
        if (Test-Path $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-Item -Path (Join-Path $tmpSrc '*') -Destination $dst -Recurse -Force
        $text = [IO.File]::ReadAllText($dstMd, $Utf8NoBom) -replace '(?m)^name:\s*TMPChineseFont', 'name: tmp-chinese-font'
        [IO.File]::WriteAllText($dstMd, $text, $Utf8NoBom)
    }
}

# 项目自有技能：.codebuddy/skills 下的 SkierFramework UI 三件套 + 测试登录规范
# （skier-ui-create / skier-ui-edit / skier-ui-yooasset / test-from-real-login）。
# 它们的 name 已是 kebab-case、description 里没有裸冒号，严格 YAML 也能过，直接建 junction。
# 注意：这些 SKILL.md 是照本工程真实流程写的，正文里不出现 Codely 工具名，DSH 里可原样使用。
$cbSrc = Join-Path $root '.codebuddy/skills'
if (Test-Path $cbSrc) {
    foreach ($d in Get-ChildItem -LiteralPath $cbSrc -Directory) {
        if (-not (Test-Path (Join-Path $d.FullName 'SKILL.md'))) { continue }
        if ($d.Name -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') {
            Write-Warning "跳过 $($d.Name)：目录名不是 kebab-case，DSH 可能拒绝加载"
            continue
        }
        $link = Join-Path $skills $d.Name
        if (-not (Test-Path $link)) { cmd /c mklink /J "$link" "$($d.FullName)" | Out-Null }
    }
} else {
    Write-Warning "找不到项目技能目录: $cbSrc"
}

Get-ChildItem -LiteralPath $skills | Select-Object Name, LinkType
