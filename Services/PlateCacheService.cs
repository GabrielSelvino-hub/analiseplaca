using System.Collections.Concurrent;

namespace PlateAnalysisApi.Services;

public class PlateCacheService
{
    private readonly ConcurrentDictionary<string, DateTime> _processedPlates = new();
    private readonly ILogger<PlateCacheService> _logger;
    private readonly Timer? _cleanupTimer;

    public PlateCacheService(ILogger<PlateCacheService> logger)
    {
        _logger = logger;
        // Limpeza automática a cada 1 hora para remover entradas antigas
        _cleanupTimer = new Timer(CleanupOldEntries, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public bool IsDuplicate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate) || 
            plate == "Placa não encontrada" || 
            plate == "Erro de Processamento.")
        {
            return false;
        }

        return _processedPlates.ContainsKey(plate);
    }

    public void AddPlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate) || 
            plate == "Placa não encontrada" || 
            plate == "Erro de Processamento.")
        {
            return;
        }

        _processedPlates.TryAdd(plate, DateTime.UtcNow);
        _logger.LogInformation("Placa {Plate} adicionada ao cache", plate);
    }

    private void CleanupOldEntries(object? state)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24); // Remove entradas com mais de 24 horas
        var keysToRemove = _processedPlates
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _processedPlates.TryRemove(key, out _);
            _logger.LogInformation("Placa {Plate} removida do cache (expirada)", key);
        }

        if (keysToRemove.Count > 0)
        {
            _logger.LogInformation("Limpeza de cache concluída: {Count} entradas removidas", keysToRemove.Count);
        }
    }

    public int GetCacheCount() => _processedPlates.Count;
}

