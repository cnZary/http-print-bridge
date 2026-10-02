using System.Runtime.InteropServices;

namespace HttpPrintBridge.Pdfium;

/// <summary>
/// Raw P/Invoke surface over the PDFium C API (fpdfview.h).
/// Calling convention is cdecl for every FPDF_* export.
/// </summary>
internal static partial class PdfiumNative
{
    private const string Lib = "pdfium";

    // FPDF_ERR_* codes returned by FPDF_GetLastError.
    public const uint FpdfErrSuccess = 0;
    public const uint FpdfErrUnknown = 1;
    public const uint FpdfErrFile = 2;
    public const uint FpdfErrFormat = 3;
    public const uint FpdfErrPassword = 4;
    public const uint FpdfErrSecurity = 5;
    public const uint FpdfErrPage = 6;

    // FPDF_RENDER flags.
    public const int FpdfAnnot = 0x01;
    public const int FpdfGrayscale = 0x08;
    public const int FpdfPrinting = 0x800;

    [LibraryImport(Lib, EntryPoint = "FPDF_InitLibrary")]
    public static partial void FPDF_InitLibrary();

    [LibraryImport(Lib, EntryPoint = "FPDF_DestroyLibrary")]
    public static partial void FPDF_DestroyLibrary();

    [LibraryImport(Lib, EntryPoint = "FPDF_LoadMemDocument")]
    public static partial nint FPDF_LoadMemDocument(nint dataBuf, int size, nint password);

    [LibraryImport(Lib, EntryPoint = "FPDF_GetPageCount")]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Lib, EntryPoint = "FPDF_LoadPage")]
    public static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Lib, EntryPoint = "FPDF_GetPageWidth")]
    public static partial double FPDF_GetPageWidth(nint page);

    [LibraryImport(Lib, EntryPoint = "FPDF_GetPageHeight")]
    public static partial double FPDF_GetPageHeight(nint page);

    [LibraryImport(Lib, EntryPoint = "FPDF_ClosePage")]
    public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Lib, EntryPoint = "FPDF_CloseDocument")]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Lib, EntryPoint = "FPDF_GetLastError")]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(Lib, EntryPoint = "FPDFBitmap_Create")]
    public static partial nint FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport(Lib, EntryPoint = "FPDFBitmap_Destroy")]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Lib, EntryPoint = "FPDFBitmap_GetBuffer")]
    public static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    [LibraryImport(Lib, EntryPoint = "FPDFBitmap_GetStride")]
    public static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport(Lib, EntryPoint = "FPDF_RenderPageBitmap")]
    public static partial void FPDF_RenderPageBitmap(
        nint bitmap, nint page,
        int startX, int startY, int sizeX, int sizeY,
        int rotate, int flags);

    [LibraryImport(Lib, EntryPoint = "FPDFBitmap_FillRect")]
    public static partial void FPDFBitmap_FillRect(
        nint bitmap, int left, int top, int width, int height, uint color);

    /// <summary>
    /// Loads pdfium.dll from the application directory (or the normal DLL search path).
    /// Fails fast with a clear message when the native dependency is missing.
    /// </summary>
    public static bool TryLoadNative()
    {
        return NativeLibrary.TryLoad("pdfium", typeof(PdfiumNative).Assembly, null, out _);
    }
}
