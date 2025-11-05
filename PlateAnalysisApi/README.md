# API .NET 8 - Análise de Placas Veiculares

API REST para análise de placas veiculares usando Google Gemini AI. Replica a funcionalidade do `index.html` em formato de API.

## Requisitos

- .NET 8 SDK
- API Key do Google Gemini

## Configuração

1. Edite o arquivo `appsettings.json` e adicione sua API Key do Gemini:

```json
{
  "Gemini": {
    "ApiKey": "SUA_API_KEY_AQUI"
  }
}
```

## Executando a API

```bash
cd PlateAnalysisApi
dotnet run
```

A API estará disponível em `http://localhost:5000` ou `https://localhost:5001`.

## Endpoints

### POST /api/analyze-plate

Analisa uma imagem de veículo e retorna a placa, detalhes do veículo e imagem recortada.

**Request Body:**
```json
{
  "imageBase64": "iVBORw0KGgoAAAANS...",
  "mimeType": "image/jpeg"
}
```

**Response (200 OK):**
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
    "placa_mercosul": "Mercosul"
  },
  "imagemPlacaRecortada": {
    "base64": "iVBORw0KGgoAAAANS...",
    "mimeType": "image/png"
  },
  "erro": null
}
```

**Response (Duplicata):**
```json
{
  "placa": "ABC1234",
  "duplicada": true,
  "detalhesVeiculo": null,
  "imagemPlacaRecortada": null,
  "erro": "Atenção: A placa \"ABC1234\" já foi processada nesta sessão. O processamento foi interrompido."
}
```

### GET /health

Health check da API.

**Response:**
```json
{
  "status": "healthy",
  "timestamp": "2024-01-01T00:00:00Z"
}
```

## Fluxo de Processamento

1. **OCR da Placa**: Extrai o texto da placa usando Gemini Text API
2. **Verificação de Duplicatas**: Verifica se a placa já foi processada (cache em memória)
3. **Análise de Detalhes**: Analisa cor, tipo, marca e fabricante do veículo
4. **Recorte de Imagem**: Gera imagem recortada da placa usando Gemini Image API

## Tipos MIME Suportados

- `image/jpeg`
- `image/png`
- `image/gif`
- `image/webp`

## Tratamento de Erros

- Validação de base64 inválido retorna `400 Bad Request`
- Erros da API Gemini são tratados com retry automático (3 tentativas com backoff exponencial)
- Erros de processamento são retornados no campo `erro` da resposta

## Cache de Duplicatas

O cache de duplicatas é mantido em memória e:
- Expira automaticamente após 24 horas
- É limpo automaticamente a cada hora
- Apenas placas válidas são armazenadas

## Exemplo de Uso (cURL)

```bash
curl -X POST http://localhost:5000/api/analyze-plate \
  -H "Content-Type: application/json" \
  -d '{
    "imageBase64": "iVBORw0KGgoAAAANS...",
    "mimeType": "image/jpeg"
  }'
```

