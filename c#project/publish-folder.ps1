# Self-contained folder publish (most reliable on other PCs — copy the whole folder).
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\InterviewAssistant.App\InterviewAssistant.App.csproj"
$outDir = Join-Path $root "publish\$Runtime-folder"

Write-Host "Publishing folder ($Runtime) -> $outDir"

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $outDir

$exe = Join-Path $outDir "InterviewAssistant.App.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish failed: $exe not found"
}

Write-Host ""
Write-Host "Done. Copy the ENTIRE folder to the other PC:"
Write-Host "  $outDir"
Write-Host "Run InterviewAssistant.App.exe from that folder."
Write-Host "No .NET install needed. WebView2 Runtime still required:"
Write-Host "  https://go.microsoft.com/fwlink/p/?LinkId=2124703"
Copy-Item -Force (Join-Path $root "publish\READ-ME-on-other-PC.txt") (Join-Path $outDir "READ-ME-on-other-PC.txt") -ErrorAction SilentlyContinue
Write-Host "If startup fails, read InterviewAssistant-startup.log in that folder"
Write-Host "  or $env:TEMP\InterviewAssistant\startup.log"
