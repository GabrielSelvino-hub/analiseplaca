using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlateAnalysisApi.Configuration;
using PlateAnalysisApi.Models;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Linq;
using ImagingEncoder = System.Drawing.Imaging.Encoder;

namespace PlateAnalysisApi.Services;

public class GeminiService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiService> _logger;
    private const int MaxRetries = 3;

    public GeminiService(HttpClient httpClient, IOptions<GeminiOptions> options, ILogger<GeminiService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        // Validate API key on service initialization
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogError("Gemini API Key is missing or empty. Please configure it in appsettings.json");
            throw new InvalidOperationException("Gemini API Key is not configured. Please set 'Gemini:ApiKey' in appsettings.json or environment variables.");
        }

        // Basic validation: Gemini API keys typically start with "AIza"
        if (!_options.ApiKey.StartsWith("AIza", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Gemini API Key format may be incorrect. Expected format: AIza... (Current key starts with: {KeyPrefix})", 
                _options.ApiKey.Length > 4 ? _options.ApiKey.Substring(0, 4) : "too short");
        }
    }

    public async Task<string> GetPlateTextAsync(string imageBase64, string mimeType, CancellationToken cancellationToken = default)
    {
        var plateDetectionSchema = new
        {
            type = "OBJECT",
            properties = new
            {
                placa = new
                {
                    type = "STRING",
                    description = "O número da placa identificada, ex: 'ABC1234' ou 'JKL5M67'. Se não encontrada, deve ser 'Placa não encontrada'."
                },
                nivelConfianca = new
                {
                    type = "NUMBER",
                    description = "Nível de confiança da leitura da placa, um valor entre 0.0 e 1.0, onde 1.0 indica 100% de confiança. Se a placa não foi encontrada, use 0.0."
                },
                coordenadas = new
                {
                    type = "OBJECT",
                    description = "Coordenadas normalizadas (0.0 a 1.0) da placa na imagem. Se a placa não foi encontrada, use valores 0.0.",
                    properties = new
                    {
                        x = new { type = "NUMBER", description = "Posição X normalizada (0.0 a 1.0) do canto superior esquerdo da placa." },
                        y = new { type = "NUMBER", description = "Posição Y normalizada (0.0 a 1.0) do canto superior esquerdo da placa." },
                        width = new { type = "NUMBER", description = "Largura normalizada (0.0 a 1.0) da placa." },
                        height = new { type = "NUMBER", description = "Altura normalizada (0.0 a 1.0) da placa." }
                    },
                    required = new[] { "x", "y", "width", "height" }
                }
            },
            required = new[] { "placa", "nivelConfianca", "coordenadas" }
        };

        var prompt = "Analise esta imagem de um veículo e identifique o texto da placa e sua localização exata na imagem. Retorne o número da placa, o nível de confiança da leitura (0.0 a 1.0) e as coordenadas normalizadas (0.0 a 1.0) da placa na imagem. As coordenadas devem representar um retângulo que contenha a placa completa com margem adequada. Se a placa não for visível ou detectável, retorne 'Placa não encontrada' com nível de confiança 0.0 e coordenadas com valores 0.0. Preencha o JSON estritamente conforme o esquema.";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inlineData = new
                            {
                                mimeType = mimeType,
                                data = imageBase64
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = plateDetectionSchema
            },
            systemInstruction = new
            {
                parts = new[]
                {
                    new { text = "Você é um modelo de OCR (Reconhecimento Óptico de Caracteres). Sua única tarefa é ler o texto da placa e retornar o JSON. NÃO adicione texto fora do JSON." }
                }
            }
        };

        return await FetchGeminiTextResultAsync(payload, _options.TextModel, cancellationToken);
    }

    public async Task<VehicleDetails> GetVehicleDetailsAsync(string imageBase64, string mimeType, string extractedPlate, CancellationToken cancellationToken = default)
    {
        var vehicleDetailsSchema = new
        {
            type = "OBJECT",
            properties = new
            {
                cor = new { type = "STRING", description = "Cor predominante do veículo, ex: 'Vermelho' ou 'Branco'." },
                tipo = new { type = "STRING", description = "Tipo de carroceria ou modelo, ex: 'Caminhão Baú', 'Hatchback', 'Sedan'." },
                fabricante = new { type = "STRING", description = "Fabricante do veículo, ex: 'Mercedes-Benz', 'Fiat', 'Volkswagen'." },
                placa_mercosul = new { type = "STRING", description = "Confirma se o formato da placa é 'Mercosul' (placa nova do Mercosul) ou 'Padrão Antigo' (placa antiga do Brasil). Se a placa não foi encontrada, use 'Não Identificada'." }
            },
            required = new[] { "cor", "tipo", "fabricante", "placa_mercosul" }
        };

        var prompt = $"Analise a imagem para determinar as características do veículo, como cor predominante, tipo de carroceria (ex: caminhão, carro, SUV) e fabricante. Com base na placa identificada \"{extractedPlate}\", determine se é uma placa nova do Mercosul ou uma placa antiga do Brasil. O campo placa_mercosul deve ser 'Mercosul' (se for placa nova do Mercosul) ou 'Padrão Antigo' (se for placa antiga do Brasil). Se o valor da placa for \"Placa não encontrada\", classifique o formato como 'Não Identificada' para o campo placa_mercosul, mas *continue a analisar as características visuais do veículo* (cor, tipo, fabricante) normalmente. Preencha o JSON estruturado seguindo o esquema.";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inlineData = new
                            {
                                mimeType = mimeType,
                                data = imageBase64
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = vehicleDetailsSchema
            },
            systemInstruction = new
            {
                parts = new[]
                {
                    new { text = "Você é um sistema de Visão Computacional de alta precisão especializado em detecção de veículos. Sua única tarefa é retornar um objeto JSON estritamente conforme o esquema fornecido, mesmo que a placa não tenha sido encontrada na primeira etapa de análise." }
                }
            }
        };

        var jsonString = await FetchGeminiTextResultAsync(payload, _options.TextModel, cancellationToken);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        return JsonSerializer.Deserialize<VehicleDetails>(jsonString, options) 
            ?? throw new InvalidOperationException("Resposta da IA não é um JSON válido para detalhes do veículo.");
    }

    public async Task<(string? base64, string? mimeType, string? errorMessage)> CropPlateImageAsync(string imageBase64, string mimeType, string plate, PlateCoordinates? coordinates = null, CancellationToken cancellationToken = default)
    {
        if (plate == "Placa não encontrada" || plate == "Erro de Processamento." || string.IsNullOrWhiteSpace(plate))
        {
            return (null, null, "Não foi possível recortar a imagem da placa (placa não encontrada no Passo 1).");
        }

        // Verifica se a chave está configurada como gratuita
        // Se sim, usa recorte local se coordenadas estiverem disponíveis
        if (_options.IsFreeTier)
        {
            if (coordinates != null && coordinates.Width > 0 && coordinates.Height > 0)
            {
                _logger.LogInformation("Usando recorte local (software) para API gratuita, baseado nas coordenadas retornadas pela detecção.");
                try
                {
                    var croppedBase64 = await CropAndEnhanceImageAsync(imageBase64, mimeType, coordinates);
                    if (croppedBase64 != null)
                    {
                        return (croppedBase64, mimeType, null);
                    }
                    else
                    {
                        _logger.LogWarning("Falha no recorte local, mas não retornando erro para não exibir mensagem na tela");
                        return (null, null, null); // Retorna null sem erro para não exibir mensagem
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Erro no recorte local, mas não retornando erro para não exibir mensagem na tela");
                    return (null, null, null); // Retorna null sem erro para não exibir mensagem
                }
            }
            else
            {
                _logger.LogWarning("Tentativa de recortar imagem com chave gratuita sem coordenadas disponíveis.");
                return (null, null, null); // Retorna null sem erro para não exibir mensagem na tela
            }
        }

        var systemPrompt = "Você é uma ferramenta de detecção e corte de placas veiculares. Sua tarefa é analisar a imagem, detectar **APENAS a placa veicular principal e mais proeminente** e gerar uma nova imagem contendo *somente* essa placa recortada. O corte deve ser o mais preciso e limpo possível. Retorne APENAS a imagem gerada, sem nenhum texto adicional.";
        var userQuery = "Recorte a placa veicular desta imagem. Priorize uma única placa.";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = userQuery },
                        new
                        {
                            inlineData = new
                            {
                                mimeType = mimeType,
                                data = imageBase64
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                responseModalities = new[] { "TEXT", "IMAGE" }
            },
            systemInstruction = new
            {
                parts = new[] { new { text = systemPrompt } }
            }
        };

        try
        {
            var (base64Data, returnedMimeType, errorMsg) = await FetchGeminiImageResultAsync(payload, cancellationToken);
            if (errorMsg != null)
            {
                return (null, null, errorMsg);
            }
            return (base64Data, returnedMimeType ?? mimeType, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao recortar imagem");
            var errorMessage = ex is HttpRequestException httpEx ? httpEx.Message : ex.ToString();
            return (null, null, errorMessage);
        }
    }

    private async Task<string> FetchGeminiTextResultAsync(object payload, string modelName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("Gemini API Key is not configured. Please set 'Gemini:ApiKey' in appsettings.json");
        }

        var apiUrl = $"{_options.BaseUrl}/{modelName}:generateContent?key={_options.ApiKey}";
        _logger.LogInformation("Chamando API Gemini Text: {ApiUrl} (key length: {KeyLength})", 
            apiUrl.Replace(_options.ApiKey, "***"), _options.ApiKey.Length);

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(apiUrl, content, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < MaxRetries - 1)
                {
                    throw new HttpRequestException("429 Throttling");
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorData = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Gemini API error (Text): Status {StatusCode}, Response: {ErrorData}", 
                        response.StatusCode, errorData);
                    
                    // Check for specific permission denied error
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || 
                        (errorData.Contains("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase) ||
                         errorData.Contains("unregistered callers", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new HttpRequestException($"Gemini API authentication failed. Please verify your API key is valid and has proper permissions. Error: {errorData}");
                    }
                    
                    throw new HttpRequestException($"Gemini API error: {errorData}");
                }

                var resultJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<JsonElement>(resultJson);

                if (result.TryGetProperty("candidates", out var candidates) &&
                    candidates.GetArrayLength() > 0 &&
                    candidates[0].TryGetProperty("content", out var contentObj) &&
                    contentObj.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0 &&
                    parts[0].TryGetProperty("text", out var text))
                {
                    return text.GetString()?.Trim() ?? throw new InvalidOperationException("Resposta da IA (Texto) incompleta ou vazia.");
                }

                throw new InvalidOperationException("Resposta da IA (Texto) incompleta ou vazia.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tentativa {Attempt} (Texto) falhou", attempt + 1);

                if (attempt < MaxRetries - 1)
                {
                    var delay = (int)(Math.Pow(2, attempt) * 1000 + (Random.Shared.NextDouble() * 500));
                    await Task.Delay(delay, cancellationToken);
                }
                else
                {
                    var errorMessage = ex is HttpRequestException httpEx ? httpEx.Message : ex.ToString();
                    throw new HttpRequestException(errorMessage, ex);
                }
            }
        }

        throw new InvalidOperationException("Erro desconhecido na comunicação com a API (Gemini Texto).");
    }

    private async Task<(string? base64, string? mimeType, string? errorMessage)> FetchGeminiImageResultAsync(object payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return (null, null, "Gemini API Key is not configured. Please set 'Gemini:ApiKey' in appsettings.json");
        }

        var apiUrl = $"{_options.BaseUrl}/{_options.ImageModel}:generateContent?key={_options.ApiKey}";

        _logger.LogInformation("Chamando API Gemini Image: modelo {Model}", _options.ImageModel);

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(apiUrl, content, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < MaxRetries - 1)
                {
                    var errorData = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("429 TooManyRequests (Imagem) - Quota excedida. Resposta: {ErrorData}", errorData);
                    
                    // Tenta extrair o retryDelay da resposta
                    int retryDelayMs = 5000; // Default: 5 segundos
                    try
                    {
                        var errorJson = JsonSerializer.Deserialize<JsonElement>(errorData);
                        if (errorJson.TryGetProperty("error", out var errorObj) &&
                            errorObj.TryGetProperty("details", out var details) &&
                            details.GetArrayLength() > 0)
                        {
                            foreach (var detail in details.EnumerateArray())
                            {
                                if (detail.TryGetProperty("@type", out var type) &&
                                    type.GetString() == "type.googleapis.com/google.rpc.RetryInfo" &&
                                    detail.TryGetProperty("retryDelay", out var retryDelay))
                                {
                                    // Parse retryDelay (formato: "4s" ou "4.815910382s")
                                    var delayStr = retryDelay.GetString();
                                    if (!string.IsNullOrEmpty(delayStr) && delayStr.EndsWith("s"))
                                    {
                                        var secondsStr = delayStr.Substring(0, delayStr.Length - 1);
                                        if (double.TryParse(secondsStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                                        {
                                            retryDelayMs = (int)(seconds * 1000) + 1000; // Adiciona 1 segundo extra para segurança
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Não foi possível extrair retryDelay da resposta, usando valor padrão");
                    }
                    
                    _logger.LogInformation("Aguardando {Delay}ms antes de tentar novamente (quota excedida)...", retryDelayMs);
                    await Task.Delay(retryDelayMs, cancellationToken);
                    continue; // Tenta novamente
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorData = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("HTTP error (Imagem)! status: {StatusCode}, resposta: {ErrorData}", response.StatusCode, errorData);
                    
                    // Check for specific permission denied error
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || 
                        (errorData.Contains("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase) ||
                         errorData.Contains("unregistered callers", StringComparison.OrdinalIgnoreCase)))
                    {
                        var authError = "Gemini API authentication failed. Please verify your API key is valid and has proper permissions.";
                        _logger.LogError("{AuthError} Error: {ErrorData}", authError, errorData);
                        return (null, null, $"{authError} Error: {errorData}");
                    }
                    
                    throw new HttpRequestException($"Gemini API error: {errorData}");
                }

                var resultJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<JsonElement>(resultJson);

                // Log da resposta para debug (primeiros 500 chars para não sobrecarregar)
                var responsePreview = resultJson.Length > 500 ? resultJson.Substring(0, 500) + "..." : resultJson;
                _logger.LogInformation("Resposta da API Gemini (Imagem) - Preview: {Response}", responsePreview);

                if (result.TryGetProperty("candidates", out var candidates) &&
                    candidates.GetArrayLength() > 0)
                {
                    var firstCandidate = candidates[0];
                    
                    if (firstCandidate.TryGetProperty("content", out var contentObj))
                    {
                        if (contentObj.TryGetProperty("parts", out var parts))
                        {
                            foreach (var part in parts.EnumerateArray())
                            {
                                // Verifica se tem inlineData com imagem
                                if (part.TryGetProperty("inlineData", out var inlineData))
                                {
                                    string? imageMimeType = null;
                                    
                                    // Verifica se tem mimeType
                                    if (inlineData.TryGetProperty("mimeType", out var mimeTypeElement))
                                    {
                                        imageMimeType = mimeTypeElement.GetString();
                                        _logger.LogInformation("MimeType da imagem encontrado: {MimeType}", imageMimeType);
                                    }
                                    else
                                    {
                                        _logger.LogWarning("inlineData não contém mimeType");
                                    }
                                    
                                    if (inlineData.TryGetProperty("data", out var base64Data))
                                    {
                                        var imageData = base64Data.GetString();
                                        if (!string.IsNullOrEmpty(imageData))
                                        {
                                            _logger.LogInformation("Imagem recortada recebida com sucesso (tamanho: {Length} chars, mimeType: {MimeType})", imageData.Length, imageMimeType ?? "não especificado");
                                            return (imageData, imageMimeType, null);
                                        }
                                        else
                                        {
                                            _logger.LogWarning("Campo 'data' em inlineData está vazio ou null");
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogWarning("inlineData não contém campo 'data'");
                                    }
                                }
                                
                                // Verifica se tem texto (pode vir junto com a imagem)
                                if (part.TryGetProperty("text", out var text))
                                {
                                    _logger.LogInformation("Texto na resposta (parte não é imagem): {Text}", text.GetString());
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Candidato não contém 'parts'");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Candidato não contém 'content'");
                    }
                }
                else
                {
                    _logger.LogWarning("Resposta não contém 'candidates' ou está vazia.");
                }

                // Log completo da resposta para diagnóstico (limitado a 2000 chars para não sobrecarregar)
                var fullResponse = resultJson.Length > 2000 ? resultJson.Substring(0, 2000) + "... (truncado)" : resultJson;
                _logger.LogError("Resposta da IA (Imagem) incompleta. Estrutura da resposta: {Response}", fullResponse);
                
                // Tenta extrair informações úteis da resposta
                if (result.TryGetProperty("error", out var errorResponse))
                {
                    var errorMessage = errorResponse.GetRawText();
                    _logger.LogError("Erro retornado pela API: {Error}", errorMessage);
                    return (null, null, errorMessage);
                }
                
                return (null, null, "Resposta da IA (Imagem) incompleta. A IA não retornou uma imagem recortada.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tentativa {Attempt} (Imagem) falhou: {ErrorType} - {Message}", attempt + 1, ex.GetType().Name, ex.Message);

                if (attempt < MaxRetries - 1)
                {
                    var delay = (int)(Math.Pow(2, attempt) * 1000 + (Random.Shared.NextDouble() * 500));
                    _logger.LogInformation("Aguardando {Delay}ms antes da próxima tentativa...", delay);
                    await Task.Delay(delay, cancellationToken);
                }
                else
                {
                    _logger.LogError("Todas as tentativas falharam. Último erro: {Error}", ex);
                    var errorMessage = ex is HttpRequestException httpEx ? httpEx.Message : ex.ToString();
                    return (null, null, errorMessage);
                }
            }
        }

        return (null, null, "Erro desconhecido na comunicação com a API (Gemini Imagem).");
    }

    private async Task<string?> CropAndEnhanceImageAsync(string imageBase64, string mimeType, PlateCoordinates coordinates)
    {
        try
        {
            // Decodifica a imagem base64
            var imageBytes = Convert.FromBase64String(imageBase64);
            
            using var ms = new MemoryStream(imageBytes);
            using var originalImage = Image.FromStream(ms);
            
            // Converte coordenadas normalizadas (0.0-1.0) para pixels
            // Padding inteligente: mais padding horizontal para placas, considerando proporção
            var paddingHorizontal = 0.15; // 15% de padding horizontal para melhor visualização
            var paddingVertical = 0.12; // 12% de padding vertical
            
            var x = (int)(coordinates.X * originalImage.Width);
            var y = (int)(coordinates.Y * originalImage.Height);
            var width = (int)(coordinates.Width * originalImage.Width);
            var height = (int)(coordinates.Height * originalImage.Height);
            
            // Aplica padding inteligente
            var paddingX = (int)(width * paddingHorizontal);
            var paddingY = (int)(height * paddingVertical);
            
            x = Math.Max(0, x - paddingX);
            y = Math.Max(0, y - paddingY);
            width = Math.Min(originalImage.Width - x, width + (paddingX * 2));
            height = Math.Min(originalImage.Height - y, height + (paddingY * 2));
            
            // Garante dimensões mínimas adequadas para placas
            var minWidth = 100;
            var minHeight = 40;
            if (width < minWidth) width = Math.Min(minWidth, originalImage.Width - x);
            if (height < minHeight) height = Math.Min(minHeight, originalImage.Height - y);
            
            // Cria o retângulo de recorte
            var cropRect = new Rectangle(x, y, width, height);
            
            // Configuração de zoom: aumenta o tamanho da imagem recortada para melhor visualização
            var zoomFactor = 2.5; // Zoom de 2.5x para melhor legibilidade
            var targetWidth = (int)(width * zoomFactor);
            var targetHeight = (int)(height * zoomFactor);
            
            // Garante que o zoom não ultrapassa limites razoáveis (máximo 1200px de largura)
            var maxWidth = 1200;
            if (targetWidth > maxWidth)
            {
                zoomFactor = (double)maxWidth / width;
                targetWidth = maxWidth;
                targetHeight = (int)(height * zoomFactor);
            }
            
            // Recorta e aplica zoom com alta qualidade
            Bitmap croppedBitmap;
            using (var tempBitmap = new Bitmap(targetWidth, targetHeight))
            {
                using (var g = Graphics.FromImage(tempBitmap))
                {
                    // Configurações de alta qualidade para melhor resultado
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    
                    // Desenha a imagem recortada com zoom
                    g.DrawImage(originalImage, new Rectangle(0, 0, targetWidth, targetHeight), cropRect, GraphicsUnit.Pixel);
                }
                
                // Aplica sharpening leve para melhorar a nitidez após o zoom
                croppedBitmap = ApplySharpening(tempBitmap);
            }
            
            // Converte para base64 com alta qualidade
            using var outputMs = new MemoryStream();
            using (croppedBitmap)
            {
                var imageFormat = mimeType.ToLowerInvariant() switch
                {
                    "image/png" => ImageFormat.Png,
                    "image/gif" => ImageFormat.Gif,
                    "image/webp" => ImageFormat.Webp,
                    _ => ImageFormat.Jpeg
                };
                
                // Configuração de qualidade para JPEG
                if (imageFormat == ImageFormat.Jpeg)
                {
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(ImagingEncoder.Quality, 95L); // Alta qualidade (95%)
                    var jpegCodec = ImageCodecInfo.GetImageEncoders()
                        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    if (jpegCodec != null)
                    {
                        croppedBitmap.Save(outputMs, jpegCodec, encoderParams);
                    }
                    else
                    {
                        croppedBitmap.Save(outputMs, imageFormat);
                    }
                }
                else
                {
                    croppedBitmap.Save(outputMs, imageFormat);
                }
            }
            
            var croppedBytes = outputMs.ToArray();
            
            _logger.LogInformation("Imagem recortada e ampliada: {OriginalWidth}x{OriginalHeight} -> {TargetWidth}x{TargetHeight} (zoom {ZoomFactor}x)", 
                width, height, targetWidth, targetHeight, zoomFactor);
            
            return Convert.ToBase64String(croppedBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao recortar e aplicar zoom na imagem");
            return null;
        }
    }
    
    private Bitmap ApplySharpening(Bitmap bitmap)
    {
        // Aplica um filtro de sharpening leve para melhorar a nitidez após o zoom
        // Usa LockBits com Marshal para melhor performance sem código unsafe
        try
        {
            var sharpened = new Bitmap(bitmap.Width, bitmap.Height);
            
            // Kernel de sharpening leve (unsharp mask)
            float[,] kernel = {
                { 0, -0.1f, 0 },
                { -0.1f, 1.4f, -0.1f },
                { 0, -0.1f, 0 }
            };
            
            // Usa LockBits para acesso direto à memória (muito mais rápido que GetPixel/SetPixel)
            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            
            var sharpenedData = sharpened.LockBits(
                new Rectangle(0, 0, sharpened.Width, sharpened.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            
            try
            {
                // Copia os dados da imagem original para array gerenciado
                int bytes = Math.Abs(bitmapData.Stride) * bitmap.Height;
                byte[] sourceRgbaValues = new byte[bytes];
                Marshal.Copy(bitmapData.Scan0, sourceRgbaValues, 0, bytes);
                
                // Cria array para a imagem resultante
                byte[] destRgbaValues = new byte[bytes];
                
                int stride = bitmapData.Stride;
                
                // Aplica o kernel de sharpening
                for (int y = 1; y < bitmap.Height - 1; y++)
                {
                    for (int x = 1; x < bitmap.Width - 1; x++)
                    {
                        float r = 0, g = 0, b = 0;
                        
                        for (int i = -1; i <= 1; i++)
                        {
                            for (int j = -1; j <= 1; j++)
                            {
                                int offsetX = x + i;
                                int offsetY = y + j;
                                int index = (offsetY * stride) + (offsetX * 4);
                                
                                float weight = kernel[i + 1, j + 1];
                                b += sourceRgbaValues[index] * weight;     // B
                                g += sourceRgbaValues[index + 1] * weight; // G
                                r += sourceRgbaValues[index + 2] * weight; // R
                            }
                        }
                        
                        r = Math.Max(0, Math.Min(255, r));
                        g = Math.Max(0, Math.Min(255, g));
                        b = Math.Max(0, Math.Min(255, b));
                        
                        int destIndex = (y * stride) + (x * 4);
                        destRgbaValues[destIndex] = (byte)b;     // B
                        destRgbaValues[destIndex + 1] = (byte)g; // G
                        destRgbaValues[destIndex + 2] = (byte)r; // R
                        destRgbaValues[destIndex + 3] = sourceRgbaValues[destIndex + 3]; // A (preserva alpha)
                    }
                }
                
                // Copia bordas sem processamento
                for (int y = 0; y < bitmap.Height; y++)
                {
                    if (y == 0 || y == bitmap.Height - 1)
                    {
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            int index = (y * stride) + (x * 4);
                            destRgbaValues[index] = sourceRgbaValues[index];
                            destRgbaValues[index + 1] = sourceRgbaValues[index + 1];
                            destRgbaValues[index + 2] = sourceRgbaValues[index + 2];
                            destRgbaValues[index + 3] = sourceRgbaValues[index + 3];
                        }
                    }
                    else
                    {
                        // Borda esquerda
                        int leftIndex = y * stride;
                        destRgbaValues[leftIndex] = sourceRgbaValues[leftIndex];
                        destRgbaValues[leftIndex + 1] = sourceRgbaValues[leftIndex + 1];
                        destRgbaValues[leftIndex + 2] = sourceRgbaValues[leftIndex + 2];
                        destRgbaValues[leftIndex + 3] = sourceRgbaValues[leftIndex + 3];
                        
                        // Borda direita
                        int rightIndex = (y * stride) + ((bitmap.Width - 1) * 4);
                        destRgbaValues[rightIndex] = sourceRgbaValues[rightIndex];
                        destRgbaValues[rightIndex + 1] = sourceRgbaValues[rightIndex + 1];
                        destRgbaValues[rightIndex + 2] = sourceRgbaValues[rightIndex + 2];
                        destRgbaValues[rightIndex + 3] = sourceRgbaValues[rightIndex + 3];
                    }
                }
                
                // Copia os dados processados de volta para a imagem
                Marshal.Copy(destRgbaValues, 0, sharpenedData.Scan0, bytes);
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
                sharpened.UnlockBits(sharpenedData);
            }
            
            return sharpened;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao aplicar sharpening, retornando imagem original: {Message}", ex.Message);
            // Se o sharpening falhar, retorna a imagem original
            return bitmap;
        }
    }

}

