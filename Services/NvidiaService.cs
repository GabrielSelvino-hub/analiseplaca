using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlateAnalysisApi.Configuration;
using PlateAnalysisApi.Models;

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
        var systemPrompt = "Você é um modelo de OCR (Reconhecimento Óptico de Caracteres) especializado em placas veiculares brasileiras. Sua única tarefa é ler o texto da placa e retornar APENAS um JSON válido com o seguinte formato: {\"placa\": \"ABC1234\"}. Se a placa não for visível ou detectável, retorne {\"placa\": \"Placa não encontrada\"}. NÃO adicione texto fora do JSON.";

        var userPrompt = "Analise esta imagem de um veículo e identifique o texto da placa. Retorne apenas um JSON válido com o formato: {\"placa\": \"ABC1234\"} ou {\"placa\": \"Placa não encontrada\"} se não conseguir identificar.";

        var payload = new
        {
            model = _options.VisionModel,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = (object)systemPrompt
                },
                new
                {
                    role = "user",
                    content = (object)new object[]
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

        var userPrompt = $"Analise a imagem para determinar as características do veículo, como cor predominante, tipo de carroceria (ex: caminhão, carro, SUV), marca e fabricante. Use o valor fornecido para a placa, \"{extractedPlate}\", para preencher o campo placa_brasil. Se o valor da placa for \"Placa não encontrada\", use-o no campo placa_brasil e classifique o formato como 'Não Identificada' para o campo placa_mercosul, mas *continue a analisar as características visuais do veículo* (cor, tipo, marca) normalmente. Retorne um JSON válido com os seguintes campos: cor, tipo, marca, fabricante, placa_brasil, placa_mercosul. O campo placa_mercosul deve ser 'Mercosul' ou 'Padrão Antigo' ou 'Não Identificada'.";

        var payload = new
        {
            model = _options.VisionModel,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = (object)systemPrompt
                },
                new
                {
                    role = "user",
                    content = (object)new object[]
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

    public async Task<(string? base64, string? mimeType, string? errorMessage)> CropPlateImageAsync(string imageBase64, string mimeType, string plate, CancellationToken cancellationToken = default)
    {
        if (plate == "Placa não encontrada" || plate == "Erro de Processamento." || string.IsNullOrWhiteSpace(plate))
        {
            return (null, null, "Não foi possível recortar a imagem da placa (placa não encontrada no Passo 1).");
        }

        // A API NVIDIA NIM não suporta geração de imagens diretamente nos modelos de visão gratuitos
        // Esta funcionalidade requer modelos especializados que não estão disponíveis na versão gratuita
        _logger.LogWarning("Recorte de imagem não está disponível na API NVIDIA gratuita.");
        return (null, null, "API Gratuita da NVIDIA não suporta recorte de imagem. Esta funcionalidade requer modelos especializados de geração de imagem que não estão disponíveis na versão gratuita.");
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

