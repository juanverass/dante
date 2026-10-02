# Spike #93: synthetic speech for the audio probes, using the Windows text-to-speech engine (System.Speech).
# The phrase is a known fact a model can only state by hearing the audio; tone.wav has no speech and cannot replace it.
#
# From WSL:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w make_speech.ps1)" -OutFile "$(wslpath -w /tmp/mm/media/speech.wav)"
param(
    [Parameter(Mandatory = $true)][string] $OutFile,
    [string] $Text = 'A palavra secreta e girassol.'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$synthesizer = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $synthesizer.SetOutputToWaveFile($OutFile)
    $synthesizer.Speak($Text)
}
finally { $synthesizer.Dispose() }
"Fala gravada em $OutFile ($((Get-Item $OutFile).Length) bytes)."
