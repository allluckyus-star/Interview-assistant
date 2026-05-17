# Builds a self-contained single-file exe for Windows x64 (no .NET SDK needed on target PC).
# Target still needs: Windows 10/11 + WebView2 Runtime (usually already installed).
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\InterviewAssistant.App\InterviewAssistant.App.csproj"
$outDir = Join-Path $root "publish\$Runtime"

Write-Host "Publishing Interview Assistant ($Runtime) -> $outDir"

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

$exe = Join-Path $outDir "InterviewAssistant.App.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish failed: $exe not found"
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $exe ($sizeMb MB)"
Write-Host "Copy ONLY that .exe to another Windows PC (64-bit Win10 2004+ or Win11)."
Write-Host "No .NET install needed on the target PC (runtime is inside the exe)."
Copy-Item -Force (Join-Path $root "publish\READ-ME-on-other-PC.txt") (Join-Path $outDir "READ-ME-on-other-PC.txt") -ErrorAction SilentlyContinue
Write-Host "If it won't start, check InterviewAssistant-startup.log next to the exe"
Write-Host "  or $env:TEMP\InterviewAssistant\startup.log"
Write-Host "WebView2 (if ChatGPT pane fails): https://go.microsoft.com/fwlink/p/?LinkId=2124703"
