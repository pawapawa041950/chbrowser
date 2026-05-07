# ChBrowser のアプリアイコン (.ico) を生成する PowerShell スクリプト。
#
# デザイン: 角丸正方形 + 4 色クアドラント + 中央の白丸に "CHB" ロゴ。
#   - TL (board)    : #3B82F6 ブルー
#   - TR (thread)   : #F59E0B アンバー
#   - BL (favorites): #10B981 エメラルド
#   - BR (display)  : #EC4899 ピンク
# 中央は白丸 (badge) + 黒文字 "CHB" で 16-256 px の全サイズで判別可能なシンボル化。
#
# 使い方:
#   pwsh tools/make-icon.ps1
# 出力: src/ChBrowser/Resources/icon/app.ico (16/32/48/64/128/256 を内包)

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$outDir  = Join-Path $root 'src/ChBrowser/Resources/icon'
$outIco  = Join-Path $outDir 'app.ico'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# 各サイズの PNG バイト列を保持する配列。後で .ico コンテナに詰める。
$sizes = @(16, 24, 32, 48, 64, 128, 256)
Write-Host ("DIAG immediately after assign: sizes type={0}, count={1}, values=[{2}]" -f $sizes.GetType().FullName, $sizes.Count, ($sizes -join ','))

function New-IconBitmap([int]$size) {
    # PowerShell 5.1 の New-Object は複数 arg を空白区切りで取るため、コンストラクタを多用する箇所は
    # [Type]::new(...) の方が引数解釈が安定。混在を避けて統一する。
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # ---- 角丸正方形マスク ----
    $cornerRadius = [Math]::Max(2, [int]($size * 0.18))
    $rect = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $cornerRadius * 2
    $path.AddArc($rect.X,           $rect.Y,            $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d,  $rect.Y,            $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d,  $rect.Bottom - $d,  $d, $d,   0, 90)
    $path.AddArc($rect.X,           $rect.Bottom - $d,  $d, $d,  90, 90)
    $path.CloseFigure()
    $g.SetClip($path)

    # ---- 背景: 5 トラックを縦縞で塗る (= 各トラック自体が色を持つ) ----
    # スレ表示 (thread.css) の各トラックマーカー色を縞背景として borrow:
    #   popular #d32f2f / url #2e7d32 / image #f57c00 / video #7b1fa2 / mark #1e88e5
    $trackColors = @(
        [System.Drawing.Color]::FromArgb(211,  47,  47),   # #D32F2F popular
        [System.Drawing.Color]::FromArgb( 46, 125,  50),   # #2E7D32 url
        [System.Drawing.Color]::FromArgb(245, 124,   0),   # #F57C00 image
        [System.Drawing.Color]::FromArgb(123,  31, 162),   # #7B1FA2 video
        [System.Drawing.Color]::FromArgb( 30, 136, 229)    # #1E88E5 mark
    )
    for ($i = 0; $i -lt 5; $i++) {
        $x     = [int]([Math]::Round($i * $size / 5.0))
        $xNext = if ($i -eq 4) { $size } else { [int]([Math]::Round(($i + 1) * $size / 5.0)) }
        $w     = $xNext - $x
        $brush = [System.Drawing.SolidBrush]::new($trackColors[$i])
        $g.FillRectangle($brush, $x, 0, $w, $size)
        $brush.Dispose()
    }

    # ---- マーカー: 各トラック 2-3 本の白い太い横帯 ----
    # 縦軸線は描かない (= 色付き縞 + 白マーカーの 2 要素で完結させる)。
    # サイズが 24 未満では細部が潰れるので省略。
    if ($size -ge 24) {
        $markerHeight = [Math]::Max(2, [int]([Math]::Round($size * 0.088)))
        $markerYs = @(
            @(0.18, 0.62),         # popular  : 2 本
            @(0.34, 0.78),         # url      : 2 本
            @(0.12, 0.48, 0.83),   # image    : 3 本
            @(0.26, 0.70),         # video    : 2 本
            @(0.20, 0.54, 0.87)    # mark     : 3 本
        )
        $markerBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        for ($i = 0; $i -lt 5; $i++) {
            $x     = [int]([Math]::Round($i * $size / 5.0))
            $xNext = if ($i -eq 4) { $size } else { [int]([Math]::Round(($i + 1) * $size / 5.0)) }
            $w     = $xNext - $x
            foreach ($yRatio in $markerYs[$i]) {
                $yCenter = [int]($size * $yRatio)
                $y       = [Math]::Max(0, $yCenter - [int]($markerHeight / 2))
                $g.FillRectangle($markerBrush, $x, $y, $w, $markerHeight)
            }
        }
        $markerBrush.Dispose()
    }

    # ---- 中央の大きな白文字 "C" ----
    # フォントサイズはアイコンサイズの 0.85 倍 (= 字面が viewport ほぼ一杯)。
    # Arial Black が無い環境を考慮して Arial Bold をフォールバックに使う。
    # 16/24 px だと Arial Bold だと潰れがちなので 0.78 倍に少し小さめへ。
    $fontRatio = if ($size -lt 32) { 0.82 } else { 0.92 }
    $fontSize  = [float]([Math]::Max(8.0, $size * $fontRatio))
    $font = $null
    foreach ($family in @('Arial Black', 'Arial')) {
        try {
            $font = [System.Drawing.Font]::new($family, $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            break
        } catch {}
    }
    # 背景が白になったため C を白で描くと見えない。
    # 太いマーカーの上を C が横切ると視覚的に「白抜き」感が出る配置にしたいので、
    # C 自体は白のまま描き、上から濃い色のアウトラインで縁取って白文字感を維持する。
    # アウトラインは GraphicsPath + Pen で C の輪郭線をなぞる方式。
    $outlineColor = [System.Drawing.Color]::FromArgb(31, 41, 55)  # #1F2937 (= 暗いスレート)
    $textBrush    = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $sf = [System.Drawing.StringFormat]::new()
    $sf.Alignment     = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = [System.Drawing.RectangleF]::new(0.0, 0.0, [float]$size, [float]$size)

    # GraphicsPath で文字シルエットを取り出し、太いアウトライン → 白塗りの順で描く。
    # こうすると C の周囲に「縁」が出て、白色のまま白い背景の上でも視認できる。
    # 32 px 以上のときだけアウトラインを描き、それ以下では DrawString 直書きで潰れを避ける。
    if ($size -ge 32) {
        $tp = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $tp.AddString('C', $font.FontFamily, [int][System.Drawing.FontStyle]::Bold, $fontSize, $textRect, $sf)
        $strokeWidth = [Math]::Max(2.0, [float]($size * 0.05))
        $strokePen = [System.Drawing.Pen]::new($outlineColor, $strokeWidth)
        $strokePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($strokePen, $tp)
        $g.FillPath($textBrush, $tp)
        $strokePen.Dispose()
        $tp.Dispose()
    } else {
        # 16/24 px: アウトライン無しで白文字を直描き (= マーカーが背景にも無いので
        # 白文字が消えるが、サイズ的に "C" を読ませること自体が困難なので識別性は他要素に任せる)。
        $g.DrawString('C', $font, $textBrush, $textRect, $sf)
    }
    $font.Dispose()
    $textBrush.Dispose()
    $sf.Dispose()

    $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

# ---- 各サイズの PNG を生成 ----
Write-Host ("DIAG: about to render. sizes.Count = {0}" -f $sizes.Count)
$pngs = @{}
foreach ($s in $sizes) {
    Write-Host ("DIAG: rendering size {0}" -f $s)
    try {
        $bmp = New-IconBitmap -size $s
        $pngs[$s] = Get-PngBytes $bmp
        $previewPath = Join-Path $outDir ("preview_{0}.png" -f $s)
        [System.IO.File]::WriteAllBytes($previewPath, $pngs[$s])
        $bmp.Dispose()
        Write-Host ("rendered {0}x{0} ({1} bytes)" -f $s, $pngs[$s].Length)
    } catch {
        Write-Host ("ERR at size {0}: {1}" -f $s, $_.Exception.Message)
        Write-Host ("  at: {0}" -f $_.ScriptStackTrace)
        throw
    }
}
Write-Host ("DIAG: rendered {0} sizes, pngs hash count = {1}" -f $sizes.Count, $pngs.Count)

# ---- ICO コンテナ組み立て ----
# レイアウト:
#   [ICONDIR (6 bytes)]
#   [ICONDIRENTRY (16 bytes) × N]
#   [PNG payload × N]
$count = $sizes.Count
$ms = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($ms)

# ICONDIR header
$bw.Write([UInt16]0)        # reserved
$bw.Write([UInt16]1)        # type = 1 (icon)
$bw.Write([UInt16]$count)   # image count

# 各エントリの payload オフセット計算 (= header + entries の合計サイズの後ろから)
$payloadOffset = 6 + 16 * $count
$entries = @()
foreach ($s in $sizes) {
    $bytes = $pngs[$s]
    $entries += [pscustomobject]@{ Size = $s; Bytes = $bytes; Offset = $payloadOffset }
    $payloadOffset += $bytes.Length
}

# ICONDIRENTRY × N
foreach ($e in $entries) {
    # width / height: 256 のときは 0 を入れる慣例 (= 0 = 256 の意味)
    $w = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $h = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$w)              # width
    $bw.Write([byte]$h)              # height
    $bw.Write([byte]0)               # color count (PNG なので 0)
    $bw.Write([byte]0)               # reserved
    $bw.Write([UInt16]1)             # planes
    $bw.Write([UInt16]32)            # bit count
    $bw.Write([UInt32]$e.Bytes.Length)  # bytes in resource
    $bw.Write([UInt32]$e.Offset)        # offset
}

# PNG payloads — pscustomobject の .Bytes は object として返ってくることがあるため、
# BinaryWriter のオーバーロード解決で誤爆 (Write(byte) が選ばれて 1 byte しか書かれない) を避けるため
# 明示的に [byte[]] にキャストし、Stream.Write(byte[], int, int) で書く方が確実。
foreach ($e in $entries) {
    [byte[]]$bytes = $e.Bytes
    $ms.Write($bytes, 0, $bytes.Length)
}

$bw.Flush()
$icoBytes = $ms.ToArray()
[System.IO.File]::WriteAllBytes($outIco, $icoBytes)
$bw.Close()
$ms.Close()

Write-Host ""
Write-Host ("✓ {0}  ({1:N0} bytes, {2} sizes embedded)" -f $outIco, $icoBytes.Length, $count)
Write-Host ("  preview PNGs in {0}\preview_*.png" -f $outDir)
