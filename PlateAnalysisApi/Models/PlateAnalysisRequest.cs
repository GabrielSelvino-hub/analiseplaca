namespace PlateAnalysisApi.Models;

public class PlateAnalysisRequest
{
    public string ImageBase64 { get; set; } = string.Empty;
    public string MimeType { get; set; } = "image/jpeg";
}

