using System.Text.Json;
using System.IO;

namespace ApexKernelOptimizer.Core.Engine;

public sealed class StateBackupManager
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    private Dictionary<string, JsonElement>? _cachedState;
    private DateTime _cachedWriteTimeUtc = DateTime.MinValue;

    public StateBackupManager(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ApexKernel", "state_backup.json");
    }

    private async Task<Dictionary<string, JsonElement>> GetStateLockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            _cachedState = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            _cachedWriteTimeUtc = DateTime.MinValue;
            return _cachedState;
        }

        DateTime lastWrite = File.GetLastWriteTimeUtc(_filePath);
        if (_cachedState is not null && lastWrite == _cachedWriteTimeUtc)
            return _cachedState;

        try
        {
            string json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            _cachedState = loaded is not null
                ? new Dictionary<string, JsonElement>(loaded, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            _cachedWriteTimeUtc = lastWrite;
        }
        catch
        {
            _cachedState ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        return _cachedState;
    }

    private async Task PersistStateLockedAsync(Dictionary<string, JsonElement> state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string tempPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(state, _options), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _filePath, true);
        _cachedState = state;
        _cachedWriteTimeUtc = File.GetLastWriteTimeUtc(_filePath);
    }

    public async Task SaveOriginalAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, JsonElement> state = await GetStateLockedAsync(cancellationToken).ConfigureAwait(false);
            if (state.ContainsKey(key)) return;
            state[key] = JsonSerializer.SerializeToElement(value);
            await PersistStateLockedAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<T> ReadOriginalAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, JsonElement> state = await GetStateLockedAsync(cancellationToken).ConfigureAwait(false);
            return state.TryGetValue(key, out JsonElement value) ? value.Deserialize<T>()! : default!;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HasBackupAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, JsonElement> state = await GetStateLockedAsync(cancellationToken).ConfigureAwait(false);
            return state.ContainsKey(key);
        }
        finally { _gate.Release(); }
    }

    // ===== Valor por defecto (estado que tenía Windows antes de que ApexKernel tocara el ajuste) =====
    private static string DefaultKey(string key) => "Default:" + key;

    public async Task SaveDefaultAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, JsonElement> state = await GetStateLockedAsync(cancellationToken).ConfigureAwait(false);
            string dk = DefaultKey(key);
            if (state.ContainsKey(dk)) return;
            state[dk] = JsonSerializer.SerializeToElement(value);
            await PersistStateLockedAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<T> ReadDefaultAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, JsonElement> state = await GetStateLockedAsync(cancellationToken).ConfigureAwait(false);
            return state.TryGetValue(DefaultKey(key), out JsonElement value) ? value.Deserialize<T>()! : default!;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HasDefaultAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, JsonElement> state = await GetStateLockedAsync(cancellationToken).ConfigureAwait(false);
            return state.ContainsKey(DefaultKey(key));
        }
        finally { _gate.Release(); }
    }

    public async Task<string> GetIndexSummaryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath)) return $"No hay valores respaldados todavía.\n\nArchivo de respaldo: {_filePath}";
            Dictionary<string, JsonElement> state = await GetStateLockedAsync(cancellationToken).ConfigureAwait(false);
            string keys = state.Count == 0 ? "(sin entradas)" : string.Join(Environment.NewLine, state.Keys.Order(StringComparer.OrdinalIgnoreCase));
            return $"Respaldo exacto de ApexKernel\n{state.Count} entradas guardadas\n\n{keys}\n\nArchivo: {_filePath}\n\nEsta vista muestra solo los nombres de las entradas, nunca los valores guardados.";
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveOriginalAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath)) return;
            Dictionary<string, JsonElement> state = await GetStateLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!state.Remove(key)) return;
            await PersistStateLockedAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
