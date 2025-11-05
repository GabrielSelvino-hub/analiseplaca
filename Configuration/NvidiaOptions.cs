namespace PlateAnalysisApi.Configuration;

public class NvidiaOptions
{
    public const string SectionName = "Nvidia";
    
    public string ApiKey { get; set; } = string.Empty;
    public string TextModel { get; set; } = "meta/llama-3.1-8b-instruct";
    public string VisionModel { get; set; } = "nvidia/nemotron-nano-12b-v2-vl";
    public string BaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";
}

