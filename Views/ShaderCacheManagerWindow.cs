using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApexKernelOptimizer.Core.Engine;
using ApexKernelOptimizer.ViewModels;

namespace ApexKernelOptimizer.Views;

public sealed class ShaderCacheManagerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _backupInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _restore = new()
    {
        Content = "Restaurar archivos respaldados",
        IsEnabled = false,
        Margin = new Thickness(8, 0, 0, 0),
        Padding = new Thickness(12, 7, 12, 7)
    };

    public ShaderCacheManagerWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Caché de sombreadores DirectX / NVIDIA";
        Width = 660;
        Height = 460;
        MinWidth = 560;
        MinHeight = 380;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Header(
            "Caché de sombreadores",
            "Mueve los archivos de D3DSCache, NVIDIA\\DXCache y NVIDIA\\GLCache a una copia recuperable. Cierra juegos y aplicaciones gráficas antes de limpiar; los archivos ocupados quedan en su sitio."));

        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var clean = new Button
        {
            Content = "Mover caché a respaldo",
            Padding = new Thickness(12, 7, 12, 7),
            Background = B("YellowSoftBrush", "#3A321F"),
            BorderBrush = B("WarningBrush", "#F59E0B"),
            Foreground = B("WarningBrush", "#F59E0B")
        };
        clean.Click += async (_, _) => await CleanAsync();
        _restore.Click += async (_, _) => await RestoreAsync();
        var refresh = new Button { Content = "Actualizar estado", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        refresh.Click += async (_, _) => await RefreshAsync();
        buttons.Children.Add(clean);
        buttons.Children.Add(_restore);
        buttons.Children.Add(refresh);
        root.Children.Add(buttons);

        root.Children.Add(new Border
        {
            Background = B("PanelBrush", "#191E24"),
            BorderBrush = B("BorderBrush", "#323C46"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 14, 0, 0),
            Child = _backupInfo
        });
        root.Children.Add(_status);
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
            Margin = new Thickness(0, 0, 0, 16),
            Child = grid
        };
    }

    private void SetStatus(string text, bool ok = true)
    {
        _status.Text = text;
        _status.Foreground = ok ? B("MutedBrush", "#9DA8B3") : B("DangerBrush", "#EF4444");
    }

    private async Task CleanAsync()
    {
        if (MessageBox.Show(this,
                "¿Mover ahora los archivos de caché de DirectX y NVIDIA a la copia de seguridad?\n\n" +
                "Cierra primero los juegos y aplicaciones gráficas. Los archivos en uso no se moverán. Podrás restaurarlos desde esta ventana.",
                "Confirmar limpieza de caché", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            ExecutionResult result = await _viewModel.QuarantineShaderCachesAsync();
            SetStatus(result.Message, ok: result.Success);
            await RefreshAsync();
        }
        catch (Exception ex) { SetStatus($"No se pudo mover la caché: {ex.Message}", ok: false); }
    }

    private async Task RestoreAsync()
    {
        try
        {
            ExecutionResult result = await _viewModel.RestoreShaderCachesAsync();
            SetStatus(result.Message, ok: result.Success);
            await RefreshAsync();
        }
        catch (Exception ex) { SetStatus($"No se pudo restaurar la caché: {ex.Message}", ok: false); }
    }

    private async Task RefreshAsync()
    {
        try
        {
            ShaderCacheStatus status = await _viewModel.GetShaderCacheStatusAsync();
            _restore.IsEnabled = status.BackupFileCount > 0;
            _backupInfo.Text = status.BackupFileCount == 0
                ? "Estado del respaldo: no hay archivos de caché respaldados actualmente."
                : $"Respaldo pendiente: {status.BackupFileCount} archivos · {status.BackupBytes / 1048576d:0.0} MB.\nUbicación: {status.BackupRoot}";
            _backupInfo.Foreground = status.BackupFileCount == 0
                ? B("MutedBrush", "#9DA8B3")
                : B("WarningBrush", "#F59E0B");
        }
        catch (Exception ex)
        {
            _backupInfo.Text = $"No se pudo consultar el respaldo: {ex.Message}";
            _backupInfo.Foreground = B("DangerBrush", "#EF4444");
        }
    }
}
