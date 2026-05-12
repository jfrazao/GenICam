# Build extension and open test.bonsai in Bonsai IDE.
# --lib points at the build output so the assembly loads without package installation.
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

$bonsaiExe   = "C:\Users\jfraz\AppData\Local\Bonsai\Bonsai.exe"
$libDir      = "$PSScriptRoot\src\Bonsai.GenICam\bin\Release\net472"
$workflowFile = "$PSScriptRoot\test.bonsai"

# Build first to ensure we have the latest DLL.
Write-Host "Building Bonsai.GenICam..."
dotnet build "$PSScriptRoot\src\Bonsai.GenICam\Bonsai.GenICam.csproj" -c Release -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$args = @($workflowFile, "--lib", $libDir)
if ($Start)    { $args += "--start" }
if ($NoEditor) { $args += "--no-editor" }

Write-Host "Starting Bonsai: $bonsaiExe $args"
& $bonsaiExe @args
