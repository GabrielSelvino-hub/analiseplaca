using PlateAnalysisApi.Configuration;
using PlateAnalysisApi.Models;
using PlateAnalysisApi.Services;
using System.Linq;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Configuração JSON
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Configuração
builder.Services.Configure<GeminiOptions>(
    builder.Configuration.GetSection(GeminiOptions.SectionName));

// Serviços
builder.Services.AddHttpClient<GeminiService>();
builder.Services.AddSingleton<PlateCacheService>();
builder.Services.AddSingleton<GeminiService>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();

// Validação básica de base64
bool IsValidBase64(string? base64String)
{
    if (string.IsNullOrWhiteSpace(base64String))
        return false;

    base64String = base64String.Trim();
    
    // Remove data URL prefix se existir
    if (base64String.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
    {
        var commaIndex = base64String.IndexOf(',');
        if (commaIndex > 0)
            base64String = base64String[(commaIndex + 1)..];
    }

    // Verifica se é base64 válido
    if (base64String.Length % 4 != 0)
        return false;

    try
    {
        Convert.FromBase64String(base64String);
        return true;
    }
    catch
    {
        return false;
    }
}

// Normaliza base64 removendo prefixo data URL se existir
string NormalizeBase64(string base64String)
{
    if (string.IsNullOrWhiteSpace(base64String))
        return base64String;

    base64String = base64String.Trim();
    
    if (base64String.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
    {
        var commaIndex = base64String.IndexOf(',');
        if (commaIndex > 0)
            return base64String[(commaIndex + 1)..];
    }

    return base64String;
}

// Valida mime type
bool IsValidMimeType(string? mimeType)
{
    if (string.IsNullOrWhiteSpace(mimeType))
        return false;

    var validTypes = new[] { "image/jpeg", "image/jpg", "image/png", "image/gif", "image/webp" };
    return validTypes.Contains(mimeType.ToLowerInvariant());
}

app.MapPost("/api/analyze-plate", async (PlateAnalysisRequest request, 
    GeminiService geminiService, 
    PlateCacheService cacheService, 
    ILogger<Program> logger) =>
{
    try
    {
        // Validação de entrada
        if (string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            return Results.BadRequest(new PlateAnalysisResponse
            {
                Erro = "A imagem em base64 é obrigatória."
            });
        }

        if (!IsValidBase64(request.ImageBase64))
        {
            return Results.BadRequest(new PlateAnalysisResponse
            {
                Erro = "O formato da imagem em base64 é inválido."
            });
        }

        if (!IsValidMimeType(request.MimeType))
        {
            return Results.BadRequest(new PlateAnalysisResponse
            {
                Erro = $"O tipo MIME '{request.MimeType}' não é suportado. Use: image/jpeg, image/png, image/gif ou image/webp."
            });
        }

        var normalizedBase64 = NormalizeBase64(request.ImageBase64);
        var mimeType = request.MimeType.ToLowerInvariant();

        // PASSO 1: Extração da placa (OCR)
        logger.LogInformation("Iniciando extração da placa via OCR...");
        string plateText;

        try
        {
            var plateJson = await geminiService.GetPlateTextAsync(normalizedBase64, mimeType);
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var plateResult = JsonSerializer.Deserialize<PlateDetectionResult>(plateJson, jsonOptions);
            plateText = plateResult?.Placa ?? "Placa não encontrada";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro na extração da placa (OCR)");
            plateText = "Erro de Processamento.";
        }

        var plateFound = plateText != "Placa não encontrada" && plateText != "Erro de Processamento.";

        // Verificação de duplicatas
        if (plateFound && cacheService.IsDuplicate(plateText))
        {
            logger.LogWarning("Placa duplicada detectada: {Plate}", plateText);
            return Results.Ok(new PlateAnalysisResponse
            {
                Placa = plateText,
                Duplicada = true,
                Erro = $"Atenção: A placa \"{plateText}\" já foi processada nesta sessão. O processamento foi interrompido."
            });
        }

        // Se não for duplicata, adiciona ao cache
        if (plateFound)
        {
            cacheService.AddPlate(plateText);
        }

        VehicleDetails? vehicleDetails = null;
        CroppedPlateImage? croppedImage = null;

        // PASSO 2: Análise de detalhes do veículo
        if (plateFound)
        {
            try
            {
                logger.LogInformation("Analisando detalhes do veículo para placa: {Plate}", plateText);
                vehicleDetails = await geminiService.GetVehicleDetailsAsync(normalizedBase64, mimeType, plateText);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro na análise dos detalhes do veículo");
                // Continua mesmo se falhar, mas não retorna detalhes
            }
        }

        // PASSO 3: Recorte da imagem da placa
        // COMENTADO: Recorte de imagem desabilitado pois a API gratuita não suporta
        // A chamada à API de recorte foi comentada para evitar erros de quota
        // if (plateFound)
        // {
        //     logger.LogInformation("Recortando imagem da placa: {Plate}", plateText);
        //     var (croppedBase64, croppedMimeType, errorMsg) = await geminiService.CropPlateImageAsync(normalizedBase64, mimeType, plateText);
        //     
        //     if (errorMsg != null)
        //     {
        //         logger.LogWarning("Não foi possível recortar imagem: {Error}", errorMsg);
        //         erroRecorte = errorMsg;
        //     }
        //     else if (croppedBase64 != null)
        //     {
        //         croppedImage = new CroppedPlateImage
        //         {
        //             Base64 = croppedBase64,
        //             MimeType = croppedMimeType ?? mimeType
        //         };
        //         logger.LogInformation("Imagem da placa recortada com sucesso");
        //     }
        //     else
        //     {
        //         logger.LogWarning("Recorte de imagem retornou null sem mensagem de erro");
        //         erroRecorte = "Não foi possível recortar a imagem da placa.";
        //     }
        // }
        
        // Define mensagem informativa no campo imagemPlacaRecortada quando o recorte não está disponível
        if (plateFound)
        {
            croppedImage = new CroppedPlateImage
            {
                Base64 = null,
                MimeType = null,
                Mensagem = "API Gratuita não pode fazer recorte de placa, apenas análise. O recorte de imagem requer um plano pago da API do Google Gemini."
            };
        }

        // Monta resposta
        var response = new PlateAnalysisResponse
        {
            Placa = plateText,
            Duplicada = false,
            DetalhesVeiculo = vehicleDetails,
            ImagemPlacaRecortada = croppedImage,
            Erro = null // Campo erro apenas para erros reais de processamento
        };

        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Erro geral no processamento da análise de placa");
        return Results.StatusCode(500);
    }
})
.WithName("AnalyzePlate");

// Endpoint de health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .WithName("HealthCheck");

app.Run();

