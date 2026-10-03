using System.Runtime.InteropServices;
using System.Text;
using HttpPrintBridge.Pdfium;

namespace HttpPrintBridge.Printing;

public sealed record PrinterInfo(
    string Name,
    string? Server,
    string? Port,
    string? Driver,
    bool IsDefault,
    bool IsNetwork,
    bool IsOffline,
    uint Status);

public sealed record PrintResult(string Printer, int Pages, int Copies, int Dpi);

/// <summary>
/// Windows silent printing through the GDI printer DC (CreateDC → StartDoc →
/// StartPage → StretchDIBits → EndPage → EndDoc). No print dialog is ever shown.
/// </summary>
public sealed class PrinterService
{
    private readonly SemaphoreSlim _printLock = new(1, 1);
    private readonly TimeSpan _printTimeout;

    public PrinterService(TimeSpan printTimeout)
    {
        _printTimeout = printTimeout;
    }

    // ------------------------------------------------------------------ list

    public IReadOnlyList<PrinterInfo> ListPrinters()
    {
        string? defaultName = TryGetDefaultPrinter();
        var result = new List<PrinterInfo>();

        WinspoolNative.EnumPrinters(
            WinspoolNative.PRINTER_ENUM_LOCAL | WinspoolNative.PRINTER_ENUM_CONNECTIONS,
            0, 2, 0, 0, out uint needed, out _);

        if (needed == 0)
            return result;

        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!WinspoolNative.EnumPrinters(
                    WinspoolNative.PRINTER_ENUM_LOCAL | WinspoolNative.PRINTER_ENUM_CONNECTIONS,
                    0, 2, buffer, needed, out _, out uint returned))
                throw new PrintException($"EnumPrinters failed: {Marshal.GetLastWin32Error()}");

