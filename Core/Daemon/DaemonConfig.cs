using System.IO;
using System.Text.Json;

namespace ApexKernelOptimizer.Core.Daemon;

/// <summary>
/// Configuración del demonio nativo (toggles #21-31/#38 y reglas por juego).
/// Se guarda en %AppData%\ApexKernel\daemon_config.json y se cachea en memoria por timestamp.
/// </summary>
public sealed class DaemonRule
{
    public string Exe { get; set; } = "";
    public bool ExcludeCore0 { get; set; }
    public bool PhysicalCoresOnly { get; set; }
    public bool HighPriority { get; set; }
    public bool LowPriority { get; set; }
}

public sealed class DaemonConfigFile
{
    public Dictionary<string, bool> Flags { get; set; } = [];
    public List<DaemonRule> Rules { get; set; } = [];
    public int StandbyFreeThresholdMb { get; set; } = 1024;
}

public static class DaemonConfigStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static DaemonConfigFile? _cached;
    private static DateTime _lastWriteUtc = DateTime.MinValue;

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApexKernel", "daemon_config.json");

    public static DaemonConfigFile Load()
    {
        lock (Gate)
        {
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path))
                {
                    _cached = new DaemonConfigFile();
                    _lastWriteUtc = DateTime.MinValue;
                    return _cached;
                }

                DateTime writeUtc = File.GetLastWriteTimeUtc(path);
                if (_cached is not null && writeUtc == _lastWriteUtc)
                    return _cached;

                _cached = JsonSerializer.Deserialize<DaemonConfigFile>(File.ReadAllText(path)) ?? new DaemonConfigFile();
                _lastWriteUtc = writeUtc;
                return _cached;
            }
            catch
            {
                return _cached ?? new DaemonConfigFile();
            }
        }
    }

    public static void Save(DaemonConfigFile config)
    {
        lock (Gate)
        {
            string path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, WriteOptions));
            File.Move(temp, path, overwrite: true);
            _cached = config;
            _lastWriteUtc = File.GetLastWriteTimeUtc(path);
        }
    }

    public static bool GetFlag(string id) => Load().Flags.TryGetValue(id, out bool value) && value;

    public static void SetFlag(string id, bool value)
    {
        lock (Gate)
        {
            DaemonConfigFile config = Load();
            config.Flags[id] = value;
            Save(config);
        }
    }

    public static void RemoveFlag(string id)
    {
        lock (Gate)
        {
            DaemonConfigFile config = Load();
            config.Flags.Remove(id);
            Save(config);
        }
    }

    public static List<DaemonRule> GetRules() => Load().Rules;
}
