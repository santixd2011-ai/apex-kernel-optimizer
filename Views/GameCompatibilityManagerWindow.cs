using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ApexKernelOptimizer.Core.Engine;
using ApexKernelOptimizer.ViewModels;

namespace ApexKernelOptimizer.Views;

public sealed class GameCompatibilityManagerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TextBox _path = new() { MinWidth = 400 };
    private readonly TextBox _search = new() { Width = 220 };
    private readonly ListBox _entries = new() { MinHeight = 250, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _count;
    private readonly TextBlock _detail;
    private readonly Button _restore = new()
    {
        Content = "Restaurar original",
        IsEnabled = false,
        Margin = new Thickness(8, 0, 0, 0),
        Padding = new Thickness(12, 7, 12, 7)
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    public GameCompatibilityManagerWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Compatibilidad FSO y DPI por juego";
        Width = 820;
        Height = 620;
        MinWidth = 660;
        MinHeight = 500;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel();
        header.Children.Add(Header(
            "Compatibilidad FSO y DPI",
            "Selecciona el .exe real del juego. Se aplican DISABLEDXMAXIMIZEDWINDOWEDMODE y HIGHDPIAWARE; esto puede cambiar la escala de la interfaz. Otras opciones AppCompat existentes se conservan."));

        var pathRow = new WrapPanel();
        pathRow.Children.Add(new TextBlock
        {
            Text = "Ejecutable:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = B("MutedBrush", "#9DA8B3")
        });
        pathRow.Children.Add(_path);
        var browse = new Button { Content = "Buscar .exe…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        browse.Click += (_, _) => BrowseExecutable();
        pathRow.Children.Add(browse);
        header.Children.Add(pathRow);

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        var apply = new Button
        {
            Content = "Aplicar FSO + DPI",
            Padding = new Thickness(12, 7, 12, 7),
            Background = B("PanelAltBrush", "#20262E"),
            BorderBrush = B("AccentBrush", "#22C55E")
        };
        apply.Click += async (_, _) => await ApplyAsync();
        var refresh = new Button { Content = "Actualizar lista", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        refresh.Click += async (_, _) => await RefreshAsync();
        _restore.Click += async (_, _) => await RestoreAsync();
        actions.Children.Add(apply);
        actions.Children.Add(refresh);
        actions.Children.Add(_restore);
        header.Children.Add(actions);

        var filterRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        filterRow.Children.Add(new TextBlock
        {
            Text = "Buscar en la lista:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = B("MutedBrush", "#9DA8B3"),
            FontSize = 11
        });
        _search.TextChanged += (_, _) => ApplyFilter();
        filterRow.Children.Add(_search);
        _count = new TextBlock
        {
            Text = "0/0 visibles",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = B("MutedBrush", "#9DA8B3"),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11
        };
        filterRow.Children.Add(_count);
        header.Children.Add(filterRow);

        _entries.SelectionChanged += (_, _) => { UpdateDetail(); _restore.IsEnabled = _entries.SelectedItem is CompatibilityListEntry { Entry.HasBackup: true }; };
        header.Children.Add(_detail = new TextBlock
        {
            Text = "Selecciona una entrada para ver su detalle.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0)
        });

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_entries);
        Content = root;
        Loaded += async (_, _) => await RefreshAsync();
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

    private void UpdateDetail()
    {
        if (_entries.SelectedItem is not CompatibilityListEntry item)
        {
            _detail.Text = "Selecciona una entrada para ver su detalle.";
            return;
        }
        GameCompatibilityEntry e = item.Entry;
        _detail.Text = $"Detalle: {e.ExePath} · valor: {e.Value} · {(e.HasBackup ? "respaldado por ApexKernel: se puede restaurar" : "sin respaldo de ApexKernel: solo consulta")}.";
    }

    private void ApplyFilter()
    {
        string query = _search.Text.Trim();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_entries.ItemsSource);
        if (view is null) return;
        view.Filter = value =>
        {
            if (value is not CompatibilityListEntry item) return false;
            return query.Length == 0 || item.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
        };
        _count.Text = $"{view.Cast<object>().Count()}/{_entries.Items.Count} visibles";
    }

    private void BrowseExecutable()
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar ejecutable real del juego", Filter = "Ejecutable (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true) _path.Text = dialog.FileName;
    }

    private async Task ApplyAsync()
    {
        if (string.IsNullOrWhiteSpace(_path.Text))
        {
            SetStatus("Selecciona primero el ejecutable del juego.", ok: false);
            return;
        }
        if (MessageBox.Show(this,
                $"¿Aplicar FSO + HIGHDPIAWARE a '{System.IO.Path.GetFileName(_path.Text)}'?\n\n" +
                "Puede cambiar la escala de la interfaz del juego. Se conservarán otras opciones AppCompat existentes.",
                "Confirmar compatibilidad", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ExecutionResult result = await _viewModel.SetGameCompatibilityAsync(_path.Text, enabled: true);
        SetStatus(result.Message, ok: result.Success);
        if (result.Success) await RefreshAsync();
    }

    private async Task RestoreAsync()
    {
        if (_entries.SelectedItem is not CompatibilityListEntry { Entry.HasBackup: true } selected) return;
        ExecutionResult result = await _viewModel.SetGameCompatibilityAsync(selected.Entry.ExePath, enabled: false);
        SetStatus(result.Message, ok: result.Success);
        if (result.Success) await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            IReadOnlyList<GameCompatibilityEntry> entries = await _viewModel.GetGameCompatibilityEntriesAsync();
            _entries.ItemsSource = entries.Select(entry => new CompatibilityListEntry(entry)).ToArray();
            _restore.IsEnabled = false;
            ApplyFilter();
            SetStatus(entries.Count == 0
                ? "No hay valores de compatibilidad FSO/DPI en HKCU."
                : $"{entries.Count} ejecutables · {entries.Count(e => e.HasBackup)} respaldados por ApexKernel (restaurables desde aquí).");
        }
        catch (Exception ex) { SetStatus($"No se pudo leer la lista: {ex.Message}", ok: false); }
    }

    private sealed record CompatibilityListEntry(GameCompatibilityEntry Entry)
    {
        public override string ToString() => $"{Entry.ExePath,-52} {Entry.Value,-40} {(Entry.HasBackup ? "respaldado por ApexKernel" : "sin respaldo; solo consulta")}";
    }
}
