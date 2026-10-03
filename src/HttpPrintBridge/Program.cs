using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HttpPrintBridge.Auth;
using HttpPrintBridge.Dto;
using HttpPrintBridge.Pdfium;
using HttpPrintBridge.Printing;
using Microsoft.AspNetCore.Http.Features;

namespace HttpPrintBridge;

public static class Program
{
    private const string ServiceName = "http-print-bridge";

    public static async Task<int> Main(string[] args)
    {
        // A bare "--hide-console" would make .NET's command-line config provider swallow
        // the next argument as its value (breaking --urls). Normalise it up front.
        args = NormalizeSwitchArgs(args, "--hide-console");

        // Load appsettings.json from beside the exe regardless of the launch directory.
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // --- configuration (command line > environment > appsettings.json) ---
        long maxUploadBytes = ReadLong(builder.Configuration, "MaxUploadBytes", "PRINTBRIDGE_MAX_UPLOAD_BYTES", 200L * 1024 * 1024);
        int defaultDpi = (int)ReadLong(builder.Configuration, "DefaultDpi", "PRINTBRIDGE_DEFAULT_DPI", 300);
        int printTimeoutSeconds = (int)ReadLong(builder.Configuration, "PrintTimeoutSeconds", "PRINTBRIDGE_PRINT_TIMEOUT", 120);
        bool hideConsole = ReadFlag(args, builder.Configuration, "--hide-console", "PRINTBRIDGE_HIDE_CONSOLE", "HideConsole");
        string key = ReadString(builder.Configuration, "Key", "PRINTBRIDGE_KEY") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(key))
        {
            // Reuse the key persisted by a previous hidden run so restarts stay usable.
            string keyFile = Path.Combine(AppContext.BaseDirectory, "key.txt");
            if (File.Exists(keyFile))
            {
                key = File.ReadAllText(keyFile).Trim();
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                Console.WriteLine();
                Console.WriteLine("  ============================================================");
                Console.WriteLine("   No auth key configured — generated a random key:");
                Console.WriteLine($"     {key}");
                Console.WriteLine("   Configure it via --key=... or PRINTBRIDGE_KEY to keep it");
                Console.WriteLine("   stable across restarts.");
                Console.WriteLine("  ============================================================");
                Console.WriteLine();

                // With a hidden console nobody can read the banner, so persist the key.
                if (hideConsole)
                {
                    File.WriteAllText(keyFile, key);
                    Console.WriteLine($"   Key also written to: {keyFile}");
                    Console.WriteLine();
                }
            }
        }

        // Hide the console window once startup output has been written. With an
        // auto-generated key the banner above is the only place the key is shown,
        // so pair --hide-console with --key=... to keep the key reachable.
        if (hideConsole)
            Win32.ConsoleWindow.HideOwnConsole();

        var keyAuth = new KeyAuth(key);

