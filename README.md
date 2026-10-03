# HttpPrintBridge

A local Windows HTTP service that prints PDFs silently: it rasterizes each page with pdfium and prints via GDI — no dialogs.

## Requirements

- Windows (uses winspool/GDI)
- `pdfium.dll` next to the executable

## Quick start

```bash
# Build (needs .NET 10 SDK + MSVC linker)
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1

# Run
dist/win-x64/HttpPrintBridge.exe --key=SECRET --urls=http://127.0.0.1:8080
```

## Usage

```bash
# Health check
curl -s http://127.0.0.1:8080/SECRET

# List printers
curl -s http://127.0.0.1:8080/SECRET/list

# Print a PDF
curl -s -X POST "http://127.0.0.1:8080/SECRET/print?copies=1&dpi=300" \
  -F "file=@test/test3.pdf;type=application/pdf"
```

The access key is the first URL path segment. Print jobs run one at a time; extra requests queue.

See [API.md](API.md) for the full API contract and configuration options.
