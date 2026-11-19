using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using ApexKernelOptimizer.Core.Models;
using ApexKernelOptimizer.Core.Engine;

namespace ApexKernelOptimizer.Core.Services;

/// <summary>Temas de la interfaz. Apex Neo es la identidad propia inspirada en NoPing.</summary>
public static class ThemeService
{
    public static readonly string[] Themes = ["Apex Neo", "Apex Atlas", "Apex Amber"];

    private sealed record Palette(string Canvas, string Sidebar, string Panel, string PanelAlt,
        string Accent, string Warning, string Danger, string Border);

    private static readonly Dictionary<string, Palette> Palettes = new()
    {
        ["Apex Neo"] = new("#0B0E11", "#10151A", "#141B21", "#1B242C", "#B6F13C", "#FFC84A", "#FF5C6C", "#2B3842"),
        ["Apex Atlas"] = new("#0E1420", "#111A28", "#162031", "#1D2A3D", "#4F9DFF", "#FFC857", "#FF6B6B", "#2C4260"),
        ["Apex Amber"] = new("#100D09", "#171208", "#1E180D", "#282012", "#F5A623", "#FFD166", "#FF5C5C", "#453617")
    };

    private static string _current = "Apex Neo";
    public static string Current => _current;
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexKernel", "theme.txt");

    public static string Load()
    {
        try
        {
            string saved = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath).Trim() : "Apex Neo";
            Apply(Palettes.ContainsKey(saved) ? saved : "Apex Neo");
        }
        catch { Apply("Apex Neo"); }
        return _current;
    }

    public static void Apply(string name)
    {
        if (!Palettes.TryGetValue(name, out Palette? p)) return;
        _current = name;
        if (Application.Current?.Resources is null)
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (dir is not null) Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, name);
            }
            catch { }
            return;
        }
        try
        {
            Application.Current.Resources["CanvasColor"] = Parse(p.Canvas);
            Application.Current.Resources["SidebarColor"] = Parse(p.Sidebar);
            Application.Current.Resources["PanelColor"] = Parse(p.Panel);
            Application.Current.Resources["PanelAltColor"] = Parse(p.PanelAlt);
            Application.Current.Resources["AccentColor"] = Parse(p.Accent);
            Application.Current.Resources["WarningColor"] = Parse(p.Warning);
            Application.Current.Resources["DangerColor"] = Parse(p.Danger);
            Application.Current.Resources["BorderBrush"] = new SolidColorBrush(Parse(p.Border));
        }
        catch { /* sin ventana WPF (pruebas/servicio): solo se persiste la preferencia */ }
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, name);
        }
        catch { }
    }

    private static Color Parse(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromRgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }
}

/// <summary>Una lectura de telemetría del sistema.</summary>
public sealed record TelemetrySample(double Cpu, double Ram, double Gpu, double LatencyMs, double Disk, int Processes);

/// <summary>Recolector ligero de métricas nativas, sin dependencias externas.</summary>
public sealed class TelemetryService
{
    private long _lastIdle, _lastKernel, _lastUser;
    private static readonly string? CachedNvidiaSmiPath = ResolveNvidiaSmiPath();

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation info, uint cb);

    private static string? ResolveNvidiaSmiPath()
    {
        try
        {
            string sys = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (File.Exists(sys)) return sys;
            string nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
            if (File.Exists(nvsmi)) return nvsmi;
        }
        catch { }
        return null;
    }

    private static int ReadProcessCountFast()
    {
        try
        {
            uint size = (uint)Marshal.SizeOf<PerformanceInformation>();
            if (GetPerformanceInfo(out PerformanceInformation info, size) && info.ProcessCount > 0)
                return (int)info.ProcessCount;
        }
        catch { }
        return Safe(() => Process.GetProcesses().Length, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; public ulong Value => ((ulong)High << 32) | Low; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length; public uint MemoryLoad; public ulong TotalPhysical; public ulong AvailablePhysical;
        public ulong TotalPageFile; public ulong AvailablePageFile; public ulong TotalVirtual; public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    public async Task<TelemetrySample> SampleAsync()
    {
        double cpu = ReadCpu();
        double ram = ReadRam();
        double disk = ReadDisk();
        int processes = ReadProcessCountFast();

        Task<double> gpuTask = ReadGpuAsync();
        Task<double> latencyTask = ReadLatencyAsync();
        await Task.WhenAll(gpuTask, latencyTask).ConfigureAwait(false);

        return new TelemetrySample(cpu, ram, await gpuTask.ConfigureAwait(false),
            await latencyTask.ConfigureAwait(false), disk, processes);
    }

    public TelemetrySample Sample() => SampleAsync().GetAwaiter().GetResult();

    private double ReadCpu()
    {
        try
        {
            if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user)) return 0;
            ulong i = idle.Value, k = kernel.Value, u = user.Value;
            if (_lastKernel == 0) { _lastIdle = (long)i; _lastKernel = (long)k; _lastUser = (long)u; return 0; }
            long di = (long)(i - (ulong)_lastIdle);
            long dk = (long)(k - (ulong)_lastKernel);
            long du = (long)(u - (ulong)_lastUser);
            _lastIdle = (long)i; _lastKernel = (long)k; _lastUser = (long)u;
            long total = dk + du;
            return total <= 0 ? 0 : Math.Clamp((total - di) * 100.0 / total, 0, 100);
        }
        catch { return 0; }
    }

    private static double ReadRam()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : 0;
        }
        catch { return 0; }
    }

    private static async Task<double> ReadGpuAsync()
    {
        if (CachedNvidiaSmiPath is null) return 0;
        try
        {
            var psi = new ProcessStartInfo(CachedNvidiaSmiPath)
            {
                Arguments = "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            using Process? p = Process.Start(psi);
            if (p is null) return 0;
            Task<string> readTask = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
            string output = await readTask.ConfigureAwait(false);
            string first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "0";
            return double.TryParse(first, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? Math.Clamp(v, 0, 100) : 0;
        }
        catch { return 0; }
    }

    private static async Task<double> ReadLatencyAsync()
    {
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync("1.1.1.1", 1000).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
        }
        catch { return -1; }
    }

    private static double ReadDisk()
    {
        try
        {
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            double total = drive.TotalSize;
            return total <= 0 ? 0 : (total - drive.AvailableFreeSpace) * 100.0 / total;
        }
        catch { return 0; }
    }

    private static T Safe<T>(Func<T> action, T fallback)
    {
        try { return action(); } catch { return fallback; }
    }
}

