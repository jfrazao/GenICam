# Build extension and open test.bonsai in Bonsai IDE.
# --lib points at the build output so the assembly loads without package installation.
#
# First-time setup: run .bonsai\Setup.cmd (or Setup.ps1) to install Bonsai locally,
# or let this script do it automatically when Bonsai.exe is not yet present.
#
# Usage:
#   .\run-bonsai.ps1              # open in editor (click Run to start)
#   .\run-bonsai.ps1 --start      # open in editor and start automatically
#   .\run-bonsai.ps1 --no-editor  # headless (no editor UI, starts immediately)

param(
    [switch]$Start,
    [switch]$NoEditor
)

$ErrorActionPreference = "Stop"

$bonsaiExe    = "$PSScriptRoot\.bonsai\Bonsai.exe"
$libDir       = "$PSScriptRoot\artifacts\bin\Bonsai.GenICam\release_win-x64"
$workflowFile = "$PSScriptRoot\workflows\test.bonsai"

# Bootstrap local Bonsai if not yet installed.
if (!(Test-Path $bonsaiExe)) {
    Write-Host "Local Bonsai not found - running setup..."
    & "$PSScriptRoot\.bonsai\Setup.ps1"
}

# Build first to ensure we have the latest DLL.
Write-Host "Building Bonsai.GenICam..."
dotnet build "$PSScriptRoot\src\Bonsai.GenICam\Bonsai.GenICam.csproj" -c Release -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$bonsaiArgs = @($workflowFile, "--lib", $libDir)
if ($Start)    { $bonsaiArgs += "--start" }
if ($NoEditor) { $bonsaiArgs += "--no-editor" }

Write-Host "Starting Bonsai: $bonsaiExe $bonsaiArgs"
& $bonsaiExe @bonsaiArgs
