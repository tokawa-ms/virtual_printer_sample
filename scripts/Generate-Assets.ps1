<#
.SYNOPSIS
  Generate minimal placeholder PNG assets required by the MSIX manifest.
.DESCRIPTION
  Creates Assets\Square44x44Logo.png, Square150x150Logo.png,
  Wide310x150Logo.png, StoreLogo.png as solid-color PNGs.
  Replace with real artwork for production use.
#>

param(
    [string]$AssetsDir = ''
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not $AssetsDir) {
    $AssetsDir = Join-Path $scriptDir '..\src\VirtualPrinter.App\Assets'
}
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $AssetsDir)) {
    New-Item -ItemType Directory -Force -Path $AssetsDir | Out-Null
}

function New-Logo {
    param([string]$Path, [int]$W, [int]$H, [string]$Text)
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode  = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 110, 200))
    $g.FillRectangle($bg, 0, 0, $W, $H)
    if ($Text) {
        $fontSize = [Math]::Max(8, [int]([Math]::Min($W, $H) / 4))
        $font     = New-Object System.Drawing.Font 'Segoe UI', $fontSize, ([System.Drawing.FontStyle]::Bold)
        $sf       = New-Object System.Drawing.StringFormat
        $sf.Alignment     = 'Center'
        $sf.LineAlignment = 'Center'
        $g.DrawString($Text, $font, [System.Drawing.Brushes]::White,
                      (New-Object System.Drawing.RectangleF 0, 0, $W, $H), $sf)
        $font.Dispose()
    }
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Host "wrote $Path"
}

New-Logo (Join-Path $AssetsDir 'Square44x44Logo.png')   44  44  'V'
New-Logo (Join-Path $AssetsDir 'Square150x150Logo.png') 150 150 'VP'
New-Logo (Join-Path $AssetsDir 'Wide310x150Logo.png')   310 150 'Virtual Print'
New-Logo (Join-Path $AssetsDir 'StoreLogo.png')         50  50  'V'
