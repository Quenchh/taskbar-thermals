<#
  Installs or updates Taskbar Thermals.
  - Builds a single-file TaskbarThermals.exe
  - Installs it to %LOCALAPPDATA%\Programs\TaskbarThermals (closing and replacing a running copy)
  - Adds a Start menu shortcut
  - Enables start with Windows (skip with -NoStartup)
  Needs administrator rights (UAC prompt). Run it again to update.
#>
param([switch]$NoStartup)
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($NoStartup) { $argList += '-NoStartup' }
    $child = Start-Process -FilePath (Get-Process -Id $PID).Path -Verb RunAs -ArgumentList $argList -Wait -PassThru
    if ($child.ExitCode -eq 0) { Write-Host 'Installed. Taskbar Thermals is running.' } else { Write-Warning "Install failed (exit code $($child.ExitCode))." }
    exit $child.ExitCode
}

try {
    $root = $PSScriptRoot
    $dest = Join-Path $env:LOCALAPPDATA 'Programs\TaskbarThermals'
    $exe = Join-Path $dest 'TaskbarThermals.exe'

    Write-Host 'Building...'
    dotnet publish "$root\TaskbarThermals.csproj" -c Release -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root\publish" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    Write-Host 'Closing the running copy...'
    Get-Process TaskbarThermals -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    Write-Host "Installing to $dest"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item "$root\publish\TaskbarThermals.exe" $exe -Force

    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Taskbar Thermals.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $dest
    $link.Description = 'CPU/GPU temperature and usage on the taskbar'
    $link.Save()

    if (-not $NoStartup) {
        $code = (Start-Process $exe -ArgumentList '--enable-startup' -Wait -PassThru).ExitCode
        if ($code -ne 0) { throw 'Could not enable start with Windows.' }
    }

    Start-Process $exe
    exit 0
}
catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}
