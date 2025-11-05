using System.Text.Json.Serialization;

namespace PlateAnalysisApi.Models;

public class PlateDetectionResult
{
    [JsonPropertyName("placa")]
    public string Placa { get; set; } = string.Empty;
}

