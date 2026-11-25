param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[void][System.IO.Directory]::CreateDirectory($Destination)
Add-Type -AssemblyName System.Drawing

# WixUI leaves the right edge of banners and the left column of dialogs free for branding.
function Save-SetupBitmap([string]$Name, [int]$Height, [int]$X, [int]$Y, [int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new(493, $Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::White)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($logo, $X, $Y, $Size, $Size)
        } finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $Destination $Name), [System.Drawing.Imaging.ImageFormat]::Bmp)
    } finally { $bitmap.Dispose() }
}

$logo = [System.Drawing.Image]::FromFile((Join-Path $root 'src/YubiEnroll.UI/Assets/App.png'))
try {
    Save-SetupBitmap 'Banner.bmp' 58 435 6 46
    Save-SetupBitmap 'Dialog.bmp' 312 32 28 96
} finally { $logo.Dispose() }

$license = [System.IO.File]::ReadAllText((Join-Path $root 'LICENSE'))
$escaped = $license.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}') -replace '\r?\n', '\par '
[System.IO.File]::WriteAllText((Join-Path $Destination 'License.rtf'), ('{\rtf1\ansi ' + $escaped + '}'))
