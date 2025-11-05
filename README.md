# API .NET 8 - Análise de Placas Veiculares

API REST desenvolvida em .NET 8 para análise automática de placas veiculares usando inteligência artificial da NVIDIA. A API realiza OCR (reconhecimento óptico de caracteres) para extrair o número da placa, analisa características do veículo (cor, tipo, marca, fabricante) e identifica o formato da placa (Brasil ou Mercosul).

## 📋 Índice

- [Funcionalidades](#funcionalidades)
- [Requisitos](#requisitos)
- [Instalação](#instalação)
- [Configuração](#configuração)
- [Executando a API](#executando-a-api)
- [Endpoints](#endpoints)
- [Estrutura de Dados](#estrutura-de-dados)
- [Exemplos de Uso](#exemplos-de-uso)
- [Fluxo de Processamento](#fluxo-de-processamento)
- [Tratamento de Erros](#tratamento-de-erros)
- [Cache de Duplicatas](#cache-de-duplicatas)
- [Limitações](#limitações)
- [Troubleshooting](#troubleshooting)

## 🚀 Funcionalidades

- **OCR de Placas**: Extração automática do número da placa veicular usando NVIDIA NIM API
- **Análise de Veículos**: Identificação de cor, tipo, marca e fabricante do veículo
- **Detecção de Formato**: Identificação se a placa é do formato brasileiro tradicional ou Mercosul
- **Prevenção de Duplicatas**: Sistema de cache em memória para evitar processamento duplicado
- **Validação de Entrada**: Validação automática de formato base64 e tipos MIME
- **Retry Automático**: Sistema de retry com backoff exponencial para falhas temporárias
- **Health Check**: Endpoint para verificação do status da API

## 📦 Requisitos

- **.NET 8 SDK** ou superior
- **API Key da NVIDIA** (obtenha em [NVIDIA AI Foundation Models](https://build.nvidia.com/))
- **Windows, Linux ou macOS**

## 🔧 Instalação

1. Clone o repositório ou navegue até o diretório do projeto:

```bash
cd PlateAnalysisApi
```

2. Restaure as dependências (se necessário):

```bash
dotnet restore
```

## ⚙️ Configuração

A API suporta dois provedores de IA: **NVIDIA** e **Google Gemini**. Você pode escolher qual usar através da configuração no `appsettings.json`.

### 1. Escolha do Provedor

Edite o arquivo `appsettings.json` e configure o provedor desejado:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "AiProvider": {
    "Provider": "Nvidia"
  },
  "Nvidia": {
    "ApiKey": "SUA_API_KEY_NVIDIA",
    "TextModel": "meta/llama-3.1-8b-instruct",
    "VisionModel": "meta/llama-3.1-70b-instruct",
    "BaseUrl": "https://integrate.api.nvidia.com/v1"
  },
  "Gemini": {
    "ApiKey": "SUA_API_KEY_GEMINI",
    "TextModel": "gemini-2.5-flash-preview-09-2025",
    "ImageModel": "gemini-2.5-flash-image-preview",
    "BaseUrl": "https://generativelanguage.googleapis.com/v1beta/models"
  }
}
```

**Valores aceitos para `AiProvider.Provider`:**
- `"Nvidia"` - Usa a API NVIDIA (padrão)
- `"Gemini"` - Usa a API Google Gemini

### 2. Configuração da API NVIDIA

#### Como obter a API Key da NVIDIA

1. Acesse [NVIDIA AI Foundation Models](https://build.nvidia.com/)
2. Crie uma conta ou faça login
3. Navegue até a seção de API Keys
4. Gere uma nova API Key
5. Copie a chave e cole no campo `Nvidia.ApiKey` do `appsettings.json`

### 3. Configuração da API Google Gemini

#### Como obter a API Key do Google Gemini

1. Acesse [Google AI Studio](https://makersuite.google.com/app/apikey)
2. Crie uma conta ou faça login
3. Gere uma nova API Key
4. Copie a chave e cole no campo `Gemini.ApiKey` do `appsettings.json`

**Nota:** Você precisa configurar apenas a API Key do provedor que deseja usar. O outro pode ficar vazio, mas é recomendado configurar ambos para facilitar a troca entre provedores.

### Configuração Avançada

Você também pode configurar via variáveis de ambiente:

```bash
# Windows PowerShell
# Escolher provedor
$env:AiProvider__Provider="Nvidia"  # ou "Gemini"

# Configurar API Keys
$env:Nvidia__ApiKey="SUA_API_KEY_NVIDIA"
$env:Gemini__ApiKey="SUA_API_KEY_GEMINI"

# Linux/macOS
export AiProvider__Provider="Nvidia"  # ou "Gemini"
export Nvidia__ApiKey="SUA_API_KEY_NVIDIA"
export Gemini__ApiKey="SUA_API_KEY_GEMINI"
```

Ou criar um arquivo `appsettings.Development.json` para configurações de desenvolvimento:

```json
{
  "AiProvider": {
    "Provider": "Nvidia"
  },
  "Nvidia": {
    "ApiKey": "SUA_API_KEY_DEVELOPMENT"
  },
  "Gemini": {
    "ApiKey": "SUA_API_KEY_DEVELOPMENT"
  }
}
```

### Comparação entre Provedores

| Recurso | NVIDIA | Gemini |
|---------|--------|--------|
| **OCR de Placas** | ✅ | ✅ |
| **Análise de Veículos** | ✅ | ✅ |
| **Recorte de Imagem** | ❌ (gratuito) | ⚠️ (requer plano pago) |
| **API Key Gratuita** | ✅ | ✅ |
| **Modelos** | Llama (Meta) | Gemini Flash |
| **Rate Limits** | Conforme política NVIDIA | Conforme plano Google |

**Recomendação:** Use **NVIDIA** para uso gratuito completo, ou **Gemini** se você já tiver um plano pago e precisar de recorte de imagem.

## 🏃 Executando a API

### Modo Desenvolvimento

```bash
dotnet run
```

A API estará disponível em:
- **HTTP**: `http://localhost:5000`
- **HTTPS**: `https://localhost:5001`

### Modo Produção

```bash
dotnet build --configuration Release
dotnet run --configuration Release
```

### Executar em Porta Específica

```bash
dotnet run --urls "http://localhost:8080"
```

## 📡 Endpoints

### POST /api/analyze-plate

Analisa uma imagem de veículo e retorna informações sobre a placa e detalhes do veículo.

#### Request Body

```json
{
  "imageBase64": "iVBORw0KGgoAAAANS...",
  "mimeType": "image/jpeg"
}
```

**Parâmetros:**
- `imageBase64` (string, obrigatório): Imagem codificada em base64 (com ou sem prefixo `data:image/...;base64,`)
- `mimeType` (string, opcional, padrão: `"image/jpeg"`): Tipo MIME da imagem. Valores aceitos:
  - `image/jpeg` ou `image/jpg`
  - `image/png`
  - `image/gif`
  - `image/webp`

#### Response (200 OK - Sucesso)

```json
{
  "placa": "ABC1234",
  "duplicada": false,
  "detalhesVeiculo": {
    "cor": "Branco",
    "tipo": "Caminhão Baú",
    "marca": "Mercedes-Benz",
    "fabricante": "Mercedes-Benz",
    "placa_brasil": "ABC1234",
    "placa_mercosul": "Padrão Antigo"
  },
  "imagemPlacaRecortada": {
    "base64": null,
    "mimeType": null,
    "mensagem": "API Gratuita da NVIDIA não suporta recorte de imagem. Esta funcionalidade requer modelos especializados de geração de imagem que não estão disponíveis na versão gratuita."
  },
  "erro": null
}
```

#### Response (200 OK - Placa Duplicada)

```json
{
  "placa": "ABC1234",
  "duplicada": true,
  "detalhesVeiculo": null,
  "imagemPlacaRecortada": null,
  "erro": "Atenção: A placa \"ABC1234\" já foi processada nesta sessão. O processamento foi interrompido."
}
```

#### Response (200 OK - Placa Não Encontrada)

```json
{
  "placa": "Placa não encontrada",
  "duplicada": false,
  "detalhesVeiculo": {
    "cor": "Branco",
    "tipo": "Caminhão",
    "marca": "Mercedes-Benz",
    "fabricante": "Mercedes-Benz",
    "placa_brasil": "Placa não encontrada",
    "placa_mercosul": "Não Identificada"
  },
  "imagemPlacaRecortada": null,
  "erro": null
}
```

#### Response (400 Bad Request)

```json
{
  "erro": "A imagem em base64 é obrigatória."
}
```

ou

```json
{
  "erro": "O formato da imagem em base64 é inválido."
}
```

ou

```json
{
  "erro": "O tipo MIME 'image/bmp' não é suportado. Use: image/jpeg, image/png, image/gif ou image/webp."
}
```

### GET /health

Health check da API. Retorna o status da aplicação.

#### Response (200 OK)

```json
{
  "status": "healthy",
  "timestamp": "2024-01-01T12:00:00.000Z"
}
```

## 📊 Estrutura de Dados

### PlateAnalysisRequest

```typescript
{
  imageBase64: string;    // Imagem em base64 (obrigatório)
  mimeType: string;       // Tipo MIME (opcional, padrão: "image/jpeg")
}
```

### PlateAnalysisResponse

```typescript
{
  placa: string;                          // Número da placa ou "Placa não encontrada"
  duplicada: boolean;                     // Indica se a placa já foi processada
  detalhesVeiculo: VehicleDetails | null; // Detalhes do veículo (null se duplicada)
  imagemPlacaRecortada: CroppedPlateImage | null; // Imagem recortada (null na versão gratuita)
  erro: string | null;                    // Mensagem de erro (null se sucesso)
}
```

### VehicleDetails

```typescript
{
  cor: string;             // Cor predominante do veículo
  tipo: string;            // Tipo de carroceria (ex: "Caminhão Baú", "Sedan")
  marca: string;           // Marca comercial do veículo
  fabricante: string;      // Fabricante do veículo
  placa_brasil: string;    // Placa no formato brasileiro
  placa_mercosul: string;  // "Mercosul" ou "Padrão Antigo"
}
```

### CroppedPlateImage

```typescript
{
  base64: string | null;      // Imagem recortada em base64 (null na versão gratuita)
  mimeType: string | null;    // Tipo MIME da imagem (null na versão gratuita)
  mensagem: string | null;     // Mensagem informativa sobre limitações
}
```

## 💻 Exemplos de Uso

### cURL

```bash
curl -X POST http://localhost:5000/api/analyze-plate \
  -H "Content-Type: application/json" \
  -d '{
    "imageBase64": "iVBORw0KGgoAAAANS...",
    "mimeType": "image/jpeg"
  }'
```

### JavaScript/TypeScript (Fetch API)

```javascript
async function analyzePlate(imageBase64, mimeType = 'image/jpeg') {
  const response = await fetch('http://localhost:5000/api/analyze-plate', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      imageBase64: imageBase64,
      mimeType: mimeType
    })
  });

  if (!response.ok) {
    throw new Error(`HTTP error! status: ${response.status}`);
  }

  const data = await response.json();
  return data;
}

// Exemplo de uso
const imageFile = document.querySelector('input[type="file"]').files[0];
const reader = new FileReader();

reader.onload = async function(e) {
  const base64 = e.target.result.split(',')[1]; // Remove prefixo data URL
  const mimeType = imageFile.type;
  
  try {
    const result = await analyzePlate(base64, mimeType);
    console.log('Placa:', result.placa);
    console.log('Detalhes:', result.detalhesVeiculo);
  } catch (error) {
    console.error('Erro:', error);
  }
};

reader.readAsDataURL(imageFile);
```

### Python (requests)

```python
import requests
import base64

def analyze_plate(image_path):
    # Lê e codifica a imagem em base64
    with open(image_path, 'rb') as image_file:
        image_base64 = base64.b64encode(image_file.read()).decode('utf-8')
    
    # Determina o MIME type
    mime_type = 'image/jpeg'
    if image_path.lower().endswith('.png'):
        mime_type = 'image/png'
    elif image_path.lower().endswith('.gif'):
        mime_type = 'image/gif'
    elif image_path.lower().endswith('.webp'):
        mime_type = 'image/webp'
    
    # Faz a requisição
    response = requests.post(
        'http://localhost:5000/api/analyze-plate',
        json={
            'imageBase64': image_base64,
            'mimeType': mime_type
        }
    )
    
    response.raise_for_status()
    return response.json()

# Exemplo de uso
result = analyze_plate('veiculo.jpg')
print(f"Placa: {result['placa']}")
print(f"Cor: {result['detalhesVeiculo']['cor']}")
print(f"Tipo: {result['detalhesVeiculo']['tipo']}")
```

### C# (.NET)

```csharp
using System.Text;
using System.Text.Json;

var httpClient = new HttpClient();
var request = new
{
    imageBase64 = Convert.ToBase64String(File.ReadAllBytes("veiculo.jpg")),
    mimeType = "image/jpeg"
};

var json = JsonSerializer.Serialize(request);
var content = new StringContent(json, Encoding.UTF8, "application/json");

var response = await httpClient.PostAsync(
    "http://localhost:5000/api/analyze-plate",
    content
);

var responseJson = await response.Content.ReadAsStringAsync();
var result = JsonSerializer.Deserialize<PlateAnalysisResponse>(responseJson);

Console.WriteLine($"Placa: {result.Placa}");
Console.WriteLine($"Cor: {result.DetalhesVeiculo?.Cor}");
```

### PowerShell

```powershell
# Converte imagem para base64
$imageBytes = [System.IO.File]::ReadAllBytes("veiculo.jpg")
$imageBase64 = [Convert]::ToBase64String($imageBytes)

# Monta o body
$body = @{
    imageBase64 = $imageBase64
    mimeType = "image/jpeg"
} | ConvertTo-Json

# Faz a requisição
$response = Invoke-RestMethod -Uri "http://localhost:5000/api/analyze-plate" `
    -Method Post `
    -ContentType "application/json" `
    -Body $body

Write-Host "Placa: $($response.placa)"
Write-Host "Cor: $($response.detalhesVeiculo.cor)"
```

## 🔄 Fluxo de Processamento

A API processa as imagens em 3 etapas principais:

1. **Extração da Placa (OCR)**
   - Usa o modelo `meta/llama-3.1-70b-instruct` da NVIDIA para análise de visão computacional
   - Extrai o número da placa da imagem
   - Retorna "Placa não encontrada" se não conseguir identificar

2. **Verificação de Duplicatas**
   - Verifica se a placa já foi processada no cache
   - Se duplicada, retorna imediatamente sem processar os próximos passos
   - Se não duplicada, adiciona ao cache para futuras verificações

3. **Análise de Detalhes do Veículo**
   - Usa o modelo de visão da NVIDIA para análise visual
   - Identifica: cor, tipo, marca e fabricante
   - Classifica o formato da placa (Brasil ou Mercosul)
   - Continua mesmo se a placa não foi encontrada na etapa 1

4. **Recorte de Imagem** *(Não disponível na versão gratuita)*
   - Funcionalidade não suportada pela API NVIDIA gratuita
   - Retorna mensagem informativa sobre a limitação
   - Requer modelos especializados de geração de imagem que não estão disponíveis gratuitamente

## ⚠️ Tratamento de Erros

### Validação de Entrada

- **Base64 inválido**: Retorna `400 Bad Request` com mensagem de erro
- **MIME type não suportado**: Retorna `400 Bad Request` com lista de tipos aceitos
- **Imagem ausente**: Retorna `400 Bad Request`

### Erros da API NVIDIA

- **Retry automático**: 3 tentativas com backoff exponencial (1s, 2s, 4s)
- **Rate limiting**: Aguarda automaticamente conforme headers `Retry-After` da resposta
- **Erro de autenticação**: Retorna mensagem clara sobre API Key inválida
- **Erro de rede**: Retenta automaticamente com delay crescente

### Códigos de Status HTTP

- `200 OK`: Processamento concluído (sucesso ou erro no campo `erro`)
- `400 Bad Request`: Erro de validação de entrada
- `500 Internal Server Error`: Erro interno do servidor

## 🗄️ Cache de Duplicatas

O sistema mantém um cache em memória para evitar processamento duplicado:

- **Armazenamento**: `ConcurrentDictionary` thread-safe
- **Expiração**: Entradas expiram automaticamente após 24 horas
- **Limpeza**: Limpeza automática a cada 1 hora
- **Validação**: Apenas placas válidas são armazenadas (ignora "Placa não encontrada" e "Erro de Processamento.")

### Comportamento

- Placas duplicadas dentro do período de 24 horas são detectadas imediatamente
- O processamento é interrompido antes das etapas de análise de detalhes
- A resposta indica que a placa é duplicada e inclui mensagem explicativa

## 🚫 Limitações

### API Gratuita da NVIDIA

- **Recorte de Imagem**: Não suportado na versão gratuita (requer modelos especializados)
- **Rate Limiting**: Limites de requisições conforme política da NVIDIA
- **Modelos**: Usa modelos de visão computacional disponíveis na API gratuita
- **Requisitos**: Requer API Key válida obtida em [NVIDIA AI Foundation Models](https://build.nvidia.com/)

### Processamento

- **Cache em memória**: Perdido quando a aplicação é reiniciada
- **Thread-safe**: Cache seguro para uso simultâneo
- **Imagens grandes**: Recomenda-se imagens otimizadas para melhor performance

### Tipos de Imagem Suportados

- ✅ JPEG/JPG
- ✅ PNG
- ✅ GIF
- ✅ WebP
- ❌ BMP, TIFF, SVG (não suportados)

## 🔍 Troubleshooting

### Erro: "API Key da NVIDIA inválida ou não fornecida"

**Solução**: 
- Verifique se a API Key está correta no `appsettings.json`
- Certifique-se de que a API Key está ativa em [NVIDIA AI Foundation Models](https://build.nvidia.com/)
- Verifique se o nome da seção no appsettings.json é "Nvidia" (não "Gemini")

### Erro: "Rate limit atingido"

**Solução**: 
- A API automaticamente aguarda e tenta novamente usando o header `Retry-After`
- Aguarde alguns minutos antes de fazer novas requisições
- Verifique os limites da sua conta no portal da NVIDIA

### Erro: "Placa não encontrada" recorrente

**Soluções**:
- Certifique-se de que a imagem contém uma placa visível
- Verifique se a qualidade da imagem é suficiente
- Tente ajustar o contraste ou brilho da imagem
- Garanta que a placa está legível e não está muito pequena na imagem

### Erro: "O formato da imagem em base64 é inválido"

**Soluções**:
- Verifique se o base64 não contém quebras de linha ou espaços extras
- Certifique-se de remover o prefixo `data:image/...;base64,` se presente
- Valide que a string base64 está completa e não foi truncada

### Performance lenta

**Soluções**:
- Otimize as imagens antes de enviar (reduza tamanho e resolução)
- Use formatos mais eficientes (JPEG ao invés de PNG para fotos)
- Considere processamento assíncrono para múltiplas imagens

### Cache não funciona como esperado

**Nota**: O cache é limpo automaticamente e perde dados ao reiniciar a aplicação. Para persistência, considere implementar cache em banco de dados ou arquivo.

## 📝 Notas Adicionais

- A API usa CORS configurado para permitir requisições de qualquer origem
- Logs são gerados automaticamente para facilitar debugging
- O sistema de retry ajuda a lidar com falhas temporárias da API da NVIDIA
- A validação de base64 aceita tanto strings puras quanto data URLs
- O serviço limpa automaticamente respostas JSON que podem vir com markdown code blocks

## 🔄 Escolhendo entre NVIDIA e Gemini

A API agora suporta ambos os provedores. Para trocar entre eles:

1. **Edite o `appsettings.json`** e altere o campo `AiProvider.Provider`:
   - Para usar NVIDIA: `"Provider": "Nvidia"`
   - Para usar Gemini: `"Provider": "Gemini"`

2. **Configure a API Key correspondente** no mesmo arquivo

3. **Reinicie a aplicação**

O formato das requisições e respostas da API permanece o mesmo independente do provedor escolhido, então não é necessário alterar o código cliente ao trocar de provedor.

## 📄 Licença

Este projeto é fornecido como está, sem garantias.

## 🤝 Contribuindo

Contribuições são bem-vindas! Sinta-se à vontade para abrir issues ou pull requests.

---

**Desenvolvido com .NET 8 e NVIDIA NIM API**
