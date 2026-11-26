param(
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\SnapPocket\SnapPocket.csproj"
$suffix = if ($SelfContained) { "SnapPocket-win-x64-standalone" } else { "SnapPocket-win-x64" }
$outDir = Join-Path $root "outputs\$suffix"

Write-Host "Publishing SnapPocket ($suffix) to $outDir ..." -ForegroundColor Cyan

$scArg = if ($SelfContained) { "true" } else { "false" }
dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained $scArg `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Get-ChildItem -Path $outDir -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "Publish complete: $outDir\SnapPocket.exe" -ForegroundColor Green
