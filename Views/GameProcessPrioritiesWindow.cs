using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ApexKernelOptimizer.Core.Engine;
using ApexKernelOptimizer.ViewModels;

namespace ApexKernelOptimizer.Views;

public sealed class GameProcessPrioritiesWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TextBox _path = new() { MinWidth = 400 };
    private readonly TextBlock[] _states = new TextBlock[3];
    private readonly Button[] _restoreButtons = new Button[3];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private static readonly (int Id, string Name, string Desired, string Description)[] Options =
    [
        (18, "CPU · prioridad alta", "CpuPriorityClass=3", "Prioridad de planificación alta"),
        (19, "E/S · prioridad alta", "IoPriority=3", "Prioridad de E/S alta"),
        (20, "Memoria · páginas muy altas", "PagePriority=5", "Prioridad de memoria muy alta")
    ];

    public GameProcessPrioritiesWindow(MainViewModel viewModel, int selectedOptimizationId)
    {
        _viewModel = viewModel;
        Title = "Prioridades persistentes por juego";
        Width = 860;
        Height = 580;
        MinWidth = 700;
        MinHeight = 480;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel();
        string badge = selectedOptimizationId is 18 or 19 or 20 ? $"#{selectedOptimizationId}" : "#18 · #19 · #20";
        header.Children.Add(Header(
            "Prioridades persistentes por juego",
            "Selecciona el .exe del juego. Windows guarda estas reglas IFEO por nombre de archivo, así que también afectan a otras copias del mismo nombre en otra carpeta. Aplicar o restaurar requiere ejecutar ApexKernel como administrador.",
            badge));

        var pathRow = new WrapPanel();
        pathRow.Children.Add(new TextBlock
        {
            Text = "Juego (.exe):",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = B("MutedBrush", "#9DA8B3")
        });
        pathRow.Children.Add(_path);
        var browse = new Button { Content = "Buscar juego .exe…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        browse.Click += (_, _) => BrowseExecutable();
        pathRow.Children.Add(browse);
        var refresh = new Button { Content = "Actualizar estados", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        refresh.Click += async (_, _) => await RefreshAsync();
        pathRow.Children.Add(refresh);
        header.Children.Add(pathRow);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var rows = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < Options.Length; i++)
        {
            int index = i;
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = Options[i].Name, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush", "#F2F5F7") },
                    new TextBlock
                    {
                        Text = $"{Options[i].Desired} · {Options[i].Description} (#{Options[i].Id})",
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = B("MutedBrush", "#9DA8B3"),
                        Margin = new Thickness(0, 2, 0, 0)
                    }
                }
            });
            _states[i] = new TextBlock
            {
                Text = "Selecciona un juego para consultar.",
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = B("MutedBrush", "#9DA8B3"),
                Margin = new Thickness(8, 0, 10, 0)
            };
            Grid.SetColumn(_states[i], 1);
            grid.Children.Add(_states[i]);
            var apply = new Button
            {
                Content = "Aplicar",
                Margin = new Thickness(4, 0, 4, 0),
                Padding = new Thickness(12, 7, 12, 7),
                Tag = index,
                Background = B("PanelAltBrush", "#20262E"),
                BorderBrush = B("AccentBrush", "#22C55E")
            };
            apply.Click += async (_, _) => await SetAsync(index, true);
            Grid.SetColumn(apply, 2);
            grid.Children.Add(apply);
            _restoreButtons[i] = new Button
            {
                Content = "Restaurar",
                IsEnabled = false,
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(12, 7, 12, 7),
                Tag = index
            };
            _restoreButtons[i].Click += async (_, _) => await SetAsync(index, false);
            Grid.SetColumn(_restoreButtons[i], 3);
            grid.Children.Add(_restoreButtons[i]);
            rows.Children.Add(new Border
            {
                Background = B("PanelBrush", "#191E24"),
                BorderBrush = B("BorderBrush", "#323C46"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Child = grid
            });
        }
        root.Children.Add(rows);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        Content = root;
        Loaded += async (_, _) =>
        {
            if (selectedOptimizationId is 18 or 19 or 20)
                SetStatus($"Ajuste #{selectedOptimizationId} seleccionado. Revisa el nombre de archivo y confirma el alcance global por nombre antes de aplicar.");
            if (!string.IsNullOrWhiteSpace(_path.Text)) await RefreshAsync();
        };
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
            Margin = new Thickness(0, 0, 0, 14),
            Child = grid
        };
    }

    private void SetStatus(string text, bool ok = true)
    {
        _status.Text = text;
        _status.Foreground = ok ? B("MutedBrush", "#9DA8B3") : B("DangerBrush", "#EF4444");
    }

    private void BrowseExecutable()
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar ejecutable del juego", Filter = "Ejecutable (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true) { _path.Text = dialog.FileName; _ = RefreshAsync(); }
    }

    private async Task SetAsync(int index, bool enabled)
    {
        if (!File.Exists(_path.Text)) { SetStatus("Selecciona primero un archivo .exe existente.", ok: false); return; }
        if (enabled)
        {
            MessageBoxResult confirmation = MessageBox.Show(this,
                $"Se aplicará {Options[index].Desired} (#{Options[index].Id}) para {Path.GetFileName(_path.Text)} en IFEO. " +
                "Windows usa el nombre del ejecutable; la regla también alcanza otras copias con ese mismo nombre. ¿Continuar?",
                "Confirmar regla por ejecutable", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;
        }
        ExecutionResult result = await _viewModel.SetGamePriorityAsync(_path.Text, Options[index].Id, enabled);
        SetStatus(result.Message, ok: result.Success);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!File.Exists(_path.Text)) return;
        try
        {
            IReadOnlyList<GamePriorityStatus> values = await _viewModel.GetGamePriorityStatusAsync(_path.Text);
            for (int i = 0; i < Options.Length; i++)
            {
                GamePriorityStatus value = values.First(item => item.OptimizationId == Options[i].Id);
                string current = value.CurrentValue is null ? "sin valor" : $"actual={value.CurrentValue}";
                string state = value.HasBackup ? "respaldo ApexKernel disponible" : "sin respaldo";
                _states[i].Text = $"{current}\n{state}";
                _states[i].Foreground = value.CurrentValue is null
                    ? B("MutedBrush", "#9DA8B3")
                    : B("WarningBrush", "#F59E0B");
                _restoreButtons[i].IsEnabled = value.HasBackup;
            }
            if (string.IsNullOrWhiteSpace(_status.Text))
                SetStatus($"Consulta completada para {Path.GetFileName(_path.Text)}. Aplicar guarda cada valor original antes de cambiarlo.");
        }
        catch (Exception ex) { SetStatus($"No se pudo consultar IFEO: {ex.Message}", ok: false); }
    }
}
