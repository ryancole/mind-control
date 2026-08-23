# Dev loop: iterate on the policy against a replayed recording.
#
# In one terminal (spectral-sight repo):
#   python tools/replay.py <clip>.jsonl [--from 120] [--speed 4]
# Then here:
#   etc/dev.ps1                 # dry run, intents logged
#   etc/dev.ps1 -Port COM5      # drive the real board
param(
    [string]$Port,
    [string]$Feed = "http://127.0.0.1:8723"
)

$args = @("--feed", $Feed)
if ($Port) { $args += @("--port", $Port) }

dotnet watch --project "$PSScriptRoot\..\src\MindControl" -- run -- @args
