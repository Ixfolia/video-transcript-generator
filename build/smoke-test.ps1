<#
.SYNOPSIS
  End-to-end check of a published VideoTranscriber.exe: native library loading, FFmpeg, model download and
  every output format. Used by the CI and release workflows on Windows runners.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Exe
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    choco install ffmpeg -y --no-progress | Out-Null
    $env:PATH = "C:\ProgramData\chocolatey\bin;$env:PATH"
}

$Exe = (Resolve-Path $Exe).Path
& $Exe --version
if ($LASTEXITCODE -ne 0) { throw "--version failed with exit code $LASTEXITCODE" }

$work = Join-Path ([IO.Path]::GetTempPath()) "vt-smoke-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    Add-Type -AssemblyName System.Speech
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $synth.SetOutputToWaveFile("$work\speech.wav")
    $synth.Speak("Hello from the build pipeline. This is a short test of the video transcriber.")
    $synth.Dispose()
}
catch {
    Write-Warning "Speech synthesis unavailable ($_); using a test tone instead."
    ffmpeg -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:duration=5" "$work\speech.wav"
}

ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=320x240:rate=25" -i "$work\speech.wav" `
    -shortest -c:v libx264 -pix_fmt yuv420p -c:a aac "$work\smoke test.mp4"
if ($LASTEXITCODE -ne 0) { throw "Couldn't create the test video" }

& $Exe "$work\smoke test.mp4" --model tiny --format all
if ($LASTEXITCODE -ne 0) { throw "Transcription failed with exit code $LASTEXITCODE" }

foreach ($ext in 'txt', 'srt', 'vtt', 'json') {
    if (-not (Test-Path "$work\smoke test.$ext")) { throw "Missing output: smoke test.$ext" }
}

Write-Host "---- smoke test.srt ----"
Get-Content "$work\smoke test.srt"

# Error handling: a missing file must fail cleanly with exit code 1.
& $Exe "$work\does-not-exist.mp4" --model tiny
if ($LASTEXITCODE -ne 1) { throw "Expected exit code 1 for a missing file, got $LASTEXITCODE" }

Remove-Item -Recurse -Force $work
Write-Host "Smoke test passed."
exit 0
