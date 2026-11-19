using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ApexKernelOptimizer.Core.Engine;
using ApexKernelOptimizer.ViewModels;

namespace ApexKernelOptimizer.Views;

public sealed class DisplayRefreshManagerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ListBox _displays = new() { MinHeight = 190, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _detail = new()
    {
        Text = "Selecciona un monitor para ver sus modos.",
        TextWrapping = TextWrapping.Wrap,
        Foreground = B("MutedBrush", "#9DA8B3"),
        Margin = new Thickness(0, 6, 0, 0)
    };
    private readonly TextBlock _countdownLabel = new()
    {
        Text = "Sin vista previa activa.",
        FontSize = 11,
        Foreground = B("MutedBrush", "#9DA8B3")
    };
    private readonly ProgressBar _countdown = new() { Maximum = 15, Value = 0, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _apply = new() { Content = "Probar frecuencia máxima", Padding = new Thickness(12, 7, 12, 7) };
    private readonly Button _confirm = new() { Content = "Conservar modo", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
    private readonly Button _restore = new() { Content = "Restaurar frecuencia previa", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _previewDevice;
    private int _secondsLeft;
    private bool _allowClose;

    public DisplayRefreshManagerWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Frecuencia de refresco del monitor";
        Width = 780;
        Height = 560;
        MinWidth = 620;
        MinHeight = 440;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel();
        header.Children.Add(Header(
            "Frecuencia de refresco",
            "Enumera la frecuencia máxima a la resolución y profundidad de color actuales. El modo se prueba temporalmente: tienes 15 segundos para conservarlo o se restaura automáticamente."));
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        _apply.Click += async (_, _) => await ApplyAsync();
        _confirm.Click += async (_, _) => await ConfirmAsync();
        _restore.Click += async (_, _) => await RestoreAsync();
        var refresh = new Button { Content = "Actualizar", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        refresh.Click += async (_, _) => await RefreshAsync();
        buttons.Children.Add(_apply);
        buttons.Children.Add(_confirm);
        buttons.Children.Add(_restore);
        buttons.Children.Add(refresh);
        header.Children.Add(buttons);
        header.Children.Add(_countdownLabel);
        header.Children.Add(_countdown);
        header.Children.Add(_detail);
        _displays.SelectionChanged += (_, _) => UpdateActions();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_displays);
        Content = root;
        _timer.Tick += async (_, _) => await TickAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closing += OnClosing;
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

    private void SetStatus(string text, bool ok = true, bool warning = false)
    {
        _status.Text = text;
        _status.Foreground = warning
            ? B("WarningBrush", "#F59E0B")
            : ok ? B("MutedBrush", "#9DA8B3") : B("DangerBrush", "#EF4444");
    }

    private void UpdateDetail()
    {
        if (_displays.SelectedItem is not DisplayListEntry item)
        {
            _detail.Text = "Selecciona un monitor para ver sus modos.";
            return;
        }
        DisplayRefreshOption o = item.Option;
        _detail.Text = $"Detalle: {o.DisplayName} · dispositivo {o.DeviceName} · resolución actual {o.Width}×{o.Height} a {o.BitsPerPixel} bits · " +
                       $"frecuencia actual {o.CurrentHz} Hz / máxima enumerada {o.MaximumHz} Hz · " +
                       (o.HasBackup ? "hay una configuración previa respaldada que se puede restaurar." : "sin respaldo previo en esta sesión.");
    }

    private async Task ApplyAsync()
    {
        if (_displays.SelectedItem is not DisplayListEntry selected || selected.Option.HasBackup || selected.Option.MaximumHz <= selected.Option.CurrentHz) return;
        ExecutionResult result = await _viewModel.BeginDisplayRefreshPreviewAsync(selected.Option.DeviceName);
        SetStatus(result.Message, ok: result.Success);
        if (result.Success)
        {
            _previewDevice = selected.Option.DeviceName;
            _secondsLeft = 15;
            _countdown.Visibility = Visibility.Visible;
            _countdown.Value = 15;
            _countdownLabel.Text = "Vista previa activa · 15 s para confirmar";
            _countdownLabel.Foreground = B("WarningBrush", "#F59E0B");
            _confirm.IsEnabled = true;
            _timer.Start();
            UpdateActions();
        }
        await RefreshAsync(keepSelection: true);
    }

    private async Task ConfirmAsync()
    {
        if (_previewDevice is null) return;
        string device = _previewDevice;
        _timer.Stop();
        ExecutionResult result = await _viewModel.ConfirmDisplayRefreshPreviewAsync(device);
        SetStatus(result.Message, ok: result.Success);
        _previewDevice = null;
        _countdown.Visibility = Visibility.Collapsed;
        _countdownLabel.Text = "Sin vista previa activa.";
        _countdownLabel.Foreground = B("MutedBrush", "#9DA8B3");
        _confirm.IsEnabled = false;
        UpdateActions();
        await RefreshAsync();
    }

    private async Task RestoreAsync()
    {
        if (_displays.SelectedItem is not DisplayListEntry selected || !selected.Option.HasBackup) return;
        bool preview = string.Equals(_previewDevice, selected.Option.DeviceName, StringComparison.OrdinalIgnoreCase);
        ExecutionResult result = await _viewModel.RestoreDisplayRefreshAsync(selected.Option.DeviceName, onlyIfTargetStillCurrent: preview);
        SetStatus(result.Message, ok: result.Success);
        if (result.Success)
        {
            if (preview) _timer.Stop();
            _previewDevice = null;
            _countdown.Visibility = Visibility.Collapsed;
            _countdownLabel.Text = "Sin vista previa activa.";
            _countdownLabel.Foreground = B("MutedBrush", "#9DA8B3");
            _confirm.IsEnabled = false;
        }
        await RefreshAsync();
    }

    private async Task TickAsync()
    {
        if (_previewDevice is null) { _timer.Stop(); return; }
        _secondsLeft--;
        if (_secondsLeft > 0)
        {
            _countdown.Value = _secondsLeft;
            _countdownLabel.Text = $"Vista previa activa · confirma en {_secondsLeft} s";
            SetStatus($"Vista previa activa. Confirma en {_secondsLeft} s o el modo se restaurará automáticamente.", warning: true);
            return;
        }
        string device = _previewDevice;
        _timer.Stop();
        ExecutionResult result = await _viewModel.RestoreDisplayRefreshAsync(device, onlyIfTargetStillCurrent: true);
        SetStatus(result.Message, ok: result.Success);
        _previewDevice = null;
        _countdown.Visibility = Visibility.Collapsed;
        _countdownLabel.Text = "Sin vista previa activa.";
        _countdownLabel.Foreground = B("MutedBrush", "#9DA8B3");
        _confirm.IsEnabled = false;
        await RefreshAsync();
    }

    private async Task RefreshAsync(bool keepSelection = false)
    {
        try
        {
            string? selectedName = keepSelection && _displays.SelectedItem is DisplayListEntry current ? current.Option.DeviceName : null;
            IReadOnlyList<DisplayRefreshOption> options = await _viewModel.GetDisplayRefreshOptionsAsync();
            _displays.ItemsSource = options.Select(option => new DisplayListEntry(option)).ToArray();
            if (selectedName is not null)
                _displays.SelectedItem = _displays.Items.Cast<DisplayListEntry>().FirstOrDefault(item => item.Option.DeviceName.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
            UpdateActions();
            if (options.Count == 0) SetStatus("No se encontraron monitores activos con modos enumerables.");
            else SetStatus($"{options.Count} monitor(es) enumerados · selecciona uno para probar su frecuencia máxima.");
        }
        catch (Exception ex) { SetStatus($"No se pudieron enumerar los modos: {ex.Message}", ok: false); }
    }

    private void UpdateActions()
    {
        bool canEdit = _previewDevice is null && _displays.SelectedItem is DisplayListEntry;
        if (_displays.SelectedItem is DisplayListEntry selected)
        {
            _apply.IsEnabled = canEdit && !selected.Option.HasBackup && selected.Option.MaximumHz > selected.Option.CurrentHz;
            _restore.IsEnabled = _previewDevice is null && selected.Option.HasBackup;
            UpdateDetail();
        }
        else
        {
            _apply.IsEnabled = false;
            _restore.IsEnabled = false;
            _detail.Text = "Selecciona un monitor para ver sus modos.";
        }
        _confirm.IsEnabled = _previewDevice is not null;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || _previewDevice is null) return;
        e.Cancel = true;
        string device = _previewDevice;
        _timer.Stop();

        // La restauración usa la API síncrona de DisplayRefreshController bajo una lectura de respaldo
        // asíncrona: bombea el dispatcher y espera de forma síncrona para que el modo original quede
        // aplicado antes de permitir el cierre, sin bloquear las continuaciones en el hilo de UI.
        Task<ExecutionResult> restore = _viewModel.RestoreDisplayRefreshAsync(device, onlyIfTargetStillCurrent: true);
        var frame = new DispatcherFrame();
        _ = restore.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        ExecutionResult result;
        try { result = restore.GetAwaiter().GetResult(); }
        catch (Exception ex) { result = new ExecutionResult(false, $"No se pudo restaurar el modo original: {ex.Message}"); }

        SetStatus(result.Message, ok: result.Success);
        if (!result.Success) return;
        _previewDevice = null;
        _allowClose = true;
        Close();
    }

    private sealed record DisplayListEntry(DisplayRefreshOption Option)
    {
        public override string ToString() => $"{Option.DisplayName,-24} ({Option.DeviceName,-22}) {Option.Width}×{Option.Height}  {Option.CurrentHz,3} Hz actual / {Option.MaximumHz,3} Hz máximo" + (Option.HasBackup ? " · restauración disponible" : "");
    }
}
