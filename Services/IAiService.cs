using PlateAnalysisApi.Models;

namespace PlateAnalysisApi.Services;

public interface IAiService
{
    Task<string> GetPlateTextAsync(string imageBase64, string mimeType, CancellationToken cancellationToken = default);
    Task<VehicleDetails> GetVehicleDetailsAsync(string imageBase64, string mimeType, string extractedPlate, CancellationToken cancellationToken = default);
    Task<(string? base64, string? mimeType, string? errorMessage)> CropPlateImageAsync(string imageBase64, string mimeType, string plate, CancellationToken cancellationToken = default);
}

