<#
.SYNOPSIS
  End-to-end check of a published exe on Windows: native library loading, FFmpeg, model download and every output
  format. Used by the CI and release workflows.

.PARAMETER Exe
  Path to the exe to test.

.PARAMETER Mode
  "gui" for the desktop app (VideoTranscriber.exe, run with its hidden --smoke-test switch, which opens the real
  window, transcribes the clip and closes), or "cli" for the command-line app.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Exe,
    [ValidateSet('gui', 'cli')] [string] $Mode = 'gui'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    choco install ffmpeg -y --no-progress | Out-Null
    $env:PATH = "C:\ProgramData\chocolatey\bin;$env:PATH"
}

$Exe = (Resolve-Path $Exe).Path
$work = Join-Path ([IO.Path]::GetTempPath()) "vt-smoke-$([guid]::NewGuid().ToString('N'))"
$out = Join-Path $work 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null

# A short test clip with real speech (Windows text-to-speech), or a tone if no voice is installed.
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

$clip = "$work\smoke test.mp4"
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=320x240:rate=25" -i "$work\speech.wav" `
    -shortest -c:v libx264 -pix_fmt yuv420p -c:a aac $clip
if ($LASTEXITCODE -ne 0) { throw "Couldn't create the test video" }

if ($Mode -eq 'cli') {
    & $Exe --version
    if ($LASTEXITCODE -ne 0) { throw "--version failed with exit code $LASTEXITCODE" }

    & $Exe $clip --model tiny --format all --output $out
    if ($LASTEXITCODE -ne 0) { throw "Transcription failed with exit code $LASTEXITCODE" }

    # Error handling: a missing file must fail cleanly with exit code 1.
    & $Exe "$work\does-not-exist.mp4" --model tiny
    if ($LASTEXITCODE -ne 1) { throw "Expected exit code 1 for a missing file, got $LASTEXITCODE" }
}
else {
    # A windowed app doesn't block the console, so wait for it explicitly (with a time limit).
    $process = Start-Process -FilePath $Exe -ArgumentList '--smoke-test', "`"$clip`"", "`"$out`"" -PassThru
    if (-not $process.WaitForExit(600000)) {
        $process.Kill()
        throw "The desktop app didn't finish within 10 minutes"
    }
    if ($process.ExitCode -ne 0) { throw "The desktop app's smoke test failed with exit code $($process.ExitCode)" }
}

foreach ($ext in 'txt', 'srt', 'vtt', 'json') {
    if (-not (Test-Path "$out\smoke test.$ext")) { throw "Missing output: smoke test.$ext" }
}

Write-Host "---- smoke test.srt ----"
Get-Content "$out\smoke test.srt"

Remove-Item -Recurse -Force $work
Write-Host "Smoke test passed ($Mode)."
exit 0
