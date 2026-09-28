# Dev loop: iterate on the policy against a replayed recording.
#
# In one terminal (spectral-sight repo):
#   python tools/replay.py <clip>.jsonl [--from 120] [--speed 4]
#   (execution coaching: ../mind-control/data/coach-full-20260902-222718.jsonl)
# Then here:
#   etc/dev.ps1                         # coaching feedback to the console
#   etc/dev.ps1 -Log data/coaching.log  # also append it to a file
# PositionalBinding off, or app options land in -Log/-Feed positionally
# (`-- --trace out.jsonl` once bound "--trace" to -Log and the path to -Feed).
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Log,
    [string]$Feed = "http://127.0.0.1:8723",
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$appArgs = @("--feed", $Feed)
if ($Log) { $appArgs += @("--log", $Log) }
if ($Rest) { $appArgs += $Rest }   # e.g. etc/dev.ps1 --trace

dotnet watch run --project "$PSScriptRoot\..\src\MindControl" -- @appArgs
