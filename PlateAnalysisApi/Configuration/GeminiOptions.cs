namespace PlateAnalysisApi.Configuration;

public class GeminiOptions
{
    public const string SectionName = "Gemini";
    
    public string ApiKey { get; set; } = string.Empty;
    public string TextModel { get; set; } = "gemini-2.5-flash-preview-09-2025";
    public string ImageModel { get; set; } = "gemini-2.5-flash-image-preview";
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";
}

