using StreetBiz.Application.Common.Models;

namespace StreetBiz.Application.Common.Interfaces;

/// <summary>Writes an export as a file. Pure: no data access.</summary>
public interface IPlatformReportRenderer
{
    byte[] RenderXlsx(PlatformReportDataset dataset);

    /// <summary>UTF-8 with a byte-order mark so Excel reads the Vietnamese correctly; sheets become titled sections.</summary>
    byte[] RenderCsv(PlatformReportDataset dataset);
}