/// <summary>Exporta el estado respaldado como informe HTML, JSON o Markdown.</summary>
public static class ReportService
{
    public sealed record ChangeRow(int Id, string Title, string Category, string Risk, string Previous, string Current, string Path);

    public static IReadOnlyList<ChangeRow> BuildRows(IEnumerable<OptimizationItem> items)
    {
        string backupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApexKernel", "state_backup.json");
        Dictionary<string, JsonElement> state = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(backupPath))
        {
            try { state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(backupPath)) ?? state; }
            catch { }
        }
        var rows = new List<ChangeRow>();
        foreach (OptimizationItem item in items)
        {
            if (item.IsTool || item.RegistryChanges.Count == 0) continue;
            RegistryChange change = item.RegistryChanges[0];
            string key = $"{change.Hive}\\{change.KeyPath}\\{change.ValueName}";
            if (!state.TryGetValue(key, out JsonElement element)) continue;
            string previous = "(desconocido)";
            try
            {
                RegistryValueSnapshot? snapshot = element.Deserialize<RegistryValueSnapshot>();
                previous = snapshot?.SerializedData ?? "(sin valor)";
            }
            catch { }
            rows.Add(new ChangeRow(item.Id, item.Title, item.CategoryLabel, item.RiskLabel,
                previous, item.TargetValueText, key));
        }
        return rows.OrderBy(r => r.Id).ToArray();
    }

    public static string Export(IReadOnlyList<ChangeRow> rows, string format)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return format.ToLowerInvariant() switch
        {
            "json" => JsonSerializer.Serialize(new { generated = stamp, count = rows.Count, changes = rows },
                new JsonSerializerOptions { WriteIndented = true }),
            "md" or "markdown" => Markdown(rows, stamp),
            _ => Html(rows, stamp)
        };
    }

    private static string Markdown(IReadOnlyList<ChangeRow> rows, string stamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Informe de cambios de ApexKernel Optimizer");
        sb.AppendLine();
        sb.AppendLine($"Generado: {stamp} · {rows.Count} ajustes con respaldo");
        sb.AppendLine();
        sb.AppendLine("| ID | Ajuste | Categoría | Riesgo | Valor anterior | Objetivo | Clave |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (ChangeRow r in rows)
            sb.AppendLine($"| {r.Id} | {EscapeCell(r.Title)} | {EscapeCell(r.Category)} | {r.Risk} | {EscapeCell(r.Previous)} | {EscapeCell(r.Current)} | {EscapeCell(r.Path)} |");
        return sb.ToString();
    }

    private static string Html(IReadOnlyList<ChangeRow> rows, string stamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang='es'><head><meta charset='utf-8'>");
        sb.AppendLine("<title>ApexKernel · Informe de cambios</title>");
        sb.AppendLine("<style>body{background:#0B0E11;color:#E7EEF3;font-family:'Segoe UI',sans-serif;margin:0;padding:32px}");
        sb.AppendLine("h1{color:#B6F13C;font-size:24px}meta{color:#8FA3B3}table{width:100%;border-collapse:collapse;margin-top:20px}");
        sb.AppendLine("th{text-align:left;background:#141B21;color:#B6F13C;padding:10px;font-size:12px}");
        sb.AppendLine("td{padding:9px;border-bottom:1px solid #2B3842;font-size:12px}tr:hover{background:#141B21}");
        sb.AppendLine("code{color:#9FE870}small{color:#71808D}</style></head><body>");
        sb.AppendLine("<h1>ApexKernel Optimizer</h1><meta>Informe de cambios · generado " + stamp + " · " + rows.Count + " ajustes</meta>");
        sb.AppendLine("<table><thead><tr><th>ID</th><th>Ajuste</th><th>Categoría</th><th>Riesgo</th><th>Anterior</th><th>Objetivo</th><th>Clave</th></tr></thead><tbody>");
        foreach (ChangeRow r in rows)
            sb.AppendLine($"<tr><td>#{r.Id}</td><td>{Escape(r.Title)}</td><td>{Escape(r.Category)}</td><td>{Escape(r.Risk)}</td><td><code>{Escape(r.Previous)}</code></td><td><code>{Escape(r.Current)}</code></td><td><small>{Escape(r.Path)}</small></td></tr>");
        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value ?? "");
    private static string EscapeCell(string value) => (value ?? "").Replace("|", "\\|").Replace("\n", " ");
}
