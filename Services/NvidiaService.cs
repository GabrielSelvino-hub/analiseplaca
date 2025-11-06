using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlateAnalysisApi.Configuration;
using PlateAnalysisApi.Models;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using ImagingEncoder = System.Drawing.Imaging.Encoder;

namespace PlateAnalysisApi.Services;

public class NvidiaService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly NvidiaOptions _options;
    private readonly ILogger<NvidiaService> _logger;
    private const int MaxRetries = 3;

    public NvidiaService(HttpClient httpClient, IOptions<NvidiaOptions> options, ILogger<NvidiaService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        
        // Configura o header de autorização para todas as requisições
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.ApiKey}");
        }
        else
        {
            _logger.LogWarning("API Key da NVIDIA não configurada. Configure a chave no appsettings.json");
        }
        
        // Configura headers adicionais
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<string> GetPlateTextAsync(string imageBase64, string mimeType, CancellationToken cancellationToken = default)
    {
        var systemPrompt = "Você é um modelo de OCR (Reconhecimento Óptico de Caracteres) especializado em placas veiculares brasileiras. Sua única tarefa é ler o texto da placa e retornar APENAS um JSON válido com o seguinte formato: {\"placa\": \"ABC1234\", \"nivelConfianca\": 0.95}. O campo nivelConfianca deve ser um número entre 0.0 e 1.0 indicando a confiança da leitura. Se a placa não for visível ou detectável, retorne {\"placa\": \"Placa não encontrada\", \"nivelConfianca\": 0.0}. NÃO adicione texto fora do JSON.";

        var userPrompt = "Analise esta imagem de um veículo e identifique o texto da placa. Retorne um JSON válido com o formato: {\"placa\": \"ABC1234\", \"nivelConfianca\": 0.95} onde nivelConfianca é um número entre 0.0 e 1.0. Se não conseguir identificar, retorne {\"placa\": \"Placa não encontrada\", \"nivelConfianca\": 0.0}.";

        var payload = new
        {
            model = _options.VisionModel,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = (object)systemPrompt
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = userPrompt },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:{mimeType};base64,{imageBase64}"
                            }
                        }
                    }
                }
            },
            temperature = 0.1,
            max_tokens = 200,
            top_p = 0.9
        };

        return await FetchNvidiaTextResultAsync(payload, cancellationToken);
    }

    public async Task<VehicleDetails> GetVehicleDetailsAsync(string imageBase64, string mimeType, string extractedPlate, CancellationToken cancellationToken = default)
    {
        var systemPrompt = "Você é um sistema de Visão Computacional de alta precisão especializado em detecção de veículos. Sua única tarefa é retornar um objeto JSON estritamente conforme o esquema fornecido, mesmo que a placa não tenha sido encontrada na primeira etapa de análise.";

        var userPrompt = $"Analise a imagem para determinar as características do veículo, como cor predominante, tipo de carroceria (ex: caminhão, carro, SUV) e fabricante. Com base na placa identificada \"{extractedPlate}\", determine se é uma placa nova do Mercosul ou uma placa antiga do Brasil. O campo placa_mercosul deve ser 'Mercosul' (se for placa nova do Mercosul) ou 'Padrão Antigo' (se for placa antiga do Brasil). Se o valor da placa for \"Placa não encontrada\", classifique o formato como 'Não Identificada' para o campo placa_mercosul, mas *continue a analisar as características visuais do veículo* (cor, tipo, fabricante) normalmente. Retorne um JSON válido com os seguintes campos: cor, tipo, fabricante, placa_mercosul. O campo placa_mercosul deve ser 'Mercosul' ou 'Padrão Antigo' ou 'Não Identificada'.";

        var payload = new
        {
            model = _options.VisionModel,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = (object)systemPrompt
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = userPrompt },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:{mimeType};base64,{imageBase64}"
                            }
                        }
                    }
                }
            },
            temperature = 0.2,
            max_tokens = 500,
            top_p = 0.9
        };

        var jsonString = await FetchNvidiaTextResultAsync(payload, cancellationToken);
        
        // Limpa a resposta removendo markdown code blocks se existirem
        jsonString = jsonString.Trim();
        if (jsonString.StartsWith("```json"))
        {
            jsonString = jsonString.Substring(7);
        }
        if (jsonString.StartsWith("```"))
        {
            jsonString = jsonString.Substring(3);
        }
        if (jsonString.EndsWith("```"))
        {
            jsonString = jsonString.Substring(0, jsonString.Length - 3);
        }
        jsonString = jsonString.Trim();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        
        try
        {
            return JsonSerializer.Deserialize<VehicleDetails>(jsonString, options) 
                ?? throw new InvalidOperationException("Resposta da IA não é um JSON válido para detalhes do veículo.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Erro ao deserializar resposta da NVIDIA. JSON recebido: {Json}", jsonString);
            throw new InvalidOperationException($"Resposta da IA não é um JSON válido: {ex.Message}", ex);
        }
    }

    public async Task<(string? base64, string? mimeType, string? errorMessage)> CropPlateImageAsync(string imageBase64, string mimeType, string plate, PlateCoordinates? coordinates = null, CancellationToken cancellationToken = default)
    {
        if (plate == "Placa não encontrada" || plate == "Erro de Processamento." || string.IsNullOrWhiteSpace(plate))
        {
            return (null, null, "Não foi possível recortar a imagem da placa (placa não encontrada no Passo 1).");
        }

        try
        {
            _logger.LogInformation("Iniciando processo de criação de imagem em zoom da placa via API NVIDIA...");
            
            // Passo 1: Usar coordenadas passadas se disponíveis, caso contrário obter via API
            PlateCoordinates? plateCoordinates = coordinates;
            if (plateCoordinates == null)
            {
                _logger.LogInformation("Coordenadas não fornecidas, obtendo via API NVIDIA...");
                plateCoordinates = await GetPlateCoordinatesAsync(imageBase64, mimeType, plate, cancellationToken);
            }
            else
            {
                _logger.LogInformation("Usando coordenadas fornecidas: x={X}, y={Y}, width={Width}, height={Height}", 
                    plateCoordinates.X, plateCoordinates.Y, plateCoordinates.Width, plateCoordinates.Height);
            }
            
            if (plateCoordinates == null)
            {
                _logger.LogWarning("Não foi possível obter coordenadas da placa via API NVIDIA. Tentando recorte alternativo...");
                // Fallback: tenta recortar uma região central inferior da imagem (onde geralmente ficam as placas)
                return await CropPlateImageFallbackAsync(imageBase64, mimeType);
            }

            // Passo 2: Recortar e aplicar zoom/enhancement na imagem usando as coordenadas
            _logger.LogInformation("Recortando e aplicando zoom na imagem usando coordenadas: x={X}, y={Y}, width={Width}, height={Height}", 
                plateCoordinates.X, plateCoordinates.Y, plateCoordinates.Width, plateCoordinates.Height);
            
            var croppedImageBase64 = await CropAndEnhanceImageAsync(imageBase64, mimeType, plateCoordinates);
            
            if (string.IsNullOrEmpty(croppedImageBase64))
            {
                return (null, null, "Erro ao processar o recorte e zoom da imagem.");
            }

            _logger.LogInformation("Imagem da placa com zoom criada com sucesso usando API NVIDIA");
            return (croppedImageBase64, mimeType, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao criar imagem em zoom da placa via NVIDIA");
            return (null, null, $"Erro ao criar imagem em zoom: {ex.Message}");
        }
    }

    private async Task<PlateCoordinates?> GetPlateCoordinatesAsync(string imageBase64, string mimeType, string plate, CancellationToken cancellationToken)
    {
        var systemPrompt = "Você é um sistema de detecção de objetos especializado em localizar placas veiculares brasileiras em imagens com alta precisão. Sua tarefa é analisar a imagem cuidadosamente, identificar a região exata da placa veicular e retornar APENAS um JSON válido com as coordenadas normalizadas da placa. Seja preciso na localização - inclua apenas a região da placa, mas com margem suficiente para capturar toda a área visível da placa. Use coordenadas relativas (0.0 a 1.0) normalizadas pela dimensão da imagem.";

        var userPrompt = $"Analise esta imagem cuidadosamente e localize a placa veicular brasileira com o texto \"{plate}\". A placa pode estar em formato Mercosul ou padrão antigo. Retorne APENAS um JSON válido com as coordenadas normalizadas (0.0 a 1.0) da região completa da placa, incluindo bordas e espaçamento suficiente para capturar todos os caracteres e elementos visuais. O formato deve ser EXATAMENTE: {{\"x\": 0.0-1.0, \"y\": 0.0-1.0, \"width\": 0.0-1.0, \"height\": 0.0-1.0}}. As coordenadas devem representar um retângulo que contenha a placa completa com margem adequada. Se não conseguir localizar a placa, retorne {{\"x\": 0.0, \"y\": 0.0, \"width\": 0.0, \"height\": 0.0}}. NÃO adicione texto, explicações ou formatação markdown fora do JSON.";

        var payload = new
        {
            model = _options.VisionModel,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = (object)systemPrompt
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = userPrompt },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:{mimeType};base64,{imageBase64}"
                            }
                        }
                    }
                }
            },
            temperature = 0.0, // Temperatura mais baixa para maior precisão
            max_tokens = 200,
            top_p = 0.8
        };

        try
        {
            var jsonString = await FetchNvidiaTextResultAsync(payload, cancellationToken);
            
            // Limpa a resposta removendo markdown code blocks se existirem
            jsonString = jsonString.Trim();
            if (jsonString.StartsWith("```json"))
            {
                jsonString = jsonString.Substring(7);
            }
            if (jsonString.StartsWith("```"))
            {
                jsonString = jsonString.Substring(3);
            }
            if (jsonString.EndsWith("```"))
            {
                jsonString = jsonString.Substring(0, jsonString.Length - 3);
            }
            jsonString = jsonString.Trim();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var coordinates = JsonSerializer.Deserialize<PlateCoordinates>(jsonString, options);
            
            // Valida se as coordenadas são válidas
            if (coordinates != null && coordinates.Width > 0 && coordinates.Height > 0)
            {
                return coordinates;
            }

            _logger.LogWarning("Coordenadas inválidas recebidas da API NVIDIA: {Json}", jsonString);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao obter coordenadas da placa via API NVIDIA");
            return null;
        }
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
                    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L); // Alta qualidade (95%)
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

    private async Task<(string? base64, string? mimeType, string? errorMessage)> CropPlateImageFallbackAsync(string imageBase64, string mimeType)
    {
        try
        {
            _logger.LogInformation("Usando método fallback para recortar região inferior da imagem (onde geralmente ficam as placas)");
            
            var imageBytes = Convert.FromBase64String(imageBase64);
            
            using var ms = new MemoryStream(imageBytes);
            using var originalImage = Image.FromStream(ms);
            
            // Recorta a região inferior central (onde geralmente ficam as placas)
            var cropWidth = (int)(originalImage.Width * 0.6); // 60% da largura
            var cropHeight = (int)(originalImage.Height * 0.25); // 25% da altura
            var x = (originalImage.Width - cropWidth) / 2; // Centralizado
            var y = (int)(originalImage.Height * 0.7); // 70% da altura (inferior)
            
            // Garante que não ultrapassa os limites
            x = Math.Max(0, x);
            y = Math.Max(0, y);
            cropWidth = Math.Min(cropWidth, originalImage.Width - x);
            cropHeight = Math.Min(cropHeight, originalImage.Height - y);
            
            var cropRect = new Rectangle(x, y, cropWidth, cropHeight);
            
            using var croppedBitmap = new Bitmap(cropWidth, cropHeight);
            using (var g = Graphics.FromImage(croppedBitmap))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(originalImage, 0, 0, cropRect, GraphicsUnit.Pixel);
            }
            
            using var outputMs = new MemoryStream();
            var imageFormat = mimeType.ToLowerInvariant() switch
            {
                "image/png" => ImageFormat.Png,
                "image/gif" => ImageFormat.Gif,
                "image/webp" => ImageFormat.Webp,
                _ => ImageFormat.Jpeg
            };
            
            croppedBitmap.Save(outputMs, imageFormat);
            var croppedBytes = outputMs.ToArray();
            var croppedBase64 = Convert.ToBase64String(croppedBytes);
            
            return (croppedBase64, mimeType, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro no método fallback de recorte");
            return (null, null, $"Erro no recorte fallback: {ex.Message}");
        }
    }

    private async Task<string> FetchNvidiaTextResultAsync(object payload, CancellationToken cancellationToken)
    {
        var apiUrl = $"{_options.BaseUrl}/chat/completions";

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                // NVIDIA API requires exactly "application/json" without charset parameter
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                _logger.LogInformation("Chamando API NVIDIA: {Url} (tentativa {Attempt}/{MaxRetries})", apiUrl, attempt + 1, MaxRetries);

                var response = await _httpClient.PostAsync(apiUrl, content, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < MaxRetries - 1)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta?.TotalMilliseconds ?? Math.Pow(2, attempt) * 1000;
                    _logger.LogWarning("Rate limit atingido. Aguardando {Delay}ms antes de tentar novamente...", retryAfter);
                    await Task.Delay((int)retryAfter, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorData = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Erro HTTP da API NVIDIA: {StatusCode} - {ErrorData}", response.StatusCode, errorData);
                    throw new HttpRequestException(errorData);
                }

                var resultJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<JsonElement>(resultJson);

                if (result.TryGetProperty("choices", out var choices) &&
                    choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var contentObj))
                {
                    var text = contentObj.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _logger.LogInformation("Resposta recebida com sucesso da API NVIDIA");
                        return text;
                    }
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

        throw new InvalidOperationException("Erro desconhecido na comunicação com a API NVIDIA (Texto).");
    }
}

