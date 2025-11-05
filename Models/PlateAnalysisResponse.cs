namespace PlateAnalysisApi.Models;

public class PlateAnalysisResponse
{
    public string Placa { get; set; } = string.Empty;
    public bool Duplicada { get; set; }
    public VehicleDetails? DetalhesVeiculo { get; set; }
    public CroppedPlateImage? ImagemPlacaRecortada { get; set; }
    public string? Erro { get; set; }
    public string? ApiUtilizada { get; set; }
}

