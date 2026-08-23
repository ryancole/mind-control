# Refresh the protocol test vectors from the misdirection repo, which owns
# them (etc/gen-vectors.py there regenerates both PROTOCOL.md's table and the
# JSON). The copy in this repo exists so `dotnet test` works standalone.
$source = Join-Path $PSScriptRoot "..\..\misdirection\etc\protocol-vectors.json"
$target = Join-Path $PSScriptRoot "..\src\MindControl.Tests\protocol-vectors.json"

Copy-Item $source $target -ErrorAction Stop
Write-Host "Synced $((Resolve-Path $target).Path)"
