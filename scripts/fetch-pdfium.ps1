# Downloads the official PDFium Windows x64 build (bblanchon/pdfium-binaries)
# and unpacks it into native/pdfium/ so the project can compile against it.
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts/fetch-pdfium.ps1

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $repoRoot 'native/pdfium'
$dllPath = Join-Path $nativeDir 'bin/pdfium.dll'

if (Test-Path $dllPath) {
    Write-Host "pdfium.dll already present at $dllPath — skipping download."
    Write-Host "Delete the file first if you want to re-fetch."
    exit 0
}

$uri = 'https://github.com/bblanchon/pdfium-binaries/releases/latest/download/pdfium-win-x64.tgz'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('pdfium-win-x64-' + [System.Guid]::NewGuid().ToString('N'))
$tgz = "$tmp.tgz"

New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Write-Host "Downloading $uri ..."
Invoke-WebRequest -Uri $uri -OutFile $tgz -UseBasicParsing

Write-Host 'Extracting ...'
# Prefer the Windows inbox bsdtar (tar.exe). GNU tar from Git/MSYS misreads
# "C:\..." as a remote host, so fall back to --force-local when needed.
$systemTar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (Test-Path $systemTar) {
    & $systemTar -xzf $tgz -C $tmp
} else {
    tar --force-local -xzf $tgz -C $tmp
}
if ($LASTEXITCODE -ne 0) { Write-Error "tar extraction failed (exit $LASTEXITCODE)"; exit 1 }

New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null
Get-ChildItem -Path $tmp | Move-Item -Destination $nativeDir -Force

Remove-Item $tgz -Force
Remove-Item $tmp -Recurse -Force

if (-not (Test-Path $dllPath)) {
    Write-Error "Extraction failed: $dllPath not found."
    exit 1
}

Write-Host ''
Write-Host "PDFium ready:"
Write-Host "  $dllPath"
Get-Content (Join-Path $nativeDir 'VERSION') -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  version: $_" }
Write-Host ''