            int structSize = Marshal.SizeOf<WinspoolNative.PRINTER_INFO_2>();
            for (int i = 0; i < returned; i++)
            {
                nint item = buffer + i * structSize;
                var info = Marshal.PtrToStructure<WinspoolNative.PRINTER_INFO_2>(item);

                string name = PtrToString(info.pPrinterName) ?? string.Empty;
                if (name.Length == 0)
                    continue;

                result.Add(new PrinterInfo(
                    Name: name,
                    Server: PtrToString(info.pServerName),
                    Port: PtrToString(info.pPortName),
                    Driver: PtrToString(info.pDriverName),
                    IsDefault: string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                    IsNetwork: (info.Attributes & WinspoolNative.PRINTER_ATTRIBUTE_NETWORK) != 0,
                    IsOffline: (info.Attributes & WinspoolNative.PRINTER_ATTRIBUTE_WORK_OFFLINE) != 0
                               || (info.Status & WinspoolNative.PRINTER_STATUS_OFFLINE) != 0,
                    Status: info.Status));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public string? TryGetDefaultPrinter()
    {
        int chars = 0;
        WinspoolNative.GetDefaultPrinter(0, ref chars);
        if (chars <= 0)
            return null;

        nint buffer = Marshal.AllocHGlobal(chars * sizeof(char));
        try
        {
            if (!WinspoolNative.GetDefaultPrinter(buffer, ref chars))
                return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ----------------------------------------------------------------- print

    /// <summary>
    /// Renders and prints every page of <paramref name="doc"/> silently.
    /// Pages are rendered one at a time so peak memory stays at a single page,
    /// except for the multi-copy fallback which may cache the whole book (capped).
    /// <paramref name="outputPath"/> redirects the spooler output to a file, which is how
    /// virtual printers such as "Microsoft Print to PDF" stay silent instead of prompting.
    /// </summary>
    public PrintResult Print(PdfDocument doc, string? printerName, int copies, int dpi,
        string? outputPath, CancellationToken ct)
    {
        if (doc.PageCount <= 0)
            throw new PrintException("PDF has no pages.");
        if (copies is < 1 or > 999)
            throw new PrintException("copies must be between 1 and 999.");
        if (outputPath is not null && !Path.IsPathRooted(outputPath))
            throw new PrintException("output must be an absolute file path.");

        string printer = ResolvePrinter(printerName);
        copies = Math.Max(1, copies);

        if (!_printLock.Wait(_printTimeout, ct))
            throw new PrintBusyException("Another print job is still running.");
        try
        {
            PrintCore(doc, printer, copies, dpi, outputPath, ct);
        }
        finally
        {
            _printLock.Release();
        }

        return new PrintResult(printer, doc.PageCount, copies, dpi);
    }

    private void PrintCore(PdfDocument doc, string printer, int copies, int dpi,
        string? outputPath, CancellationToken ct)
    {
        using var devmode = DevModeScope.Open(printer);

        // Match the paper to the first page so the raster lands 1:1 on the sheet.
        var (wPt, hPt) = doc.GetPageSize(0);
        devmode.ApplyPaperSize(wPt, hPt, copies);

        // First attempt honours the requested paper size; several v4 drivers accept the
        // DEVMODE at CreateDC time but reject the job at StartDoc. In that case we fall
        // back to the driver's own defaults and let the raster scale to the sheet.
        try
        {
            RunPrintJob(doc, printer, copies, dpi, outputPath, devmode.PreferredPointer, ct);
        }
        catch (PrintJobStartException) when (devmode.PreferredPointer != 0)
        {
            RunPrintJob(doc, printer, copies, dpi, outputPath, 0, ct);
        }
    }

    private void RunPrintJob(PdfDocument doc, string printer, int copies, int dpi,
        string? outputPath, nint devmodePtr, CancellationToken ct)
    {
        nint hdc = WinspoolNative.CreateDC("WINSPOOL", printer, 0, devmodePtr);
        if (hdc == 0)
        {
            // Retry with driver defaults — some drivers reject user paper sizes outright.
            hdc = WinspoolNative.CreateDC("WINSPOOL", printer, 0, 0);
            if (hdc == 0)
                throw new PrintException(
                    $"CreateDC failed for printer '{printer}' (win32 error {Marshal.GetLastWin32Error()}).");
        }

        bool aborted = false;
        try
        {
            nint docName = Marshal.StringToHGlobalUni("http-print-bridge");
            nint outName = outputPath is null ? 0 : Marshal.StringToHGlobalUni(outputPath);
            try
            {
                var di = new WinspoolNative.DOCINFO
                {
                    cbSize = Marshal.SizeOf<WinspoolNative.DOCINFO>(),
                    lpszDocName = docName,
                    lpszOutput = outName,
                    lpszDatatype = 0,
                    fwType = 0,
                };

                int jobId = WinspoolNative.StartDoc(hdc, in di);
                if (jobId <= 0)
                    throw new PrintJobStartException(
                        $"StartDoc failed for printer '{printer}' (win32 error {Marshal.GetLastWin32Error()}).");

                try
                {
                    // Brush origin must be pinned before the first HALFTONE blit.
                    WinspoolNative.SetBrushOrgEx(hdc, 0, 0, 0);

                    int printableWidth = Math.Max(1, WinspoolNative.GetDeviceCaps(hdc, WinspoolNative.HORZRES));
                    int printableHeight = Math.Max(1, WinspoolNative.GetDeviceCaps(hdc, WinspoolNative.VERTRES));

                    // With a DEVMODE the driver handles dmCopies; without one we print the
                    // page loop once per copy ourselves. Rendering is then shared across
                    // copies when the whole book fits the cache budget.
                    int copyCount = devmodePtr == 0 ? copies : 1;
                    List<RenderedPage>? cache = copyCount > 1
                        ? TryRenderAllPages(doc, dpi, printableWidth, printableHeight, ct)
                        : null;
                    try
                    {
                        for (int copy = 0; copy < copyCount; copy++)
                        {
                            for (int pageIndex = 0; pageIndex < doc.PageCount; pageIndex++)
                            {
                                ct.ThrowIfCancellationRequested();

                                if (cache is not null)
                                {
                                    PrintOnePage(hdc, cache[pageIndex], printableWidth, printableHeight);
                                }
                                else
                                {
                                    using RenderedPage page = doc.RenderPage(pageIndex, dpi, printableWidth, printableHeight);
                                    PrintOnePage(hdc, page, printableWidth, printableHeight);
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (cache is not null)
                        {
                            foreach (RenderedPage page in cache)
                                page.Dispose();
                        }
                    }
                }
                catch
                {
                    WinspoolNative.AbortDoc(hdc);
                    aborted = true;
                    throw;
                }

                if (WinspoolNative.EndDoc(hdc) <= 0 && !aborted)
                    throw new PrintException(
                        $"EndDoc failed for printer '{printer}' (win32 error {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                Marshal.FreeHGlobal(docName);
                if (outName != 0) Marshal.FreeHGlobal(outName);
            }
        }
        finally
        {
            WinspoolNative.DeleteDC(hdc);
        }
    }

    /// <summary>
    /// Renders every page up front when the whole book fits the cache budget, so a
    /// per-copy loop can resubmit the rasters instead of rendering the book again for
    /// every copy. Returns null when the book is too big — the caller then re-renders
    /// per copy to keep peak memory at a single page.
    /// </summary>
    private static List<RenderedPage>? TryRenderAllPages(PdfDocument doc, int dpi,
        int printableWidth, int printableHeight, CancellationToken ct)
    {
        const long maxCacheBytes = 256L * 1024 * 1024;

        // Upper bound of one clamped page: the raster never exceeds the printable area.
        long totalBytes = 4L * printableWidth * printableHeight * doc.PageCount;
        if (totalBytes > maxCacheBytes)
            return null;

        var pages = new List<RenderedPage>(doc.PageCount);
        try
        {
            for (int i = 0; i < doc.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                pages.Add(doc.RenderPage(i, dpi, printableWidth, printableHeight));
            }
            return pages;
        }
        catch
        {
            foreach (RenderedPage page in pages)
                page.Dispose();
            throw;
        }
    }

    private static void PrintOnePage(nint hdc, RenderedPage page, int printableWidth, int printableHeight)
    {
        if (WinspoolNative.StartPage(hdc) <= 0)
            throw new PrintException(
                $"StartPage failed (win32 error {Marshal.GetLastWin32Error()}).");

        try
        {
            // Scale uniformly to fit the printable area, preserving aspect ratio.
            double scale = Math.Min(
                (double)printableWidth / page.Width,
                (double)printableHeight / page.Height);

            int destW = Math.Max(1, (int)Math.Round(page.Width * scale));
            int destH = Math.Max(1, (int)Math.Round(page.Height * scale));
            int destX = (printableWidth - destW) / 2;
            int destY = (printableHeight - destH) / 2;

            // HALFTONE gives much better downscales than COLORONCOLOR but is far
            // slower. When the raster already matches the sheet — the common case,
            // because RenderPage clamps to the printable area — a straight blit is
            // identical in output and much cheaper.
            int stretchMode = scale is > 0.98 and < 1.02
                ? WinspoolNative.COLORONCOLOR
                : WinspoolNative.HALFTONE;
            WinspoolNative.SetStretchBltMode(hdc, stretchMode);

            unsafe
            {
                var header = new WinspoolNative.BITMAPINFOHEADER
                {
                    biSize = sizeof(WinspoolNative.BITMAPINFOHEADER),
                    biWidth = page.Width,
                    // Negative height = top-down DIB, matching PDFium's buffer order.
                    biHeight = -page.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = WinspoolNative.BI_RGB,
                    biSizeImage = 0,
                    biXPelsPerMeter = 0,
                    biYPelsPerMeter = 0,
                    biClrUsed = 0,
                    biClrImportant = 0,
                };

                int written = WinspoolNative.StretchDIBits(
                    hdc,
                    destX, destY, destW, destH,
                    0, 0, page.Width, page.Height,
                    page.Scan0, in header,
                    WinspoolNative.DIB_RGB_COLORS, WinspoolNative.SRCCOPY);

                if (written == 0 || written == WinspoolNative.SP_ERROR)
                    throw new PrintException(
                        $"StretchDIBits failed (win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            WinspoolNative.EndPage(hdc);
        }
    }

    private string ResolvePrinter(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            // Validate with OpenPrinter instead of enumerating every printer — a full
            // EnumPrinters walk costs hundreds of milliseconds when network printers are
            // present, and print only needs the one name. GetPrinter then reports the
            // canonical name so the response matches what /{key}/list shows.
            if (!WinspoolNative.OpenPrinter(requested, out nint hPrinter, 0))
                throw new PrinterNotFoundException(requested);
            try
            {
                return GetPrinterName(hPrinter) ?? requested;
            }
            finally
            {
                WinspoolNative.ClosePrinter(hPrinter);
            }
        }

        return TryGetDefaultPrinter()
               ?? throw new PrinterNotFoundException("(default — no default printer configured)");
    }

    private static string? GetPrinterName(nint hPrinter)
    {
        WinspoolNative.GetPrinter(hPrinter, 2, 0, 0, out uint needed);
        if (needed == 0)
            return null;

        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!WinspoolNative.GetPrinter(hPrinter, 2, buffer, needed, out _))
                return null;
            var info = Marshal.PtrToStructure<WinspoolNative.PRINTER_INFO_2>(buffer);
            return PtrToString(info.pPrinterName);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? PtrToString(nint ptr) =>
        ptr == 0 ? null : Marshal.PtrToStringUni(ptr);
}

/// <summary>
/// Owns a printer DEVMODE block allocated with the exact size (incl. driver extra
/// bytes) that DocumentProperties reports, so paper-size edits survive the round trip.
/// </summary>
internal sealed unsafe class DevModeScope : IDisposable
{
    private readonly string _printer;
    private readonly nint _hPrinter;
    private readonly nint _buffer;
    private readonly int _bufferSize;
    private bool _disposed;
    private bool _appliedChanges;

    private DevModeScope(string printer, nint hPrinter, nint buffer, int bufferSize)
    {
        _printer = printer;
        _hPrinter = hPrinter;
        _buffer = buffer;
        _bufferSize = bufferSize;
    }

    /// <summary>
    /// Pointer to pass to CreateDC. Falls back to the driver defaults (null) when the
    /// driver rejected the requested changes, so nothing is silently altered.
    /// </summary>
    public nint PreferredPointer => _appliedChanges ? _buffer : 0;

    public static DevModeScope Open(string printer)
    {
        if (!WinspoolNative.OpenPrinter(printer, out nint hPrinter, 0))
            throw new PrintException(
                $"OpenPrinter failed for '{printer}' (win32 error {Marshal.GetLastWin32Error()}).");

        try
        {
            int needed = WinspoolNative.DocumentProperties(0, hPrinter, printer, 0, 0, 0);
            if (needed <= 0)
                throw new PrintException(
                    $"DocumentProperties(size) failed for '{printer}' (win32 error {Marshal.GetLastWin32Error()}).");

            nint buffer = Marshal.AllocHGlobal(needed);
            try
            {
                int rc = WinspoolNative.DocumentProperties(
                    0, hPrinter, printer, buffer, 0, WinspoolNative.DM_OUT_BUFFER);
                if (rc < 0)
                    throw new PrintException(
                        $"DocumentProperties(read) failed for '{printer}' (win32 error {Marshal.GetLastWin32Error()}).");

                return new DevModeScope(printer, hPrinter, buffer, needed);
            }
            catch
            {
                Marshal.FreeHGlobal(buffer);
                throw;
            }
        }
        catch
        {
            WinspoolNative.ClosePrinter(hPrinter);
            throw;
        }
    }

    /// <summary>
    /// Requests a user paper size matching the PDF page (units: 1/10 mm) and the copy
    /// count. The block is only used when the driver validates the request.
    /// </summary>
    public void ApplyPaperSize(double widthPoints, double heightPoints, int copies)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        WinspoolNative.DEVMODE dm = Marshal.PtrToStructure<WinspoolNative.DEVMODE>(_buffer);

        // points -> 0.1 mm :  pt * 25.4 / 72 * 10
        short widthTenthsMm = (short)Math.Clamp((int)Math.Round(widthPoints * 25.4 / 72.0 * 10.0), 1, short.MaxValue);
        short heightTenthsMm = (short)Math.Clamp((int)Math.Round(heightPoints * 25.4 / 72.0 * 10.0), 1, short.MaxValue);

        bool landscape = widthPoints > heightPoints;
        if (landscape)
            (widthTenthsMm, heightTenthsMm) = (heightTenthsMm, widthTenthsMm);

        dm.dmOrientation = landscape ? WinspoolNative.DMORIENT_LANDSCAPE : WinspoolNative.DMORIENT_PORTRAIT;
        dm.dmPaperSize = WinspoolNative.DMPAPER_USER;
        dm.dmPaperWidth = widthTenthsMm;
        dm.dmPaperLength = heightTenthsMm;
        dm.dmCopies = (short)Math.Clamp(copies, 1, short.MaxValue);
        dm.dmFields |= WinspoolNative.DM_ORIENTATION
                       | WinspoolNative.DM_PAPERSIZE
                       | WinspoolNative.DM_PAPERWIDTH
                       | WinspoolNative.DM_PAPERLENGTH
                       | WinspoolNative.DM_COPIES;

        Marshal.StructureToPtr(dm, _buffer, false);

        // Ask the driver to validate; only keep the buffer when it accepts it.
        int rc = WinspoolNative.DocumentProperties(
            0, _hPrinter, _printer, _buffer, _buffer, WinspoolNative.DM_IN_BUFFER | WinspoolNative.DM_OUT_BUFFER);
        if (rc >= 0)
            _appliedChanges = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Marshal.FreeHGlobal(_buffer);
        if (_hPrinter != 0)
            WinspoolNative.ClosePrinter(_hPrinter);
    }
}

public class PrintException : Exception
{
    public PrintException(string message) : base(message) { }
}

public sealed class PrintBusyException : PrintException
{
    public PrintBusyException(string message) : base(message) { }
}

/// <summary>Raised when StartDoc fails; signals that a fallback with driver defaults may help.</summary>
internal sealed class PrintJobStartException : PrintException
{
    public PrintJobStartException(string message) : base(message) { }
}

public sealed class PrinterNotFoundException : PrintException
{
    public string PrinterName { get; }

    public PrinterNotFoundException(string printerName)
        : base($"Printer not found: '{printerName}'.")
    {
        PrinterName = printerName;
    }
}
