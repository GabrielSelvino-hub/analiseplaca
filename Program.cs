using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using PlateAnalysisApi.Configuration;
using PlateAnalysisApi.Models;
using PlateAnalysisApi.Services;
using System.Linq;
using System.Text.Json;
using Serilog;

// Configuração do Serilog para salvar logs na pasta "Log"
var logPath = Path.Combine(Directory.GetCurrentDirectory(), "Log", "app-.log");
var logDirectory = Path.GetDirectoryName(logPath);
if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
{
    Directory.CreateDirectory(logDirectory);
}

Log.Logger = new LoggerConfiguration()
    .WriteTo.File(
        path: logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1))
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Configura o Serilog como provider de logging
builder.Host.UseSerilog();

// Configuração JSON
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Configuração
builder.Services.Configure<NvidiaOptions>(
    builder.Configuration.GetSection(NvidiaOptions.SectionName));
builder.Services.Configure<GeminiOptions>(
    builder.Configuration.GetSection(GeminiOptions.SectionName));

// Serviços - Registra ambos os serviços
builder.Services.AddHttpClient<NvidiaService>();
builder.Services.AddHttpClient<GeminiService>();
builder.Services.AddSingleton<PlateCacheService>();
builder.Services.AddSingleton<NvidiaService>();
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

// Configurar arquivos estáticos
app.UseStaticFiles();

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

