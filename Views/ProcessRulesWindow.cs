using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApexKernelOptimizer.Core.Daemon;

namespace ApexKernelOptimizer.Views;

/// <summary>
/// Reglas por juego del demonio nativo (#21 y #22): exe + afinidad sin núcleo 0 / solo núcleos físicos.
/// </summary>
public sealed class ProcessRulesWindow : Window
{
    private readonly ListBox _rules = new() { Height = 200, FontFamily = new FontFamily("Consolas") };
    private readonly TextBox _exeInput = new();
    private readonly TextBox _search = new() { Width = 220 };
    private readonly TextBlock _count;
    private readonly CheckBox _excludeCore0 = new() { Content = "Excluir núcleo 0 (#21)" };
    private readonly CheckBox _physicalOnly = new() { Content = "Solo núcleos físicos (#22)" };
    private readonly CheckBox _highPriority = new() { Content = "Prioridad alta mientras corre" };
    private readonly CheckBox _lowPriority = new() { Content = "Prioridad baja mientras corre" };
    private readonly TextBlock _status;
    private bool _syncingPriority;

    public ProcessRulesWindow()
    {
        Title = "Reglas del demonio nativo por juego";
        Width = 700;
        Height = 660;
        MinWidth = 580;
        MinHeight = 500;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Header(
            "Reglas del demonio por juego",
            "El demonio (ProcessLassoNativeService) aplica estas reglas cada 500 ms mientras ApexKernel esté abierto. Con una regla activa se ajustan afinidad y prioridad; al cerrar el juego, no se toca nada.",
            "#21 · #22"));

        var filterRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        filterRow.Children.Add(new TextBlock
        {
            Text = "Buscar regla:",
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

        _rules.SelectionChanged += (_, _) => LoadSelectedRule();
        panel.Children.Add(_rules);

        panel.Children.Add(new TextBlock
        {
            Text = "EJECUTABLE DEL JUEGO",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 14, 0, 4)
        });
        _exeInput.Padding = new Thickness(10, 7, 10, 7);
        panel.Children.Add(_exeInput);

        panel.Children.Add(new TextBlock
        {
            Text = "Aplicar al ejecutable seleccionado",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 14, 0, 6)
        });
        var checks = new WrapPanel();
        foreach (CheckBox check in new[] { _excludeCore0, _physicalOnly, _highPriority, _lowPriority })
        {
            check.Margin = new Thickness(0, 0, 18, 4);
            check.Foreground = B("TextBrush", "#F2F5F7");
            checks.Children.Add(check);
        }
        panel.Children.Add(checks);

