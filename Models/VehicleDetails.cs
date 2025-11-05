using System.Text.Json.Serialization;

namespace PlateAnalysisApi.Models;

public class VehicleDetails
{
    [JsonPropertyName("cor")]
    public string Cor { get; set; } = string.Empty;

    [JsonPropertyName("tipo")]
    public string Tipo { get; set; } = string.Empty;

    [JsonPropertyName("fabricante")]
    public string Fabricante { get; set; } = string.Empty;

    [JsonPropertyName("placa_mercosul")]
    public string PlacaMercosul { get; set; } = string.Empty;
}

