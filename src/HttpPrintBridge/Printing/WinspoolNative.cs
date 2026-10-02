using System.Runtime.InteropServices;

namespace HttpPrintBridge.Printing;

/// <summary>
/// Blittable P/Invoke surface over winspool.drv and gdi32.dll used for silent printing
/// and printer enumeration. All structs use fixed buffers / nint so they are AOT safe.
/// </summary>
internal static partial class WinspoolNative
{
    // ---- EnumPrinters flags -------------------------------------------------
    public const uint PRINTER_ENUM_LOCAL = 0x00000002;
    public const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;

    // ---- PRINTER_ATTRIBUTE_* -----------------------------------------------
    public const uint PRINTER_ATTRIBUTE_DEFAULT = 0x00000004;
    public const uint PRINTER_ATTRIBUTE_NETWORK = 0x00000010;
    public const uint PRINTER_ATTRIBUTE_HIDDEN = 0x00000020;
    public const uint PRINTER_ATTRIBUTE_LOCAL = 0x00000040;
    public const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x00000400;

    // ---- PRINTER_STATUS_* (subset) ------------------------------------------
    public const uint PRINTER_STATUS_PAUSED = 0x00000001;
    public const uint PRINTER_STATUS_ERROR = 0x00000002;
    public const uint PRINTER_STATUS_PENDING_DELETION = 0x00000004;
    public const uint PRINTER_STATUS_PAPER_JAM = 0x00000008;
    public const uint PRINTER_STATUS_PAPER_OUT = 0x00000010;
    public const uint PRINTER_STATUS_OFFLINE = 0x00000020;
    public const uint PRINTER_STATUS_DOOR_OPEN = 0x00000040;

    // ---- DocumentProperties fMode (wingdi.h: DM_UPDATE/COPY/PROMPT/MODIFY) --
    public const uint DM_OUT_DEFAULT = 0x0001; // DM_UPDATE
    public const uint DM_OUT_BUFFER = 0x0002;  // DM_COPY
    public const uint DM_IN_PROMPT = 0x0004;   // DM_PROMPT
    public const uint DM_IN_BUFFER = 0x0008;   // DM_MODIFY

    // ---- dmFields bits ------------------------------------------------------
    public const int DM_ORIENTATION = 0x00000001;
    public const int DM_PAPERSIZE = 0x00000002;
    public const int DM_PAPERLENGTH = 0x00000004;
    public const int DM_PAPERWIDTH = 0x00000008;
    public const int DM_SCALE = 0x00000010;
    public const int DM_COPIES = 0x00000100;
    public const int DM_COLOR = 0x00000800;

    public const short DMPAPER_USER = 256;
    public const short DMORIENT_PORTRAIT = 1;
    public const short DMORIENT_LANDSCAPE = 2;

    // ---- GDI constants ------------------------------------------------------
    public const int HORZRES = 8;
    public const int VERTRES = 10;
    public const int LOGPIXELSX = 88;
    public const int LOGPIXELSY = 90;
    public const int PHYSICALWIDTH = 110;
    public const int PHYSICALHEIGHT = 111;
    public const int PHYSICALOFFSETX = 112;
    public const int PHYSICALOFFSETY = 113;

    public const int HALFTONE = 4;
    public const int SRCCOPY = 0x00CC0020;
    public const int DIB_RGB_COLORS = 0;
    public const int BI_RGB = 0;
    public const int SP_ERROR = -1;

    // ---- Structs ------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct DOCINFO
    {
        public int cbSize;
        public nint lpszDocName;
        public nint lpszOutput;
        public nint lpszDatatype;
        public int fwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    /// <summary>
    /// PRINTER_INFO_2 — every string/pointer field is kept as nint; strings are read
    /// with Marshal.PtrToStringUni. 220-byte DEVMODE pointed to by pDevMode is unused.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_INFO_2
    {
        public nint pServerName;
        public nint pPrinterName;
        public nint pShareName;
        public nint pPortName;
        public nint pDriverName;
        public nint pComment;
        public nint pLocation;
        public nint pDevMode;
        public nint pSepFile;
        public nint pPrintProcessor;
        public nint pDatatype;
        public nint pParameters;
        public nint pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    /// <summary>
    /// DEVMODEW fixed portion (220 bytes). Device/form names use fixed char buffers
    /// so the struct stays blittable for NativeAOT. Driver-specific extra bytes after
    /// the fixed part are preserved by allocating the size DocumentProperties reports.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct DEVMODE
    {
        public fixed char dmDeviceName[32];
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public short dmOrientation;
        public short dmPaperSize;
        public short dmPaperLength;
        public short dmPaperWidth;
        public short dmScale;
        public short dmCopies;
        public short dmDefaultSource;
        public short dmPrintQuality;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        public fixed char dmFormName[32];
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    // ---- winspool.drv -------------------------------------------------------

    [LibraryImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumPrinters(
        uint flags, nint name, uint level,
        nint pPrinterEnum, uint cbBuf,
        out uint pcbNeeded, out uint pcReturned);

    [LibraryImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDefaultPrinter(nint pszBuffer, ref int pcchBuffer);

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenPrinter(string pPrinterName, out nint phPrinter, nint pDefault);

    [LibraryImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClosePrinter(nint hPrinter);

    [LibraryImport("winspool.drv", EntryPoint = "DocumentPropertiesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DocumentProperties(
        nint hWnd, nint hPrinter, string pDeviceName,
        nint pDevModeOutput, nint pDevModeInput, uint fMode);

    // ---- gdi32 --------------------------------------------------------------

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateDC(string pwszDriver, string pwszDevice, nint pszPort, nint pdm);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "StartDocW", SetLastError = true)]
    public static partial int StartDoc(nint hdc, in DOCINFO lpdi);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int EndDoc(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int StartPage(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int EndPage(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int AbortDoc(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int GetDeviceCaps(nint hdc, int index);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int SetStretchBltMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetBrushOrgEx(nint hdc, int nXOrg, int nYOrg, nint lppt);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int StretchDIBits(
        nint hdc,
        int xDest, int yDest, int destWidth, int destHeight,
        int xSrc, int ySrc, int srcWidth, int srcHeight,
        nint lpBits, in BITMAPINFOHEADER lpbmi,
        uint iUsage, int rop);
}
