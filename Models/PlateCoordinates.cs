namespace PlateAnalysisApi.Models;

/// <summary>
/// Representa as coordenadas normalizadas (0.0 a 1.0) da placa veicular na imagem.
/// </summary>
public class PlateCoordinates
{
    /// <summary>
    /// Posição X normalizada (0.0 a 1.0) do canto superior esquerdo da placa.
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// Posição Y normalizada (0.0 a 1.0) do canto superior esquerdo da placa.
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    /// Largura normalizada (0.0 a 1.0) da placa.
    /// </summary>
    public double Width { get; set; }

    /// <summary>
    /// Altura normalizada (0.0 a 1.0) da placa.
    /// </summary>
    public double Height { get; set; }
}
