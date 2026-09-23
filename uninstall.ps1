<#
  Removes Taskbar Thermals: closes it, removes autostart, the Start menu shortcut and the program folder.
  Settings and logs (%LOCALAPPDATA%\TaskbarThermals) are kept.
#>
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $child = Start-Process -FilePath (Get-Process -Id $PID).Path -Verb RunAs -Wait -PassThru `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($child.ExitCode -eq 0) { Write-Host "Taskbar Thermals removed. Settings and logs are still in $env:LOCALAPPDATA\TaskbarThermals." }
    exit $child.ExitCode
}

Get-Process TaskbarThermals -ErrorAction SilentlyContinue | Stop-Process -Force
schtasks.exe /Delete /TN TaskbarThermals /F 2>$null | Out-Null
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Taskbar Thermals.lnk') -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\TaskbarThermals') -Recurse -Force -ErrorAction SilentlyContinue
exit 0
