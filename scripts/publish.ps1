# Publishes HttpPrintBridge as a Native AOT single-file executable.
# Requires .NET 10 SDK and the MSVC C++ toolchain (link.exe) for AOT linking.
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 [-Runtime win-x64] [-Configuration Release]

param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/HttpPrintBridge/HttpPrintBridge.csproj'
$outDir = Join-Path $repoRoot "dist/$Runtime"

$dll = Join-Path $repoRoot 'native/pdfium/bin/pdfium.dll'
if (-not (Test-Path $dll)) {
    Write-Host 'pdfium.dll missing — running fetch-pdfium.ps1 first.'
    & (Join-Path $PSScriptRoot 'fetch-pdfium.ps1')
}

Write-Host "Publishing $project -> $outDir"
if (Test-Path $outDir) {
    Write-Host "Cleaning previous output ..."
    Remove-Item $outDir -Recurse -Force
}

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishAot=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ''
Write-Host 'Publish output:'
Get-ChildItem $outDir | ForEach-Object {
    $mb = [Math]::Round($_.Length / 1MB, 2)
    Write-Host ("  {0,-40} {1,10} MB" -f $_.Name, $mb)
}
Write-Host ''
Write-Host "Run:  cd $outDir; .\HttpPrintBridge.exe --key=mysecret --urls=http://0.0.0.0:8080"
