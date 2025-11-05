namespace PlateAnalysisApi.Configuration;

public class NvidiaOptions
{
    public const string SectionName = "Nvidia";
    
    public string ApiKey { get; set; } = string.Empty;
    public string TextModel { get; set; } = "meta/llama-3.1-8b-instruct";
    public string VisionModel { get; set; } = "meta/llama-3.1-70b-instruct";
    public string BaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";
}

