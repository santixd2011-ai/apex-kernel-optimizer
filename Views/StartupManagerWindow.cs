using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApexKernelOptimizer.Core.Engine;
using ApexKernelOptimizer.ViewModels;

namespace ApexKernelOptimizer.Views;

/// <summary>
/// Gestor de programas de arranque (#152): entradas Run/RunOnce/RunWOW64 y carpetas de Inicio
/// del usuario y de Todos los usuarios, con activar/desactivar vía StartupApproved.
/// </summary>
public sealed class StartupManagerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ObservableCollection<StartupRow> _entries = [];
    private readonly ListBox _list = new() { Height = 340, FontFamily = new FontFamily("Consolas") };
    private readonly TextBox _search = new() { Width = 260 };
    private readonly TextBlock _count;
    private readonly TextBlock _detail;
    private readonly TextBlock _status;

    public StartupManagerWindow(MainViewModel viewModel, int toolId)
    {
        _viewModel = viewModel;
        Title = $"#{toolId} · Gestor de programas de arranque";
        Width = 900;
        Height = 680;
        MinWidth = 720;
        MinHeight = 540;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Header(
            "Programas de arranque",
            "Entradas Run/RunOnce/RunWOW64 y carpetas de Inicio del usuario y de Todos los usuarios. Desactivar no borra la entrada: usa el estado aprobado de StartupApproved para revertirla.",
            $"#{toolId}"));

        var filterRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        filterRow.Children.Add(new TextBlock
        {
            Text = "Buscar:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = B("MutedBrush", "#9DA8B3")
        });
        _search.TextChanged += (_, _) => ApplyFilter();
        filterRow.Children.Add(_search);
        filterRow.Children.Add(MakeButton("Actualizar", ReloadAsync, Margin: new Thickness(12, 0, 0, 0)));
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

        panel.Children.Add(new TextBlock
        {
            Text = "TIPO          NOMBRE                              ESTADO        UBICACIÓN",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = B("DimBrush", "#71808D"),
            Margin = new Thickness(2, 0, 0, 4)
        });
        _list.ItemsSource = _entries;
        _list.SelectionChanged += (_, _) => UpdateDetail();
        _list.MouseDoubleClick += async (_, _) => await SetEnabledAsync(false);
        panel.Children.Add(_list);

        _detail = new TextBlock
        {
            Text = "Selecciona una entrada para ver su detalle.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(_detail);

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(MakeButton("Activar seleccionado", () => SetEnabledAsync(true)));
        buttons.Children.Add(MakeButton("Desactivar seleccionado", () => SetEnabledAsync(false), danger: true));
        buttons.Children.Add(new TextBlock
        {
            Text = "Doble clic en una fila desactiva esa entrada. Los cambios son reversibles.",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = B("DimBrush", "#71808D"),
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11
        });
        panel.Children.Add(buttons);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        panel.Children.Add(_status);
        Content = panel;
        _ = ReloadAsync();
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

    private static Button MakeButton(string label, Func<Task> action, bool danger = false, Thickness? Margin = null)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 7, 12, 7),
            Margin = Margin ?? new Thickness(0, 0, 8, 0),
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
        if (_list.SelectedItem is not StartupRow row)
        {
            _detail.Text = "Selecciona una entrada para ver su detalle.";
            return;
        }
        StartupEntry e = row.Entry;
        _detail.Text = $"Detalle: {e.Name} · tipo: {e.Kind} · ubicación: {e.Location} · estado: {(e.Enabled ? "activada" : "desactivada")}\n" +
                       $"Comando: {e.Command}";
    }

    private async Task ReloadAsync()
    {
        SetStatus("Consultando entradas de arranque…");
        try
        {
            IReadOnlyList<StartupEntry> entries = await _viewModel.ListStartupEntriesAsync();
            _entries.Clear();
            foreach (StartupEntry entry in entries) _entries.Add(new StartupRow(entry));
            ApplyFilter();
            SetStatus($"{entries.Count} entradas de arranque · {entries.Count(entry => entry.Enabled)} activas · " +
                      $"{entries.Count(entry => !entry.Enabled)} desactivadas.");
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo listar el arranque: {ex.Message}", ok: false);
        }
    }

    private void ApplyFilter()
    {
        string query = _search.Text.Trim();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_entries);
        view.Filter = value =>
        {
            if (value is not StartupRow row) return false;
            if (query.Length == 0) return true;
            StartupEntry e = row.Entry;
            return e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Command.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Location.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Kind.Contains(query, StringComparison.OrdinalIgnoreCase);
        };
        _count.Text = $"{view.Cast<object>().Count()}/{_entries.Count} visibles";
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        if (_list.SelectedItem is not StartupRow row) { SetStatus("Selecciona una entrada de la lista.", ok: false); return; }
        StartupEntry entry = row.Entry;
        if (enabled == entry.Enabled)
        {
            SetStatus($"'{entry.Name}' ya está {(enabled ? "activada" : "desactivada")}.");
            return;
        }
        SetStatus($"{(enabled ? "Activando" : "Desactivando")} '{entry.Name}'…");
        string message;
        try
        {
            ExecutionResult result = await _viewModel.SetStartupEntryEnabledAsync(entry, enabled);
            message = result.Message;
        }
        catch (Exception ex) { message = $"Error: {ex.Message}"; }
        await ReloadAsync();
        foreach (StartupRow reloaded in _entries)
            if (reloaded.Entry.Name == entry.Name && reloaded.Entry.Kind == entry.Kind) { _list.SelectedItem = reloaded; break; }
        SetStatus(message);
    }

    private sealed record StartupRow(StartupEntry Entry)
    {
        public override string ToString() =>
            $"{Entry.Kind,-12} {Entry.Name,-34} {(Entry.Enabled ? "ACTIVA" : "DESACTIVADA"),-12} {Entry.Location}";
    }
}