// Método auxiliar para processar análise de placa
async Task<IResult> ProcessPlateAnalysis(
    PlateAnalysisRequest request,
    IAiService aiService,
    PlateCacheService cacheService,
    string apiUtilizada,
    Microsoft.Extensions.Logging.ILogger logger)
{
    try
    {
        // Validação de entrada
        if (string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            return Results.BadRequest(new PlateAnalysisResponse
            {
                Erro = "A imagem em base64 é obrigatória.",
                ApiUtilizada = apiUtilizada
            });
        }

        if (!IsValidBase64(request.ImageBase64))
        {
            return Results.BadRequest(new PlateAnalysisResponse
            {
                Erro = "O formato da imagem em base64 é inválido.",
                ApiUtilizada = apiUtilizada
            });
        }

        if (!IsValidMimeType(request.MimeType))
        {
            return Results.BadRequest(new PlateAnalysisResponse
            {
                Erro = $"O tipo MIME '{request.MimeType}' não é suportado. Use: image/jpeg, image/png, image/gif ou image/webp.",
                ApiUtilizada = apiUtilizada
            });
        }

        var normalizedBase64 = NormalizeBase64(request.ImageBase64);
        var mimeType = request.MimeType.ToLowerInvariant();

        // PASSO 1: Extração da placa (OCR)
        logger.LogInformation("Iniciando extração da placa via OCR...");
        string plateText;

        try
        {
            var plateJson = await aiService.GetPlateTextAsync(normalizedBase64, mimeType);
            
            // Limpa a resposta removendo markdown code blocks se existirem (principalmente para NVIDIA)
            plateJson = plateJson.Trim();
            if (plateJson.StartsWith("```json"))
            {
                plateJson = plateJson.Substring(7);
            }
            if (plateJson.StartsWith("```"))
            {
                plateJson = plateJson.Substring(3);
            }
            if (plateJson.EndsWith("```"))
            {
                plateJson = plateJson.Substring(0, plateJson.Length - 3);
            }
            plateJson = plateJson.Trim();
            
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
            var errorMessage = ex is HttpRequestException httpEx ? httpEx.Message : ex.ToString();
            return Results.BadRequest(new PlateAnalysisResponse
            {
                Erro = errorMessage,
                ApiUtilizada = apiUtilizada
            });
        }

        var plateFound = plateText != "Placa não encontrada";

        // Verificação de duplicatas
        if (plateFound && cacheService.IsDuplicate(plateText))
        {
            logger.LogWarning("Placa duplicada detectada: {Plate}", plateText);
            return Results.Ok(new PlateAnalysisResponse
            {
                Placa = plateText,
                Duplicada = true,
                Erro = $"Atenção: A placa \"{plateText}\" já foi processada nesta sessão. O processamento foi interrompido.",
                ApiUtilizada = apiUtilizada
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
                vehicleDetails = await aiService.GetVehicleDetailsAsync(normalizedBase64, mimeType, plateText);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro na análise dos detalhes do veículo");
                var errorMessage = ex is HttpRequestException httpEx ? httpEx.Message : ex.ToString();
                return Results.BadRequest(new PlateAnalysisResponse
                {
                    Placa = plateText,
                    Erro = errorMessage,
                    ApiUtilizada = apiUtilizada
                });
            }
        }
        var erroRecorte = string.Empty;
        // PASSO 3: Recorte da imagem da placa
         if (plateFound)
         {
             logger.LogInformation("Recortando imagem da placa: {Plate}", plateText);
             var (croppedBase64, croppedMimeType, errorMsg) = await aiService.CropPlateImageAsync(normalizedBase64, mimeType, plateText);
             
             if (errorMsg != null)
             {
                 logger.LogWarning("Não foi possível recortar imagem: {Error}", errorMsg);
                 erroRecorte = errorMsg;
             }
             else if (croppedBase64 != null)
             {
                 croppedImage = new CroppedPlateImage
                 {
                     Base64 = croppedBase64,
                     MimeType = croppedMimeType ?? mimeType
                 };
                 logger.LogInformation("Imagem da placa recortada com sucesso");
             }
             else
             {
                 logger.LogWarning("Recorte de imagem retornou null sem mensagem de erro");
                 erroRecorte = "Não foi possível recortar a imagem da placa.";
             }
         }

        // Monta resposta
        var response = new PlateAnalysisResponse
        {
            Placa = plateText,
            Duplicada = false,
            DetalhesVeiculo = vehicleDetails,
            ImagemPlacaRecortada = croppedImage,
            Erro = erroRecorte, // Campo erro apenas para erros reais de processamento
            ApiUtilizada = apiUtilizada
        };

        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Erro geral no processamento da análise de placa");
        var errorMessage = ex is HttpRequestException httpEx ? httpEx.Message : ex.ToString();
        return Results.BadRequest(new PlateAnalysisResponse
        {
            Erro = errorMessage,
            ApiUtilizada = apiUtilizada
        });
    }
}

// Rota para Gemini
app.MapPost("/gemini/analyze-plate", async (PlateAnalysisRequest request,
    GeminiService geminiService,
    PlateCacheService cacheService,
    ILogger<Program> logger) =>
{
    return await ProcessPlateAnalysis(request, geminiService, cacheService, "Gemini", logger);
})
.WithName("AnalyzePlateGemini");

// Rota para Nvidia
app.MapPost("/nvidia/analyze-plate", async (PlateAnalysisRequest request,
    NvidiaService nvidiaService,
    PlateCacheService cacheService,
    ILogger<Program> logger) =>
{
    return await ProcessPlateAnalysis(request, nvidiaService, cacheService, "NVIDIA", logger);
})
.WithName("AnalyzePlateNvidia");

// Endpoint de health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .WithName("HealthCheck");

// Endpoint para servir a página de teste (fallback se não for encontrado em wwwroot)
app.MapGet("/", async (HttpContext context) =>
{
    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "index.html");
    if (File.Exists(filePath))
    {
        return Results.File(filePath, "text/html");
    }
    return Results.NotFound("Página não encontrada");
})
.WithName("Index");

// Log de inicialização da aplicação
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("=== Aplicação iniciada em {Timestamp} ===", DateTime.UtcNow);
logger.LogInformation("Diretório de logs: {LogDirectory}", logDirectory);

try
{
    app.Run();
}
finally
{
    logger.LogInformation("=== Aplicação encerrada em {Timestamp} ===", DateTime.UtcNow);
    Log.CloseAndFlush();
}

