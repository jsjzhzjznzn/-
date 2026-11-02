<#
.SYNOPSIS
    把一张 GIF 表情拆成逐帧 PNG，输出到运行时表情目录（Assets/Resources/biaoq/<表情键>/）。

.DESCRIPTION
    Unity/Tuanjie 不能播放 GIF，所以动画表情采用「编辑器期抽帧 + 运行时轮播帧」的方案：
      1) 源 GIF 留在 Assets/Art/biaoq（不进包）；
      2) 本脚本把它拆成 <表情键>_000.png / <表情键>_001.png ... 写进 Assets/Resources/biaoq/<表情键>/；
      3) 运行时 StickerCatalog 靠「文件名 = 表情键_三位序号」认出动画帧（同名子目录就是动画表情），
         同名 gif 本身不进 Resources，因此不会和帧冲突。
    同时生成 sticker.txt（frames=帧数 / fps=播放帧率），StickerCatalog 会读它来决定播放速度。

    「表情键」= 输出目录名（也就是 GIF 的文件名，不含扩展名），例如 DAC41DA9030EA59E8995DF1CD3105292。
    帧文件的键前缀就是它——StickerCatalog 完全靠这个命名约定自动发现表情，不需要任何清单文件。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File extract_gif_frames.ps1 `
        -GifPath .\DAC41DA9030EA59E8995DF1CD3105292.gif `
        -OutDir ..\..\Resources\biaoq\DAC41DA9030EA59E8995DF1CD3105292
#>
param(
    [Parameter(Mandatory = $true)][string]$GifPath,
    [Parameter(Mandatory = $true)][string]$OutDir
)

Add-Type -AssemblyName System.Drawing

if (!(Test-Path $GifPath)) { throw "找不到源 GIF：$GifPath" }
if (!(Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# 表情键 = 输出目录名；帧文件名带上它，运行时才能把多张帧归到同一个表情上
$stickerKey = Split-Path $OutDir -Leaf

# 清掉上一次抽的帧（含旧的 frame_*.png 命名），避免换图后残留旧帧导致帧数错位
Get-ChildItem $OutDir -Filter "*.png" -ErrorAction SilentlyContinue | Remove-Item -Force

$image = [System.Drawing.Image]::FromFile((Resolve-Path $GifPath).Path)
try {
    $dimension = New-Object System.Drawing.Imaging.FrameDimension($image.FrameDimensionsList[0])
    $frameCount = $image.GetFrameCount($dimension)

    # 帧延时表：GIF 的 PropertyTagFrameDelay，单位 1/100 秒，每帧 4 字节
    $delays = New-Object 'System.Collections.Generic.List[int]'
    try {
        $property = $image.GetPropertyItem(0x5100)
        for ($i = 0; $i -lt $frameCount; $i++) { $delays.Add([BitConverter]::ToInt32($property.Value, $i * 4)) }
    }
    catch {
        Write-Warning "该 GIF 没有帧延时信息，按 10 fps 输出"
        for ($i = 0; $i -lt $frameCount; $i++) { $delays.Add(10) }
    }

    for ($i = 0; $i -lt $frameCount; $i++) {
        # SelectActiveFrame 会把该帧合成到整幅画布上（含 disposal 处理），再拷进 32bpp 位图保存
        [void]$image.SelectActiveFrame($dimension, $i)
        $bitmap = New-Object System.Drawing.Bitmap($image.Width, $image.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($image, 0, 0, $image.Width, $image.Height)
        $graphics.Dispose()

        $framePath = Join-Path $OutDir ("{0}_{1:D3}.png" -f $stickerKey, $i)
        $bitmap.Save($framePath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    }

    # 平均延时 → 播放帧率（居中在 6~30 fps 的合理区间，GIF 常见的 0 延时按 10 fps 处理）
    $valid = $delays | Where-Object { $_ -gt 0 }
    $averageDelayCs = if ($valid.Count -gt 0) { ($valid | Measure-Object -Average).Average } else { 10 }
    $fps = [Math]::Round(100.0 / $averageDelayCs, 2)
    if ($fps -gt 30) { $fps = 30 }
    if ($fps -lt 6) { $fps = 6 }

    $metaPath = Join-Path $OutDir "sticker.txt"
    "frames=$frameCount" | Set-Content -Path $metaPath -Encoding UTF8
    "fps=$fps" | Add-Content -Path $metaPath -Encoding UTF8
    "width=$($image.Width)" | Add-Content -Path $metaPath -Encoding UTF8
    "height=$($image.Height)" | Add-Content -Path $metaPath -Encoding UTF8

    Write-Host "抽帧完成：$frameCount 帧 → $OutDir（约 $fps fps，$($image.Width)x$($image.Height)）"
}
finally {
    $image.Dispose()
}
