<#
.SYNOPSIS
    Generates numbered test clips and a .mvp playlist for exercising the player.

.DESCRIPTION
    Each clip burns in its own index and a running timecode, which is what makes a swap
    verifiable by eye: when a video moves between the primary and a numbered slot, its
    timecode must carry straight on rather than restart.

    Requires ffmpeg on PATH (or pass -FFmpeg).
#>
param(
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) "samples"),
    [string]$FFmpeg = "ffmpeg",
    [int]$Count = 12,
    [int]$Seconds = 120,
    [string]$Size = "1280x720",
    [int]$Fps = 24,
    [int]$Crf = 30
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command $FFmpeg -ErrorAction SilentlyContinue)) {
    throw "ffmpeg not found. Install it, or pass -FFmpeg <path to ffmpeg.exe>."
}

New-Item -ItemType Directory -Force $Out | Out-Null
$font = "C\:/Windows/Fonts/arialbd.ttf"
$paths = @()

for ($i = 1; $i -le $Count; $i++) {
    $tag = "{0:D2}" -f $i
    $file = Join-Path $Out "clip_$tag.mp4"
    $hue = ($i * 29) % 360
    $freq = 220 + $i * 40

    $filter = "[0:v]hue=h=$hue," +
              "drawtext=fontfile='$font':text='$tag':fontsize=300:fontcolor=white@0.9:borderw=6:bordercolor=black@0.7:x=(w-tw)/2:y=(h-th)/2-70," +
              "drawtext=fontfile='$font':text='%{pts\:hms}':fontsize=96:fontcolor=yellow:borderw=5:bordercolor=black@0.8:x=(w-tw)/2:y=h-190[v]"

    & $FFmpeg -y -hide_banner -loglevel error `
        -f lavfi -i "testsrc=size=${Size}:rate=${Fps}:duration=$Seconds" `
        -f lavfi -i "sine=frequency=${freq}:duration=$Seconds" `
        -filter_complex $filter `
        -map "[v]" -map 1:a `
        -c:v libx264 -preset medium -crf $Crf -pix_fmt yuv420p -g ($Fps * 2) `
        -c:a aac -b:a 64k -shortest $file
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $file" }

    $paths += $file
    Write-Host ("  {0}  {1:N1} MB" -f (Split-Path -Leaf $file), ((Get-Item $file).Length / 1MB))
}

# Match what the app writes: #MVP, CRLF throughout including a trailing one,
# UTF-8 with no BOM.
$playlist = Join-Path $Out "samples.mvp"
$text = (@("#MVP") + $paths -join "`r`n") + "`r`n"
[System.IO.File]::WriteAllText($playlist, $text, (New-Object System.Text.UTF8Encoding($false)))

$total = (Get-ChildItem $Out -Filter *.mp4 | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ""
Write-Host ("{0} clips, {1:N0} MB total" -f $Count, $total)
Write-Host "playlist: $playlist"
