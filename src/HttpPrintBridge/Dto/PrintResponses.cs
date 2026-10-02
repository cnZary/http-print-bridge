using System.Text.Json.Serialization;

namespace HttpPrintBridge.Dto;

public sealed record StatusResponse(
    string Service,
    string Version,
    string? DefaultPrinter,
    int DefaultDpi,
    int PrinterCount);

public sealed record PrinterListResponse(
    string? Default,
    IReadOnlyList<PrinterEntry> Printers);

public sealed record PrinterEntry(
    string Name,
    string? Server,
    string? Port,
    string? Driver,
    bool IsDefault,
    bool IsNetwork,
    bool IsOffline,
    uint Status);

public sealed record PrintResponse(
    bool Ok,
    string Printer,
    int Pages,
    int Copies,
    int Dpi,
    long Bytes);

public sealed record ErrorResponse(bool Ok, string Error);

[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(PrinterListResponse))]
[JsonSerializable(typeof(PrintResponse))]
[JsonSerializable(typeof(ErrorResponse))]
public sealed partial class AppJsonContext : JsonSerializerContext;
