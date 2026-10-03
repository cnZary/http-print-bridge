using System.Runtime.InteropServices;

namespace HttpPrintBridge.Pdfium;

/// <summary>
/// A single page rendered to a top-down 32-bit BGRA buffer, ready for StretchDIBits.
/// The buffer is PDFium's own bitmap memory; this wrapper owns the bitmap and frees
/// it on dispose (no managed copy of the pixels is made).
/// </summary>
public sealed class RenderedPage : IDisposable
{
    private nint _bmp;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Points (1/72 inch) — the PDF user-space page size.</summary>
    public double WidthPoints { get; }
    public double HeightPoints { get; }

    public int Stride { get; }
    public nint Scan0 { get; }
    public int PageNumber { get; }

    internal RenderedPage(int pageNumber, double widthPoints, double heightPoints,
        int width, int height, int stride, nint bmp, nint scan0)
    {
        PageNumber = pageNumber;
        WidthPoints = widthPoints;
        HeightPoints = heightPoints;
        Width = width;
        Height = height;
        Stride = stride;
        _bmp = bmp;
        Scan0 = scan0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_bmp != 0)
        {
            PdfiumNative.FPDFBitmap_Destroy(_bmp);
            _bmp = 0;
        }
    }
}

/// <summary>
/// Thin wrapper over a PDFium document handle. Pages are rendered one at a time so
/// memory stays at a single-page peak even for very large PDFs.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private readonly nint _doc;
    private readonly GCHandle _pin;
    private bool _disposed;

    public int PageCount { get; }

    private PdfDocument(nint doc, GCHandle pin)
    {
        _doc = doc;
        _pin = pin;
        PageCount = PdfiumNative.FPDF_GetPageCount(doc);
    }

    /// <summary>
    /// Loads a PDF from a memory buffer. The buffer is pinned for the lifetime of the
    /// document, as PDFium may lazily read from it while pages are open.
    /// </summary>
    public static PdfDocument Load(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (pdfBytes.Length == 0)
            throw new PdfiumException("PDF data is empty.", PdfiumNative.FpdfErrFormat);

        GCHandle pin = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
        nint doc = PdfiumNative.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), pdfBytes.Length, 0);
        if (doc == 0)
        {
            uint err = PdfiumNative.FPDF_GetLastError();
            pin.Free();
            throw new PdfiumException(Describe(err), err);
        }

        return new PdfDocument(doc, pin);
    }

    public (double WidthPoints, double HeightPoints) GetPageSize(int pageIndex)
    {
        nint page = OpenPage(pageIndex);
        try
        {
            return (PdfiumNative.FPDF_GetPageWidth(page), PdfiumNative.FPDF_GetPageHeight(page));
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// Renders one page to a BGRA buffer at the requested DPI. The returned
    /// <see cref="RenderedPage"/> owns PDFium's bitmap; dispose it after printing.
    /// When a non-zero <paramref name="maxWidthPx"/>×<paramref name="maxHeightPx"/>
    /// box is given, the raster is clamped to fit inside it (aspect preserved) so
    /// pixels that GDI would only scale back down are never rendered.
    /// </summary>
    public RenderedPage RenderPage(int pageIndex, int dpi, int maxWidthPx = 0, int maxHeightPx = 0)
    {
        if (dpi is < 36 or > 1200)
            throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be between 36 and 1200.");

        nint page = OpenPage(pageIndex);
        try
        {
            double wPt = PdfiumNative.FPDF_GetPageWidth(page);
            double hPt = PdfiumNative.FPDF_GetPageHeight(page);

            int wPx = Math.Max(1, (int)Math.Round(wPt / 72.0 * dpi));
            int hPx = Math.Max(1, (int)Math.Round(hPt / 72.0 * dpi));

            // Clamp to the size that will actually be printed. Rendering more pixels
            // than StretchDIBits would scale away again is pure cost; a dpi below the
            // device size is still honoured (that is the caller's speed/quality knob).
            if (maxWidthPx > 0 && maxHeightPx > 0)
            {
                double fit = Math.Min((double)maxWidthPx / wPx, (double)maxHeightPx / hPx);
                if (fit < 1.0)
                {
                    wPx = Math.Max(1, (int)Math.Round(wPx * fit));
                    hPx = Math.Max(1, (int)Math.Round(hPx * fit));
                }
            }

            // Guard against absurd page sizes blowing past the 32-bit stride limit.
            long strideLong = (long)wPx * 4;
            long totalLong = strideLong * hPx;
            if (totalLong > int.MaxValue)
                throw new PdfiumException($"Page {pageIndex + 1} at {dpi} DPI requires too much memory.", PdfiumNative.FpdfErrUnknown);

            nint bmp = PdfiumNative.FPDFBitmap_Create(wPx, hPx, 0);
            if (bmp == 0)
                throw new PdfiumException("Failed to allocate render bitmap.", PdfiumNative.FpdfErrUnknown);

            bool handedOff = false;
            try
            {
                // Opaque white background so transparent PDF regions don't print black.
                PdfiumNative.FPDFBitmap_FillRect(bmp, 0, 0, wPx, hPx, 0xFFFFFFFF);

                PdfiumNative.FPDF_RenderPageBitmap(
                    bmp, page, 0, 0, wPx, hPx, 0,
                    PdfiumNative.FpdfAnnot | PdfiumNative.FpdfPrinting);

                nint buffer = PdfiumNative.FPDFBitmap_GetBuffer(bmp);
                int stride = PdfiumNative.FPDFBitmap_GetStride(bmp);
                if (buffer == 0 || stride <= 0)
                    throw new PdfiumException("Render bitmap has no accessible buffer.", PdfiumNative.FpdfErrUnknown);

                // Zero-copy: print directly from PDFium's buffer. RenderedPage takes
                // ownership of the bitmap and destroys it on Dispose.
                var rendered = new RenderedPage(pageIndex + 1, wPt, hPt, wPx, hPx, stride, bmp, buffer);
                handedOff = true;
                return rendered;
            }
            finally
            {
                if (!handedOff)
                    PdfiumNative.FPDFBitmap_Destroy(bmp);
            }
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private nint OpenPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        nint page = PdfiumNative.FPDF_LoadPage(_doc, pageIndex);
        if (page == 0)
            throw new PdfiumException($"Failed to load page {pageIndex + 1}.", PdfiumNative.FPDF_GetLastError());
        return page;
    }

    private static string Describe(uint err) => err switch
    {
        PdfiumNative.FpdfErrFile => "Cannot open PDF file data.",
        PdfiumNative.FpdfErrFormat => "PDF data is corrupted or not a valid PDF.",
        PdfiumNative.FpdfErrPassword => "PDF is password protected.",
        PdfiumNative.FpdfErrSecurity => "PDF uses an unsupported security scheme.",
        PdfiumNative.FpdfErrPage => "PDF contains an invalid page.",
        _ => "Unknown PDFium error while loading the document.",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PdfiumNative.FPDF_CloseDocument(_doc);
        if (_pin.IsAllocated) _pin.Free();
    }
}

public sealed class PdfiumException : Exception
{
    public uint ErrorCode { get; }

    public PdfiumException(string message, uint errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>Owns the global PDFium library lifetime for the process.</summary>
public sealed class PdfiumLibrary : IDisposable
{
    private static int _initialized;

    public PdfiumLibrary()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            PdfiumNative.FPDF_InitLibrary();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 1)
            PdfiumNative.FPDF_DestroyLibrary();
    }
}
