# Publishes HttpPrintBridge as Native AOT single-file executables in two flavours:
#
#   HttpPrintBridge.exe        Release build (optimised, no debug info)
#   HttpPrintBridge-debug.exe  Debug build   (unoptimised, full debug info)
#
# Both land in dist/<runtime>/ and share pdfium.dll + appsettings.json.
# Requires .NET 10 SDK and the MSVC C++ toolchain (link.exe) for AOT linking.
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 [-Runtime win-x64]

param(
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/HttpPrintBridge/HttpPrintBridge.csproj'
$outDir = Join-Path $repoRoot "dist/$Runtime"
$stageDir = Join-Path $repoRoot "dist/.stage-$Runtime"

$dll = Join-Path $repoRoot 'native/pdfium/bin/pdfium.dll'
if (-not (Test-Path $dll)) {
    Write-Host 'pdfium.dll missing — running fetch-pdfium.ps1 first.'
    & (Join-Path $PSScriptRoot 'fetch-pdfium.ps1')
}

Write-Host "Publishing $project -> $outDir"
foreach ($dir in @($outDir, $stageDir)) {
    if (Test-Path $dir) {
        Write-Host "Cleaning $dir ..."
        Remove-Item $dir -Recurse -Force
    }
}

function Publish-Flavour {
    param(
        [string]$Configuration,   # Debug | Release
        [string]$AssemblyName,    # output exe base name
        [string]$Target           # staging directory
    )

    Write-Host ''
    Write-Host "=== $Configuration build -> $AssemblyName.exe ==="

    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishAot=true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:AssemblyName=$AssemblyName `
        -o $Target

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish ($Configuration) failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

# Release: default assembly name -> HttpPrintBridge.exe
Publish-Flavour -Configuration 'Release' -AssemblyName 'HttpPrintBridge' -Target (Join-Path $stageDir 'release')

# Debug: suffixed assembly name -> HttpPrintBridge-debug.exe (and matching .pdb)
Publish-Flavour -Configuration 'Debug'   -AssemblyName 'HttpPrintBridge-debug' -Target (Join-Path $stageDir 'debug')

# Assemble the final folder: both binaries + one shared copy of the runtime payload.
Write-Host ''
Write-Host "Assembling $outDir ..."
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Copy-Item (Join-Path $stageDir 'release/HttpPrintBridge.exe')            $outDir -Force
Copy-Item (Join-Path $stageDir 'release/HttpPrintBridge.pdb')            $outDir -Force
Copy-Item (Join-Path $stageDir 'debug/HttpPrintBridge-debug.exe')        $outDir -Force
Copy-Item (Join-Path $stageDir 'debug/HttpPrintBridge-debug.pdb')        $outDir -Force
Copy-Item (Join-Path $stageDir 'release/pdfium.dll')                     $outDir -Force
Copy-Item (Join-Path $stageDir 'release/appsettings.json')               $outDir -Force

Remove-Item $stageDir -Recurse -Force

Write-Host ''
Write-Host 'Publish output:'
Get-ChildItem $outDir | ForEach-Object {
    $mb = [Math]::Round($_.Length / 1MB, 2)
    Write-Host ("  {0,-40} {1,10} MB" -f $_.Name, $mb)
}
Write-Host ''
Write-Host 'Run (release):  .\HttpPrintBridge.exe --key=mysecret --urls=http://0.0.0.0:8080'
Write-Host 'Run (debug):    .\HttpPrintBridge-debug.exe --key=mysecret --urls=http://0.0.0.0:8080'
