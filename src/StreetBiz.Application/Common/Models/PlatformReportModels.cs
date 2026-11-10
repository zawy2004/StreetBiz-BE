namespace StreetBiz.Application.Common.Models;

/// <summary>One table of an export. Cells are text, whole numbers, decimals or null.</summary>
public sealed record ReportSheet(
    string Name,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<object?>> Rows);

/// <summary>What an export contains; Truncated means the source had more rows than the cap allows.</summary>
public sealed record PlatformReportDataset(
    string Title,
    IReadOnlyList<ReportSheet> Sheets,
    bool Truncated = false);

/// <summary>One line of the administrator's export history (a ReportExports row).</summary>
public sealed record PlatformExportRow(
    long ExportId,
    string ReportType,
    DateOnly? From,
    DateOnly? To,
    string Format,
    string Status,
    string? FailureReason,
    string? FileName,
    string? FileUrl,
    DateTime RequestedAt,
    DateTime? CompletedAt);

/// <summary>A finished file, ready to stream back to the administrator.</summary>
public sealed record PlatformReportFile(byte[] Content, string FileName, string ContentType, long ExportId);
