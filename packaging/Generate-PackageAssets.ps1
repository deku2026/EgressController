[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function New-Logo {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [int] $Width,
        [Parameter(Mandatory)] [int] $Height
    )

    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $bounds = [System.Drawing.Rectangle]::new(0, 0, $Width, $Height)
        $start = [System.Drawing.Color]::FromArgb(23, 32, 51)
        $end = [System.Drawing.Color]::FromArgb(55, 105, 220)
        $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $bounds,
            $start,
            $end,
            [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
        try {
            $graphics.FillRectangle($background, $bounds)
        }
        finally {
            $background.Dispose()
        }

        $fontSize = [Math]::Max(10, [Math]::Min($Height * 0.42, $Width * 0.28))
        $font = [System.Drawing.Font]::new('Segoe UI Semibold', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $format = [System.Drawing.StringFormat]::new()
        try {
            $format.Alignment = [System.Drawing.StringAlignment]::Center
            $format.LineAlignment = [System.Drawing.StringAlignment]::Center
            $graphics.DrawString('EC', $font, $brush, [System.Drawing.RectangleF]$bounds, $format)
        }
        finally {
            $format.Dispose()
            $brush.Dispose()
            $font.Dispose()
        }

        $path = Join-Path $OutputDirectory $Name
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-Logo -Name 'StoreLogo.png' -Width 50 -Height 50
New-Logo -Name 'Square44x44Logo.png' -Width 44 -Height 44
New-Logo -Name 'Square150x150Logo.png' -Width 150 -Height 150
New-Logo -Name 'Wide310x150Logo.png' -Width 310 -Height 150
