# mind-control

## Repo boundary

NEVER edit another repo. Only edit this repo (`mind-control`).

This includes sibling repos in the same pipeline (kilrogg, spectral-sight,
misdirection), any git submodule checked out under this repo (for example
the jev-dotnet client), and anything else outside this working directory.
If a change seems to belong in another repo, describe it and stop; do not
make it.

## Shell

This machine runs Windows, and the shell is PowerShell, not bash or Linux.
Use PowerShell commands and syntax (`Get-ChildItem`, `$env:NAME`,
`Select-String`, here-strings), Windows paths, and the `.ps1` scripts in
`etc/`. Do not reach for bash, heredocs, `sed`, `grep`, or other Unix tools.
