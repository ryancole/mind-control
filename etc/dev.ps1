# Dev loop: iterate on the policy against a replayed recording.
#
# In one terminal (spectral-sight repo):
#   python tools/replay.py <clip>.jsonl [--from 120] [--speed 4]
# Then here:
#   etc/dev.ps1                         # coaching feedback to the console
#   etc/dev.ps1 -Log data/coaching.log  # also append it to a file
param(
    [string]$Log,
    [string]$Feed = "http://127.0.0.1:8723"
)

$args = @("--feed", $Feed)
if ($Log) { $args += @("--log", $Log) }

dotnet watch --project "$PSScriptRoot\..\src\MindControl" -- run -- @args
