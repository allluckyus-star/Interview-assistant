# Self-contained single-file exe for the tray companion (no .NET SDK on target PC).
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\InterviewAssistant.Companion\InterviewAssistant.Companion.csproj"
$outDir = Join-Path $root "publish\$Runtime"

Write-Host "Publishing Interview Assistant Companion ($Runtime) -> $outDir"

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $outDir

$exe = Join-Path $outDir "InterviewAssistant.Companion.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish failed: $exe not found"
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $exe ($sizeMb MB)"
Write-Host "Copy that .exe to another Windows PC ($Runtime, Windows 10/11)."
Write-Host "No .NET install needed - runtime is bundled inside the exe."
Write-Host "If it won't start, check InterviewAssistant-startup.log next to the exe"
Write-Host "  or $env:TEMP\InterviewAssistant\startup.log"
Copy-Item -Force (Join-Path $root "publish\READ-ME-on-other-PC.txt") (Join-Path $outDir "READ-ME-on-other-PC.txt") -ErrorAction SilentlyContinue
Write-Host "See READ-ME-on-other-PC.txt in the publish folder for troubleshooting."