        // Prioridad alta y baja son excluyentes: al marcar una se desmarca la otra.
        _highPriority.Checked += (_, _) => MakePrioritiesExclusive(_lowPriority);
        _lowPriority.Checked += (_, _) => MakePrioritiesExclusive(_highPriority);

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Button("Añadir regla", (_, _) => AddRule()));
        buttons.Children.Add(Button("Guardar cambios", (_, _) => SaveSelected()));
        buttons.Children.Add(Button("Eliminar regla", (_, _) => DeleteSelected(), danger: true));
        panel.Children.Add(buttons);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        panel.Children.Add(_status);
        Content = panel;
        Reload();
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

    private static Button Button(string label, RoutedEventHandler handler, bool danger = false)
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
        button.Click += handler;
        return button;
    }

    private void SetStatus(string text, bool ok = true)
    {
        _status.Text = text;
        _status.Foreground = ok ? B("MutedBrush", "#9DA8B3") : B("DangerBrush", "#EF4444");
    }

    private void ApplyFilter()
    {
        string query = _search.Text.Trim();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_rules.Items);
        view.Filter = value =>
        {
            if (value is not RuleEntry rule) return false;
            return query.Length == 0 || rule.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
        };
        _count.Text = $"{view.Cast<object>().Count()}/{_rules.Items.Count} visibles";
    }

    private void Reload()
    {
        DaemonConfigFile config = DaemonConfigStore.Load();
        _rules.Items.Clear();
        foreach (DaemonRule rule in config.Rules)
            _rules.Items.Add(new RuleEntry(rule));
        ApplyFilter();
        SetStatus($"{config.Rules.Count} reglas activas · el demonio las aplica en tiempo real mientras ApexKernel esté abierto.");
    }

    private void AddRule()
    {
        string exe = _exeInput.Text.Trim();
        if (exe.Length == 0) { SetStatus("Escribe el ejecutable del juego antes de añadir la regla.", ok: false); return; }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        DaemonConfigFile config = DaemonConfigStore.Load();
        if (config.Rules.Any(rule => string.Equals(rule.Exe, exe, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"Ya existe una regla para {exe}.", ok: false);
            return;
        }
        var rule = new DaemonRule { Exe = exe };
        ReadChecks(rule);
        config.Rules.Add(rule);
        DaemonConfigStore.Save(config);
        _exeInput.Clear();
        Reload();
        SetStatus($"Regla añadida para {exe} · {DescribeFlags(rule)}.");
    }

    private void LoadSelectedRule()
    {
        if (_rules.SelectedItem is not RuleEntry entry) return;
        _syncingPriority = true;
        try
        {
            _exeInput.Text = entry.Rule.Exe;
            _excludeCore0.IsChecked = entry.Rule.ExcludeCore0;
            _physicalOnly.IsChecked = entry.Rule.PhysicalCoresOnly;
            _highPriority.IsChecked = entry.Rule.HighPriority;
            _lowPriority.IsChecked = entry.Rule.LowPriority;
            if (entry.Rule.HighPriority && entry.Rule.LowPriority) _lowPriority.IsChecked = false;
        }
        finally { _syncingPriority = false; }
    }

    private void MakePrioritiesExclusive(CheckBox other)
    {
        if (_syncingPriority) return;
        _syncingPriority = true;
        try { other.IsChecked = false; }
        finally { _syncingPriority = false; }
    }

    private void ReadChecks(DaemonRule rule)
    {
        rule.ExcludeCore0 = _excludeCore0.IsChecked == true;
        rule.PhysicalCoresOnly = _physicalOnly.IsChecked == true;
        rule.HighPriority = _highPriority.IsChecked == true;
        rule.LowPriority = _lowPriority.IsChecked == true;
    }

    private void SaveSelected()
    {
        if (_rules.SelectedItem is not RuleEntry entry) { SetStatus("Selecciona una regla para guardar cambios.", ok: false); return; }
        string exe = _exeInput.Text.Trim();
        if (exe.Length > 0 && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        DaemonConfigFile config = DaemonConfigStore.Load();
        DaemonRule? rule = config.Rules.FirstOrDefault(r => string.Equals(r.Exe, entry.Rule.Exe, StringComparison.OrdinalIgnoreCase));
        if (rule is null) { SetStatus("La regla ya no existe; actualiza la lista.", ok: false); Reload(); return; }
        rule.Exe = exe.Length > 0 ? exe : rule.Exe;
        ReadChecks(rule);
        DaemonConfigStore.Save(config);
        Reload();
        SetStatus($"Regla actualizada para {rule.Exe} · {DescribeFlags(rule)}.");
    }

    private void DeleteSelected()
    {
        if (_rules.SelectedItem is not RuleEntry entry) { SetStatus("Selecciona una regla para eliminarla.", ok: false); return; }
        if (MessageBox.Show(this,
                $"¿Eliminar la regla de '{entry.Rule.Exe}'?\n\nEl demonio dejará de aplicar su afinidad y prioridad. Esta acción no se puede deshacer.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        DaemonConfigFile config = DaemonConfigStore.Load();
        config.Rules.RemoveAll(r => string.Equals(r.Exe, entry.Rule.Exe, StringComparison.OrdinalIgnoreCase));
        DaemonConfigStore.Save(config);
        Reload();
        SetStatus($"Regla eliminada: {entry.Rule.Exe}.");
    }

    private static string DescribeFlags(DaemonRule rule)
    {
        var parts = new List<string>();
        if (rule.ExcludeCore0) parts.Add("sin núcleo 0");
        if (rule.PhysicalCoresOnly) parts.Add("solo físicos");
        if (rule.HighPriority) parts.Add("prioridad alta");
        if (rule.LowPriority) parts.Add("prioridad baja");
        return parts.Count == 0 ? "sin flags" : string.Join(", ", parts);
    }

    private sealed class RuleEntry(DaemonRule rule)
    {
        public DaemonRule Rule { get; } = rule;

        public override string ToString() =>
            $"{Rule.Exe,-28} — {DescribeFlags(Rule)}";
    }
}
