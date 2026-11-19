using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using ApexKernelOptimizer.Core.Models;

namespace ApexKernelOptimizer.Core.Engine;

public sealed class CatalogProvider
{
    private readonly string? _catalogPath;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Variantes curradas para los ajustes más habituales.</summary>
    private static readonly Dictionary<int, (string[] Labels, string[] Values)> CuratedOptions = new()
    {
        [1] = (["Desactivado (0)", "Normal (1) — Windows", "Alto (2)"], ["0", "1", "2"]),
        [3] = (["6/11 (10) — Windows", "8", "12", "20"], ["10", "8", "12", "20"]),
        [5] = (["200 ms", "300 ms", "400 ms", "500 ms — Windows"], ["200", "300", "400", "500"]),
        [6] = (["Desactivar (62)", "126 — Windows"], ["62", "126"]),
        [9] = (["Rápido (59)", "126 — Windows"], ["59", "126"]),
        [10] = (["0", "1 — Windows"], ["0", "1"]),
        [11] = (["Desactivar (506)", "510 — Windows"], ["506", "510"]),
        [12] = (["Activar (62)", "58 — Windows"], ["62", "58"]),
        [73] = (["1024 — Windows", "65536", "131072"], ["1024", "65536", "131072"]),
        [92] = (["0", "100", "400 — Windows"], ["0", "100", "400"]),
        [114] = (["10000 — Windows", "5000", "20000"], ["10000", "5000", "20000"]),
        [115] = (["0 (máximo)", "10 (recomendado)", "20 — Windows"], ["0", "10", "20"]),
        [123] = (["0", "50", "80 — Windows"], ["0", "50", "80"]),
        [124] = (["0", "65536", "131072"], ["0", "65536", "131072"]),
        [131] = (["64", "128 — Windows", "255"], ["64", "128", "255"]),
        [211] = (["Sin límite (0xFFFFFFFF)", "10 — Windows", "32"], ["4294967295", "10", "32"]),
        [212] = (["0 (máximo)", "10", "20 — Windows"], ["0", "10", "20"]),
        [226] = (["Mostrar todos (0)", "Ocultar (1) — Windows"], ["0", "1"]),
        [227] = (["10 — Windows", "Sin límite (0xFFFFFFFF)"], ["10", "4294967295"])
    };

    public CatalogProvider(string? catalogPath = null) => _catalogPath = catalogPath;

    public async Task<IReadOnlyList<OptimizationItem>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using Stream stream = _catalogPath is not null
            ? File.OpenRead(_catalogPath)
            : typeof(CatalogProvider).Assembly.GetManifestResourceStream(
                "ApexKernelOptimizer.optimizations_catalog.json")
                ?? throw new FileNotFoundException("No se encontró el catálogo incrustado.");
        List<OptimizationItem>? items = await JsonSerializer.DeserializeAsync<List<OptimizationItem>>(stream, Options, cancellationToken);
        if (items is null || items.Count != 227 || items.Select(item => item.Id).Distinct().Count() != 227)
            throw new InvalidDataException("El catálogo debe incluir exactamente 227 identificadores únicos.");

        foreach (OptimizationItem item in items)
        {
            // Un solo valor de Registro => siempre ofrecemos al menos dos opciones:
            // el valor optimizado y el valor oficial de Windows.
            if (item.IsImplemented && !item.IsTool && item.RegistryChanges.Count == 1)
            {
                if (CuratedOptions.TryGetValue(item.Id, out var curated))
                {
                    item.Options.AddRange(curated.Labels);
                    item.OptionValues.AddRange(curated.Values);
                }
                else
                {
                    RegistryChange change = item.RegistryChanges[0];
                    item.Options.Add($"Optimizado: {change.ApplyValue}");
                    item.OptionValues.Add(change.ApplyValue);
                    string official = string.IsNullOrWhiteSpace(change.RevertValue) ? "(ausente)" : change.RevertValue;
                    item.Options.Add($"Defecto Windows: {official}");
                    item.OptionValues.Add(string.IsNullOrWhiteSpace(change.RevertValue) ? "__DELETE__" : change.RevertValue);
                }
            }
        }
        return items.OrderBy(item => item.Id).ToArray();
    }
}
