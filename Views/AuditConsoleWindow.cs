using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ApexKernelOptimizer.Views;

/// <summary>
/// Consola de auditoría en vivo (#155): CPU total, RAM usada/libre, procesos que más
/// CPU y memoria consumen, y disco/sistema básico, refrescado cada 2 s.
/// </summary>
public sealed class AuditConsoleWindow : Window
{
    private readonly TextBox _output;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _cpuValue = NewValue();
    private readonly TextBlock _ramValue = NewValue();
    private readonly TextBlock _processValue = NewValue();
    private readonly TextBlock _status;
    private readonly Dictionary<int, (DateTime Time, TimeSpan Cpu)> _lastCpu = [];
    private DateTime _lastSample = DateTime.UtcNow;
    private readonly int _logical = Environment.ProcessorCount;

    public AuditConsoleWindow(int toolId)
    {
        Title = $"#{toolId} · Consola de auditoría del sistema";
        Width = 900;
        Height = 700;
        MinWidth = 720;
        MinHeight = 540;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Header(
            "Auditoría del sistema",
            "Telemetría en vivo: CPU total por muestreo de tiempo de procesador, memoria física y los procesos que más CPU y RAM consumen. Actualización automática cada 2 segundos.",
            $"#{toolId}"));

        var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        cards.Children.Add(StatCard("CPU TOTAL", _cpuValue, B("AccentBrush", "#22C55E")));
        cards.Children.Add(StatCard("RAM EN USO", _ramValue, B("WarningBrush", "#F59E0B")));
        cards.Children.Add(StatCard("PROCESOS ACTIVOS", _processValue, B("AccentBrush", "#22C55E")));
        panel.Children.Add(cards);

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var refresh = MakeButton("Actualizar ahora", SampleAsync);
        buttons.Children.Add(refresh);
        buttons.Children.Add(new TextBlock
        {
            Text = "Colores: verde = normal, ámbar = atención, rojo = uso elevado. La lista inferior usa columnas alineadas en Consolas.",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = B("DimBrush", "#71808D"),
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0)
        });
        panel.Children.Add(buttons);

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Height = 430,
            Background = B("PanelBrush", "#191E24"),
            Foreground = B("TextBrush", "#F2F5F7"),
            BorderBrush = B("BorderBrush", "#323C46"),
            BorderThickness = new Thickness(1)
        };
        panel.Children.Add(_output);

        _status = new TextBlock
        {
            Text = "Esperando primera muestra…",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 10, 0, 0)
        };
        panel.Children.Add(_status);
        Content = panel;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await SampleAsync();
        Loaded += async (_, _) => { await SampleAsync(); _timer.Start(); };
        Closing += (_, _) => _timer.Stop();
    }

    private static SolidColorBrush B(string key, string fallback) =>
        Application.Current?.Resources[key] as SolidColorBrush
        ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));

    private static TextBlock NewValue() => new()
    {
        Text = "—",
        FontSize = 22,
        FontWeight = FontWeights.Bold,
        FontFamily = new FontFamily("Consolas"),
        Foreground = B("AccentBrush", "#22C55E")
    };

    private static Border StatCard(string label, TextBlock value, Brush accent)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B("MutedBrush", "#9DA8B3") });
        body.Children.Add(value);
        return new Border
        {
            Background = B("PanelBrush", "#191E24"),
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 10, 0),
            MinWidth = 196,
            Child = body
        };
    }

    private static Border Header(string title, string subtitle, string? badge = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush", "#F2F5F7") });
        texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = B("MutedBrush", "#9DA8B3"), Margin = new Thickness(0, 4, 0, 0) });
        grid.Children.Add(texts);
        if (badge is not null)
        {
            var pill = new Border
            {
                Background = B("GreenSoftBrush", "#20362E"),
                BorderBrush = B("AccentBrush", "#22C55E"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            pill.Child = new TextBlock { Text = badge, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = B("AccentBrush", "#22C55E") };
            Grid.SetColumn(pill, 1);
            grid.Children.Add(pill);
        }
        return new Border
        {
            Background = B("PanelAltBrush", "#20262E"),
            BorderBrush = B("BorderBrush", "#323C46"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 16),
            Child = grid
        };
    }

    private static Button MakeButton(string label, Func<Task> action)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Background = B("PanelAltBrush", "#20262E"),
            BorderBrush = B("BorderBrush", "#323C46"),
            Foreground = B("TextBrush", "#F2F5F7")
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static Brush UsageBrush(double percent) => percent >= 80
        ? B("DangerBrush", "#EF4444")
        : percent >= 40 ? B("WarningBrush", "#F59E0B") : B("AccentBrush", "#22C55E");

    private async Task SampleAsync()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            double elapsed = (now - _lastSample).TotalSeconds;
            _lastSample = now;

            var current = new Dictionary<int, (DateTime, TimeSpan)>();
            var byCpu = new List<(double Percent, string Name, int Id)>();
            var byMemory = new List<(double Mb, string Name, int Id)>();
            double cpuPercent = 0;
            double ramTotalMb = 0, ramFreeMb = 0;

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.HasExited) continue;
                    int id = process.Id;
                    string name = process.ProcessName;

                    // La CPU puede estar denegada en procesos protegidos: se cuenta igual y se lee su RAM aparte.
                    TimeSpan cpu = TimeSpan.Zero;
                    try
                    {
                        cpu = process.TotalProcessorTime;
                        if (_lastCpu.TryGetValue(id, out (DateTime Time, TimeSpan Cpu) previous) && elapsed > 0)
                        {
                            double percent = (cpu - previous.Cpu).TotalSeconds / (elapsed * _logical) * 100.0;
                            cpuPercent += percent;
                            if (percent >= 0.5) byCpu.Add((Math.Max(0, percent), name, id));
                        }
                    }
                    catch { }
                    current[id] = (now, cpu);

                    try
                    {
                        double mb = process.WorkingSet64 / (1024.0 * 1024.0);
                        if (mb >= 100) byMemory.Add((mb, name, id));
                    }
                    catch { }
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
            _lastCpu.Clear();
            foreach ((int id, (DateTime, TimeSpan) value) in current) _lastCpu[id] = value;

            var status = new NativeMemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMemoryStatus>() };
            if (NativeGlobalMemoryStatus(ref status))
            {
                ramTotalMb = status.TotalPhysical / (1024.0 * 1024.0);
                ramFreeMb = status.AvailablePhysical / (1024.0 * 1024.0);
            }
            cpuPercent = Math.Clamp(cpuPercent, 0, 100);
            double ramPercentUsed = ramTotalMb > 0 ? (ramTotalMb - ramFreeMb) / ramTotalMb * 100 : 0;

            _cpuValue.Text = $"{cpuPercent:0.0} %";
            _cpuValue.Foreground = UsageBrush(cpuPercent);
            _ramValue.Text = $"{ramPercentUsed:0.0} %";
            _ramValue.Foreground = UsageBrush(ramPercentUsed);
            _processValue.Text = current.Count.ToString();
            _processValue.Foreground = B("AccentBrush", "#22C55E");

            var lines = new List<string>
            {
                $"=== Auditoría del sistema · {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                $"CPU total: {cpuPercent,5:0.0} %  ({_logical} hilos lógicos)",
                $"RAM: {(ramTotalMb - ramFreeMb),8:0} / {ramTotalMb,8:0} MB usados ({ramPercentUsed,5:0} %) · Libre: {ramFreeMb,8:0} MB",
                $"Procesos activos: {current.Count}",
                "",
                "--- Mayor uso de CPU (porcentaje / PID / proceso) ---"
            };
            foreach ((double percent, string name, int id) in byCpu.OrderByDescending(entry => entry.Percent).Take(10))
                lines.Add($"  {percent,6:0.0} %  PID {id,-7} {name}");
            lines.Add("");
            lines.Add("--- Mayor uso de RAM (MB / PID / proceso) ---");
            foreach ((double mb, string name, int id) in byMemory.OrderByDescending(entry => entry.Mb).Take(10))
                lines.Add($"  {mb,9:0} MB  PID {id,-7} {name}");
            lines.Add("");
            lines.Add("Consejo: revisa procesos con CPU sostenida > 10 % o RAM > 500 MB que no reconozcas antes de cerrarlos.");
            _output.Text = string.Join(Environment.NewLine, lines);
            _output.CaretIndex = _output.Text.Length;
            _status.Text = $"Última muestra: {DateTime.Now:HH:mm:ss} · muestreo automático cada 2 s · hilos lógicos: {_logical}.";
            _status.Foreground = B("MutedBrush", "#9DA8B3");
        }
        catch (Exception ex)
        {
            _output.Text = $"No se pudo completar la auditoría: {ex.Message}";
            _status.Text = $"Error en la muestra: {ex.Message}";
            _status.Foreground = B("DangerBrush", "#EF4444");
        }
        await Task.CompletedTask;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool NativeGlobalMemoryStatus(ref NativeMemoryStatus status);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeMemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