        builder.Services.Configure<FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = maxUploadBytes;
            o.ValueLengthLimit = int.MaxValue;
            o.MultipartHeadersLengthLimit = int.MaxValue;
        });
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxUploadBytes);

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.TypeInfoResolver = AppJsonContext.Default;
        });

        builder.Services.AddSingleton(_ => new PrinterService(TimeSpan.FromSeconds(printTimeoutSeconds)));
        builder.Services.AddSingleton(_ => new PdfiumLibrary());

        WebApplication app = builder.Build();

        // Fail fast with an actionable message when the native renderer is missing.
        if (!PdfiumNative.TryLoadNative())
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("  FATAL: pdfium.dll could not be loaded.");
            Console.Error.WriteLine("  Place pdfium.dll in the same directory as this executable.");
            Console.Error.WriteLine("  Download it with:  powershell -File scripts/fetch-pdfium.ps1");
            Console.Error.WriteLine();
            return 2;
        }

        PrinterService printers = app.Services.GetRequiredService<PrinterService>();
        app.Services.GetRequiredService<PdfiumLibrary>();

        // ------------------------------------------------------------ status
        // ASP.NET routing treats a trailing slash as insignificant, so /{key} serves
        // both /{key} and /{key}/ — registering both would be ambiguous.
        app.MapGet("/{key}", (string key) =>
        {
            if (!keyAuth.IsMatch(key)) return Unauthorized();
            string? def = SafeDefault(printers);
            int count = SafeCount(printers);
            return Json(new StatusResponse(ServiceName, GetVersion(), def, defaultDpi, count));
        });

        // -------------------------------------------------------------- list
        app.MapGet("/{key}/list", (string key) =>
        {
            if (!keyAuth.IsMatch(key)) return Unauthorized();
            try
            {
                IReadOnlyList<PrinterInfo> list = printers.ListPrinters();
                string? def = list.FirstOrDefault(p => p.IsDefault)?.Name ?? printers.TryGetDefaultPrinter();
                return Json(new PrinterListResponse(def, list.Select(p => new PrinterEntry(
                    p.Name, p.Server, p.Port, p.Driver, p.IsDefault, p.IsNetwork, p.IsOffline, p.Status)).ToList()));
            }
            catch (Exception ex)
            {
                return Error(StatusCodes.Status500InternalServerError, ex.Message);
            }
        });

        // ------------------------------------------------------------- print
        app.MapPost("/{key}/print", (string key, HttpRequest req) => HandlePrintAsync(key, req));
        app.MapPost("/{key}", (string key, HttpRequest req) => HandlePrintAsync(key, req));

        async Task<IResult> HandlePrintAsync(string key, HttpRequest req)
        {
            if (!keyAuth.IsMatch(key)) return Unauthorized();

            // --- query parameters ---
            string? printer = req.Query["printer"].FirstOrDefault();
            string? output = req.Query["output"].FirstOrDefault();
            if (!int.TryParse(req.Query["copies"].FirstOrDefault(), out int copies)) copies = 1;
            if (!int.TryParse(req.Query["dpi"].FirstOrDefault(), out int dpi)) dpi = defaultDpi;

            if (copies is < 1 or > 999)
                return Error(StatusCodes.Status400BadRequest, "copies must be between 1 and 999.");
            if (dpi is < 36 or > 600)
                return Error(StatusCodes.Status400BadRequest, "dpi must be between 36 and 600.");

            // --- read PDF bytes (multipart or raw body) ---
            byte[] pdf;
            try
            {
                pdf = await ReadPdfAsync(req, maxUploadBytes);
            }
            catch (TooLargeException)
            {
                return Error(StatusCodes.Status413PayloadTooLarge, $"PDF exceeds the {maxUploadBytes} byte upload limit.");
            }
            catch (Exception ex)
            {
                return Error(StatusCodes.Status400BadRequest, $"Could not read upload: {ex.Message}");
            }

            if (pdf.Length == 0)
                return Error(StatusCodes.Status400BadRequest, "No PDF received. Send multipart field 'file' or a raw application/pdf body.");

            // --- render + silent print ---
            try
            {
                using PdfDocument doc = PdfDocument.Load(pdf);
                PrintResult result = printers.Print(doc, printer, copies, dpi, output, req.HttpContext.RequestAborted);
                return Json(new PrintResponse(true, result.Printer, result.Pages, result.Copies, result.Dpi, pdf.Length));
            }
            catch (PrinterNotFoundException ex)
            {
                return Error(StatusCodes.Status404NotFound, ex.Message);
            }
            catch (PrintBusyException ex)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, ex.Message);
            }
            catch (PdfiumException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (PrintException ex)
            {
                return Error(StatusCodes.Status500InternalServerError, ex.Message);
            }
            catch (OperationCanceledException)
            {
                return Error(StatusCodes.Status499ClientClosedRequest, "Print job was cancelled.");
            }
        }

        await app.RunAsync();
        return 0;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Rewrites bare boolean switches (<c>--flag</c>) into <c>--flag=true</c> so the
    /// configuration command-line provider does not consume the following argument.
    /// </summary>
    private static string[] NormalizeSwitchArgs(string[] args, params string[] switches)
    {
        for (int i = 0; i < args.Length; i++)
        {
            foreach (string sw in switches)
            {
                if (args[i].Equals(sw, StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = sw + "=true";
                    break;
                }
            }
        }
        return args;
    }

    private static async Task<byte[]> ReadPdfAsync(HttpRequest req, long maxUploadBytes)
    {
        if (req.HasFormContentType)
        {
            IFormCollection form = await req.ReadFormAsync(req.HttpContext.RequestAborted);
            IFormFile? file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null)
                throw new InvalidOperationException("multipart/form-data contains no file part (expected field name 'file').");
            if (file.Length > maxUploadBytes || file.Length > int.MaxValue)
                throw new TooLargeException();

            // Read straight into the exact-size result array: no intermediate
            // MemoryStream growth and no ToArray copy for large uploads.
            byte[] pdf = GC.AllocateUninitializedArray<byte>((int)file.Length);
            await using Stream src = file.OpenReadStream();
            await src.ReadExactlyAsync(pdf, req.HttpContext.RequestAborted);
            return pdf;
        }

        if (req.ContentLength is > 0 and var len && len > maxUploadBytes)
            throw new TooLargeException();

        if (req.ContentLength is > 0 and var known)
        {
            // Content-Length present: same exact-size single-read path.
            byte[] pdf = GC.AllocateUninitializedArray<byte>((int)known);
            await req.Body.ReadExactlyAsync(pdf, req.HttpContext.RequestAborted);
            return pdf;
        }

        // Chunked body of unknown length (rare): grow a buffer, then one final copy
        // into the exact-size array that PdfDocument pins for its lifetime.
        await using var buffer = new MemoryStream();
        await req.Body.CopyToAsync(buffer, req.HttpContext.RequestAborted);
        if (buffer.Length > maxUploadBytes)
            throw new TooLargeException();
        return buffer.ToArray();
    }

    private static IResult Json<T>(T value) =>
        Results.Json(value, (JsonTypeInfo<T>)AppJsonContext.Default.GetTypeInfo(typeof(T))!);

    private static IResult Unauthorized() =>
        Error(StatusCodes.Status401Unauthorized, "Invalid or missing access key.");

    private static IResult Error(int status, string message) =>
        Results.Json(new ErrorResponse(false, message), AppJsonContext.Default.ErrorResponse, statusCode: status);

    private static string? SafeDefault(PrinterService printers)
    {
        try { return printers.TryGetDefaultPrinter(); }
        catch { return null; }
    }

    private static int SafeCount(PrinterService printers)
    {
        try { return printers.ListPrinters().Count; }
        catch { return 0; }
    }

    private static string GetVersion()
    {
        Version? v = typeof(Program).Assembly.GetName().Version;
        return v?.ToString(3) ?? "0.0.0";
    }

    private static long ReadLong(IConfiguration cfg, string name, string envName, long fallback)
    {
        string? s = ReadString(cfg, name, envName);
        return long.TryParse(s, out long v) ? v : fallback;
    }

    private static string? ReadString(IConfiguration cfg, string name, string envName)
    {
        // appsettings.json ships empty strings as placeholders; treat them as unset so
        // --key=... / PRINTBRIDGE_KEY always win over the placeholder.
        foreach (string? candidate in new[] { cfg[name], cfg[envName], cfg[$"PrintBridge:{name}"] })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Boolean flag supporting both the bare switch (<c>--hide-console</c>) and the
    /// explicit form (<c>--hide-console=true|false</c>), plus env var and appsettings.
    /// </summary>
    private static bool ReadFlag(string[] args, IConfiguration cfg, string switchName,
        string envName, string configName)
    {
        foreach (string arg in args)
        {
            if (arg.Equals(switchName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (arg.StartsWith(switchName + "=", StringComparison.OrdinalIgnoreCase))
                return IsTruthy(arg[(switchName.Length + 1)..]);
        }

        foreach (string? candidate in new[] { cfg[envName], cfg[$"PrintBridge:{configName}"], cfg[configName] })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return IsTruthy(candidate);
        }

        return false;
    }

    private static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private sealed class TooLargeException : Exception;
}
