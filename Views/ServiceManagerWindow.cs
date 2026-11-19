using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApexKernelOptimizer.Core.Engine;
using ApexKernelOptimizer.ViewModels;

namespace ApexKernelOptimizer.Views;

/// <summary>
/// Gestor de servicios (#105 catalogado y #205 solo software de terceros):
/// lista servicios gestionados con filtro por categoría, búsqueda y botones de modo.
/// </summary>
public sealed class ServiceManagerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly bool _thirdPartyOnly;
    private readonly int _toolId;
    private readonly ObservableCollection<ServiceRow> _entries = [];
    private readonly ListBox _list = new() { Height = 320, FontFamily = new FontFamily("Consolas") };
    private readonly ComboBox _category = new() { MinWidth = 210 };
    private readonly TextBox _search = new() { Width = 230 };
    private readonly TextBlock _count;
    private readonly TextBlock _detail;
    private readonly TextBlock _status;

    public ServiceManagerWindow(MainViewModel viewModel, bool thirdPartyOnly, int toolId)
    {
        _viewModel = viewModel;
        _thirdPartyOnly = thirdPartyOnly;
        _toolId = toolId;
        Title = $"#{toolId} · Gestor de servicios" + (thirdPartyOnly ? " (solo terceros)" : "");
        Width = 920;
        Height = 700;
        MinWidth = 720;
        MinHeight = 540;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Header(
            "Gestor de servicios",
            thirdPartyOnly
                ? "Servicios instalados por software de terceros (drivers y utilidades ajenas a Windows). Cambia el modo con cuidado: algunos drivers pueden dejar de funcionar."
                : "Servicios gestionados por ApexKernel. El estado previo se respalda antes de cambiar el modo, para poder restaurarlo.",
            $"#{toolId} · {(thirdPartyOnly ? "Terceros" : "Sistema")}"));

        var filterRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        filterRow.Children.Add(new TextBlock
        {
            Text = "Categoría:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = B("MutedBrush", "#9DA8B3")
        });
        _category.SelectionChanged += (_, _) => ApplyFilter();
        filterRow.Children.Add(_category);
        filterRow.Children.Add(new TextBlock
        {
            Text = "Buscar:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 8, 0),
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
            Text = "NOMBRE VISIBLE                      NOMBRE INTERNO                 MODO            CATEGORÍA",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = B("DimBrush", "#71808D"),
            Margin = new Thickness(2, 0, 0, 4)
        });
        _list.ItemsSource = _entries;
        _list.SelectionChanged += (_, _) => UpdateDetail();
        panel.Children.Add(_list);

        _detail = new TextBlock
        {
            Text = "Selecciona un servicio para ver su detalle.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(_detail);

        panel.Children.Add(new TextBlock
        {
            Text = "MODOS · Automático: arranca con Windows. Manual: solo cuando se necesita. Deshabilitado: no arranca nunca. Restaurar: vuelve al estado previo respaldado.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 12, 0, 4)
        });

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(MakeButton("Automático", () => SetModeAsync("Automático")));
        buttons.Children.Add(MakeButton("Manual", () => SetModeAsync("Manual")));
        buttons.Children.Add(MakeButton("Deshabilitado", () => SetModeAsync("Deshabilitado"), danger: true));
        buttons.Children.Add(MakeButton("Restaurar original", () => SetModeAsync("Restaurar")));
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
        if (_list.SelectedItem is not ServiceRow row)
        {
            _detail.Text = "Selecciona un servicio para ver su detalle.";
            return;
        }
        ServiceManagerEntry e = row.Entry;
        _detail.Text = $"Detalle: {e.DisplayName} · nombre interno: {e.Name} · modo actual: {e.Mode} · categoría: {e.Category} · " +
                       $"{(e.Exists ? "instalado" : "no instalado")}{(e.HasBackup ? " · respaldo disponible" : "")}.";
    }

    private async Task ReloadAsync()
    {
        SetStatus("Consultando servicios…");
        try
        {
            IReadOnlyList<ServiceManagerEntry> entries = await _viewModel.ListServiceManagerEntriesAsync(_thirdPartyOnly);
            _entries.Clear();
            foreach (ServiceManagerEntry entry in entries) _entries.Add(new ServiceRow(entry));
            var categories = entries.Select(entry => entry.Category).Distinct().OrderBy(name => name).ToList();
            string? previous = (_category.SelectedItem as ComboBoxItem)?.Content as string;
            _category.Items.Clear();
            _category.Items.Add(new ComboBoxItem { Content = "Todas las categorías" });
            foreach (string category in categories) _category.Items.Add(new ComboBoxItem { Content = category });
            _category.SelectedIndex = 0;
            if (previous is not null)
            {
                foreach (ComboBoxItem item in _category.Items)
                    if ((string?)item.Content == previous) { _category.SelectedItem = item; break; }
            }
            ApplyFilter();
            SetStatus($"{entries.Count} servicios listados · {entries.Count(e => e.Exists)} instalados · " +
                      $"{entries.Count(e => e.HasBackup)} con respaldo disponible.");
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo listar los servicios: {ex.Message}", ok: false);
        }
    }

    private void ApplyFilter()
    {
        string? selected = (_category.SelectedItem as ComboBoxItem)?.Content as string;
        string query = _search.Text.Trim();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_entries);
        view.Filter = value =>
        {
            if (value is not ServiceRow row) return false;
            ServiceManagerEntry e = row.Entry;
            if (selected is not null && selected != "Todas las categorías" && e.Category != selected) return false;
            if (query.Length == 0) return true;
            return e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Category.Contains(query, StringComparison.OrdinalIgnoreCase);
        };
        _count.Text = $"{view.Cast<object>().Count()}/{_entries.Count} visibles";
    }

    private async Task SetModeAsync(string mode)
    {
        if (_list.SelectedItem is not ServiceRow row) { SetStatus("Selecciona un servicio de la lista.", ok: false); return; }
        ServiceManagerEntry entry = row.Entry;
        if (!entry.Exists) { SetStatus($"{entry.Name}: el servicio no está instalado.", ok: false); return; }
        if (mode == "Deshabilitado" && MessageBox.Show(this,
                $"¿Deshabilitar '{entry.DisplayName}' ({entry.Name})?\n\nSe respalda el estado actual para poder restaurarlo después.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetStatus($"Aplicando modo {mode} a {entry.Name}…");
        string message;
        try
        {
            ExecutionResult result = await _viewModel.SetServiceModeAsync(entry.Name, mode);
            message = result.Message;
        }
        catch (Exception ex) { message = $"Error: {ex.Message}"; }
        await ReloadAsync();
        foreach (ServiceRow reloaded in _entries)
            if (reloaded.Entry.Name == entry.Name) { _list.SelectedItem = reloaded; break; }
        SetStatus(message);
    }

    private sealed record ServiceRow(ServiceManagerEntry Entry)
    {
        public override string ToString() =>
            $"{Entry.DisplayName,-34} {Entry.Name,-30} {Entry.Mode,-14} {Entry.Category,-18} {(Entry.Exists ? "instalado" : "ausente")}{(Entry.HasBackup ? " · respaldo" : "")}";
    }
}
