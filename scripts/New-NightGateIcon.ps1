[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $repositoryRoot 'assets'
$svgPath = Join-Path $assetDirectory 'NightGate.Icon.svg'
$icoPath = Join-Path $assetDirectory 'NightGate.ico'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)

[System.IO.Directory]::CreateDirectory($assetDirectory) | Out-Null

$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-labelledby="title description">
  <title id="title">NightGate</title>
  <desc id="description">A crescent moon settling above a finish line</desc>
  <defs>
    <mask id="crescent-mask">
      <rect width="64" height="64" fill="black" />
      <ellipse cx="28" cy="27" rx="18" ry="20" fill="white" />
      <ellipse cx="39" cy="20.5" rx="16" ry="17.5" fill="black" />
    </mask>
  </defs>
  <rect width="64" height="64" fill="none" />
  <ellipse cx="28" cy="27" rx="18" ry="20" fill="#244148" mask="url(#crescent-mask)" />
  <path d="M12 51 H52" fill="none" stroke="#244148" stroke-width="5" stroke-linecap="round" />
  <circle cx="50.5" cy="45.5" r="3.5" fill="#E8AA49" />
</svg>
'@
[System.IO.File]::WriteAllText(
    $svgPath,
    $svg.Replace("`r`n", "`n"),
    [System.Text.UTF8Encoding]::new($false))

$frames = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new(
        $size,
        $size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = $null
    $outerPath = $null
    $cutoutPath = $null
    $moon = $null
    $moonBrush = $null
    $linePen = $null
    $dotBrush = $null
    $memory = $null

    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        $scale = $size / 64.0
        $outerPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $outerPath.AddEllipse([System.Drawing.RectangleF]::new(
            [single](10 * $scale),
            [single](7 * $scale),
            [single](36 * $scale),
            [single](40 * $scale)))
        $moon = [System.Drawing.Region]::new($outerPath)
        $cutoutPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $cutoutPath.AddEllipse([System.Drawing.RectangleF]::new(
            [single](23 * $scale),
            [single](3 * $scale),
            [single](32 * $scale),
            [single](35 * $scale)))
        $moon.Exclude($cutoutPath)

        $moonColor = [System.Drawing.Color]::FromArgb(255, 36, 65, 72)
        $moonBrush = [System.Drawing.SolidBrush]::new($moonColor)
        $graphics.FillRegion($moonBrush, $moon)

        $linePen = [System.Drawing.Pen]::new(
            $moonColor,
            [single][Math]::Max(1.25, 5 * $scale))
        $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine(
            $linePen,
            [single](12 * $scale),
            [single](51 * $scale),
            [single](52 * $scale),
            [single](51 * $scale))

        $dotBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.Color]::FromArgb(255, 232, 170, 73))
        $graphics.FillEllipse(
            $dotBrush,
            [single](47 * $scale),
            [single](42 * $scale),
            [single](7 * $scale),
            [single](7 * $scale))

        $memory = [System.IO.MemoryStream]::new()
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += ,$memory.ToArray()
    }
    finally {
        if ($null -ne $memory) { $memory.Dispose() }
        if ($null -ne $dotBrush) { $dotBrush.Dispose() }
        if ($null -ne $linePen) { $linePen.Dispose() }
        if ($null -ne $moonBrush) { $moonBrush.Dispose() }
        if ($null -ne $moon) { $moon.Dispose() }
        if ($null -ne $cutoutPath) { $cutoutPath.Dispose() }
        if ($null -ne $outerPath) { $outerPath.Dispose() }
        if ($null -ne $graphics) { $graphics.Dispose() }
        $bitmap.Dispose()
    }
}

$iconStream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    [uint32]$imageOffset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $frame = $frames[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Length)
        $writer.Write($imageOffset)
        $imageOffset += [uint32]$frame.Length
    }

    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame)
    }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($icoPath, $iconStream.ToArray())
}
finally {
    $writer.Dispose()
    $iconStream.Dispose()
}

Write-Output "Generated $svgPath"
Write-Output "Generated $icoPath"
