using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace ApexKernelOptimizer.Views;

/// <summary>
/// Monitor avanzado de procesos (#143): lista PID, nombre, CPU %, RAM y prioridad
/// con refresco cada 2 s, búsqueda, colores por carga y cambio de prioridad o cierre.
/// </summary>
public sealed class ProcessMonitorWindow : Window
{
    private sealed class ProcessRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public double CpuPercent { get; init; }
        public double MemoryMb { get; init; }
        public string Priority { get; init; } = "";
        public string Display => $"{Id,-8} {Name,-28} {CpuPercent,7:0.0} {MemoryMb,9:0} {Priority}";
        public Brush UsageBrush => CpuPercent >= 50
            ? B("DangerBrush", "#EF4444")
            : CpuPercent >= 15 ? B("WarningBrush", "#F59E0B") : B("TextBrush", "#F2F5F7");
        public override string ToString() => Display;
    }

    private readonly ObservableCollection<ProcessRow> _rows = [];
    private readonly ListBox _list;
    private readonly TextBox _search = new() { Width = 240 };
    private readonly TextBlock _count;
    private readonly TextBlock _summary;
    private readonly TextBlock _detail;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, (DateTime Time, TimeSpan Cpu)> _lastCpu = [];
    private DateTime _lastSample = DateTime.UtcNow;
    private readonly int _logical = Environment.ProcessorCount;

    public ProcessMonitorWindow(int toolId)
    {
        Title = $"#{toolId} · Monitor avanzado de procesos";
        Width = 900;
        Height = 700;
        MinWidth = 720;
        MinHeight = 540;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Header(
            "Monitor de procesos",
            "Uso de CPU estimado por muestreo de tiempo de procesador, memoria de trabajo y prioridad. Se actualiza cada 2 segundos. Colores: rojo ≥ 50 % de CPU, ámbar ≥ 15 %.",
            $"#{toolId}"));

        var filterRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        filterRow.Children.Add(new TextBlock
        {
            Text = "Buscar (nombre o PID):",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = B("MutedBrush", "#9DA8B3")
        });
        _search.TextChanged += (_, _) => ApplyFilter();
        filterRow.Children.Add(_search);
        _count = new TextBlock
        {
            Text = "0/0 visibles",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            Foreground = B("MutedBrush", "#9DA8B3"),
            FontFamily = new FontFamily("Consolas")
        };
        filterRow.Children.Add(_count);
        panel.Children.Add(filterRow);

        _summary = new TextBlock
        {
            Text = "Reuniendo muestras…",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(_summary);

        panel.Children.Add(new TextBlock
        {
            Text = $"{"PID",-8} {"Proceso",-28} {"CPU %",7} {"RAM MB",9} Prioridad",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = B("DimBrush", "#71808D"),
            Margin = new Thickness(2, 0, 0, 4)
        });

        _list = new ListBox
        {
            ItemsSource = _rows,
            Height = 360,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("Display"));
        text.SetBinding(TextBlock.ForegroundProperty, new Binding("UsageBrush"));
        text.SetValue(TextBlock.MarginProperty, new Thickness(6, 2, 6, 2));
        template.VisualTree = text;
        _list.ItemTemplate = template;
        _list.SelectionChanged += (_, _) => UpdateDetail();
        _list.MouseDoubleClick += async (_, _) => await KillAsync();
        panel.Children.Add(_list);

        _detail = new TextBlock
        {
            Text = "Selecciona un proceso para ver su detalle.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(_detail);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(MakeButton("Prioridad Alta", () => SetPriorityAsync(ProcessPriorityClass.High)));
        buttons.Children.Add(MakeButton("Prioridad Normal", () => SetPriorityAsync(ProcessPriorityClass.Normal)));
        buttons.Children.Add(MakeButton("Prioridad Baja", () => SetPriorityAsync(ProcessPriorityClass.BelowNormal)));
        buttons.Children.Add(MakeButton("En espera (Idle)", () => SetPriorityAsync(ProcessPriorityClass.Idle)));
        buttons.Children.Add(MakeButton("Finalizar proceso", KillAsync, danger: true));
        panel.Children.Add(buttons);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 12, 0, 0)
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

    private static Button MakeButton(string label, Func<Task> action, bool danger = false)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Background = danger ? B("RedSoftBrush", "#3D2429") : B("PanelAltBrush", "#20262E"),
            BorderBrush = danger ? B("DangerBrush", "#EF4444") : B("BorderBrush", "#323C46"),
            Foreground = danger ? B("DangerBrush", "#EF4444") : B("TextBrush", "#F2F5F7")
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private void SetStatus(string text, bool ok = true)
    {
        _status.Text = text;
        _status.Foreground = ok ? B("MutedBrush", "#9DA8B3") : B("DangerBrush", "#EF4444");
    }

    private void UpdateDetail()
    {
        if (_list.SelectedItem is not ProcessRow row)
        {
            _detail.Text = "Selecciona un proceso para ver su detalle.";
            return;
        }
        _detail.Text = $"Detalle: {row.Name} · PID {row.Id} · CPU {row.CpuPercent:0.0} % · RAM {row.MemoryMb:0} MB · prioridad {row.Priority}.";
    }

    private void ApplyFilter()
    {
        string query = _search.Text.Trim();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_rows);
        view.Filter = value =>
        {
            if (value is not ProcessRow row) return false;
            if (query.Length == 0) return true;
            return row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
        };
        _count.Text = $"{view.Cast<object>().Count()}/{_rows.Count} visibles";
    }

    private async Task SampleAsync()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = (now - _lastSample).TotalSeconds;
        _lastSample = now;
        var current = new Dictionary<int, (DateTime, TimeSpan)>();
        var snapshot = new List<ProcessRow>();
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited) continue;
                int id = process.Id;
                string name = process.ProcessName;

                // CPU y prioridad pueden estar protegidos (PPL/anticheat): un fallo aquí no debe ocultar el proceso.
                double percent = 0;
                try
                {
                    TimeSpan cpu = process.TotalProcessorTime;
                    if (_lastCpu.TryGetValue(id, out (DateTime Time, TimeSpan Cpu) previous) && elapsed > 0)
                        percent = (cpu - previous.Cpu).TotalSeconds / (elapsed * _logical) * 100.0;
                    current[id] = (now, cpu);
                }
                catch { }

                string priority;
                try { priority = process.PriorityClass.ToString(); }
                catch { priority = "Protegido"; }

                double memoryMb = 0;
                try { memoryMb = process.WorkingSet64 / (1024.0 * 1024.0); }
                catch { }

                snapshot.Add(new ProcessRow
                {
                    Id = id,
                    Name = name,
                    CpuPercent = Math.Max(0, percent),
                    MemoryMb = memoryMb,
                    Priority = priority
                });
            }
            catch { }
            finally { try { process.Dispose(); } catch { } }
        }
        _lastCpu.Clear();
        foreach ((int id, (DateTime, TimeSpan) value) in current) _lastCpu[id] = value;

        var selectedId = _list.SelectedItem is ProcessRow selected ? selected.Id : 0;
        _rows.Clear();
        foreach (ProcessRow row in snapshot.OrderByDescending(row => row.CpuPercent).ThenByDescending(row => row.MemoryMb))
            _rows.Add(row);
        if (selectedId != 0)
            foreach (object item in _list.Items)
                if (item is ProcessRow row && row.Id == selectedId) { _list.SelectedItem = row; break; }
        ApplyFilter();
        _summary.Text = $"Procesos: {snapshot.Count} · CPU ≥ 50 %: {snapshot.Count(r => r.CpuPercent >= 50)} · " +
                        $"RAM ≥ 500 MB: {snapshot.Count(r => r.MemoryMb >= 500)} · muestra: {DateTime.Now:HH:mm:ss}";
        await Task.CompletedTask;
    }

    private async Task SetPriorityAsync(ProcessPriorityClass priority)
    {
        if (_list.SelectedItem is not ProcessRow row) { SetStatus("Selecciona un proceso de la lista.", ok: false); return; }
        try
        {
            using Process process = Process.GetProcessById(row.Id);
            process.PriorityClass = priority;
            SetStatus($"{row.Name} (PID {row.Id}): prioridad {priority} aplicada.");
            await SampleAsync();
        }
        catch (Exception ex) { SetStatus($"No se pudo cambiar la prioridad de {row.Name}: {ex.Message}", ok: false); }
    }

    private async Task KillAsync()
    {
        if (_list.SelectedItem is not ProcessRow row) { SetStatus("Selecciona un proceso de la lista.", ok: false); return; }
        if (MessageBox.Show(this, $"¿Finalizar {row.Name} (PID {row.Id}) y todo su árbol de procesos?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            using Process process = Process.GetProcessById(row.Id);
            process.Kill(entireProcessTree: true);
            SetStatus($"{row.Name} (PID {row.Id}) finalizado. Los cambios no se pueden deshacer.");
            await Task.Delay(300);
            await SampleAsync();
        }
        catch (Exception ex) { SetStatus($"No se pudo finalizar {row.Name}: {ex.Message}", ok: false); }
    }
}
