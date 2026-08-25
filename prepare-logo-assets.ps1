param(
    [Parameter(Mandatory = $true)][string]$ProgramSource,
    [Parameter(Mandatory = $true)][string]$WindowsSource,
    [Parameter(Mandatory = $true)][string]$ProgramOutput,
    [Parameter(Mandatory = $true)][string]$WindowsMasterOutput,
    [Parameter(Mandatory = $true)][string]$IconOutput,
    [string]$StartupOutput
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-ShapePath {
    param(
        [ValidateSet('Circle', 'RoundedRectangle')][string]$Shape,
        [int]$Size,
        [int]$Inset,
        [int]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $extent = $Size - ($Inset * 2) - 1
    if ($Shape -eq 'Circle') {
        $path.AddEllipse($Inset, $Inset, $extent, $extent)
        return $path
    }

    $diameter = $Radius * 2
    $right = $Inset + $extent
    $bottom = $Inset + $extent
    $path.AddArc($Inset, $Inset, $diameter, $diameter, 180, 90)
    $path.AddArc($right - $diameter, $Inset, $diameter, $diameter, 270, 90)
    $path.AddArc($right - $diameter, $bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Inset, $bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Export-MaskedLogo {
    param(
        [string]$SourcePath,
        [string]$OutputPath,
        [ValidateSet('Circle', 'RoundedRectangle')][string]$Shape,
        [System.Drawing.Rectangle]$Crop,
        [int]$Inset,
        [int]$Radius
    )

    $source = [System.Drawing.Bitmap]::FromFile($SourcePath)
    $work = [System.Drawing.Bitmap]::new(2048, 2048, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $result = [System.Drawing.Bitmap]::new(1024, 1024, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($work)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $path = New-ShapePath -Shape $Shape -Size 2048 -Inset ($Inset * 2) -Radius ($Radius * 2)
            try {
                $graphics.SetClip($path)
                $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, 2048, 2048), $Crop, [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally { $path.Dispose() }
        }
        finally { $graphics.Dispose() }

        $graphics = [System.Drawing.Graphics]::FromImage($result)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($work, 0, 0, 1024, 1024)
        }
        finally { $graphics.Dispose() }

        $directory = Split-Path -Parent $OutputPath
        if ($directory) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
        $result.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $result.Dispose()
        $work.Dispose()
        $source.Dispose()
    }
}

function New-PngIconFrame {
    param([System.Drawing.Bitmap]$Source, [int]$Size)

    $frame = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($frame)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage($Source, 0, 0, $Size, $Size)
    }
    finally { $graphics.Dispose() }

    $stream = [System.IO.MemoryStream]::new()
    try {
        $frame.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output -NoEnumerate ($stream.ToArray())
    }
    finally {
        $stream.Dispose()
        $frame.Dispose()
    }
}

function Export-MultiSizeIcon {
    param([string]$SourcePath, [string]$OutputPath)

    $sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    $source = [System.Drawing.Bitmap]::FromFile($SourcePath)
    try {
        $frames = [System.Collections.Generic.List[byte[]]]::new()
        foreach ($size in $sizes) { $frames.Add((New-PngIconFrame -Source $source -Size $size)) }
    }
    finally { $source.Dispose() }

    $directory = Split-Path -Parent $OutputPath
    if ($directory) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
    $file = [System.IO.FileStream]::new($OutputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + (16 * $sizes.Count)
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $size = $sizes[$index]
            $sizeByte = if ($size -eq 256) { [byte]0 } else { [byte]$size }
            $writer.Write($sizeByte)
            $writer.Write($sizeByte)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}

function Export-ResizedPng {
    param([string]$SourcePath, [string]$OutputPath, [int]$Size)

    $source = [System.Drawing.Bitmap]::FromFile($SourcePath)
    $frame = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($frame)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($source, 0, 0, $Size, $Size)
        }
        finally { $graphics.Dispose() }
        $directory = Split-Path -Parent $OutputPath
        if ($directory) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
        $frame.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $frame.Dispose()
        $source.Dispose()
    }
}

Export-MaskedLogo -SourcePath $ProgramSource -OutputPath $ProgramOutput -Shape Circle -Crop ([System.Drawing.Rectangle]::new(62, 54, 1140, 1140)) -Inset 7 -Radius 0
Export-MaskedLogo -SourcePath $WindowsSource -OutputPath $WindowsMasterOutput -Shape RoundedRectangle -Crop ([System.Drawing.Rectangle]::new(48, 45, 1160, 1160)) -Inset 8 -Radius 150
Export-MultiSizeIcon -SourcePath $WindowsMasterOutput -OutputPath $IconOutput
if ($StartupOutput) { Export-ResizedPng -SourcePath $ProgramOutput -OutputPath $StartupOutput -Size 256 }
