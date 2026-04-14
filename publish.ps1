# publish.ps1 is RETIRED. The supported release entrypoint is publish.cmd (runs dotnet from cmd.exe).
Write-Host ""
Write-Host "publish.ps1 is retired. Use publish.cmd from a Command Prompt or:"
Write-Host "  cmd.exe /d /c .\publish.cmd"
Write-Host "  cmd.exe /d /c .\publish.cmd -SkipPublish"
Write-Host "  cmd.exe /d /c .\publish.cmd -StableGate"
Write-Host "See README.md (release / Stable gate) for options."
Write-Host ""
exit 1
