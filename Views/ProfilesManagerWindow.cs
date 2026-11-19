using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApexKernelOptimizer.ViewModels;

namespace ApexKernelOptimizer.Views;

public sealed class ProfilesManagerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TextBlock _status;

    public ProfilesManagerWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        Title = "Perfiles de ApexKernel";
        Width = 560;
        Height = 430;
        MinWidth = 480;
        MinHeight = 380;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Header(
            "Perfiles de ApexKernel",
            "Importa o exporta un archivo .optprofile con los ajustes implementados y activos. Las opciones no disponibles en este equipo se omiten al importar."));

        panel.Children.Add(ActionCard(
            "Importar perfil",
            "Aplica los ajustes guardados en un archivo .optprofile o .json. Revisa después la consola de actividad para ver los ajustes omitidos o fallidos.",
            "Importar perfil .optprofile",
            () => ImportAsync(),
            B("AccentBrush", "#22C55E")));

        panel.Children.Add(ActionCard(
            "Exportar perfil",
            "Guarda el estado actual de los ajustes activos para poder restaurarlo más tarde o llevarlo a otro equipo con ApexKernel.",
            "Exportar perfil .optprofile",
            () => ExportAsync(),
            B("PanelAltBrush", "#20262E")));

        _status = new TextBlock
        {
            Text = "Selecciona una operación para comenzar.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 16, 0, 0)
        };
        panel.Children.Add(_status);
        Content = panel;
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

    private static Border ActionCard(string title, string description, string buttonText, Func<Task> action, Brush accent)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = B("TextBrush", "#F2F5F7")
        });
        body.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 4, 0, 10)
        });
        var button = new Button
        {
            Content = buttonText,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 8, 14, 8),
            Background = accent,
            BorderBrush = accent,
            Foreground = B("TextBrush", "#F2F5F7")
        };
        button.Click += async (_, _) => await action();
        body.Children.Add(button);
        return new Border
        {
            Background = B("PanelBrush", "#191E24"),
            BorderBrush = B("BorderBrush", "#323C46"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = body
        };
    }

    private void SetStatus(string text, bool ok = true)
    {
        _status.Text = text;
        _status.Foreground = ok ? B("MutedBrush", "#9DA8B3") : B("DangerBrush", "#EF4444");
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar perfil de optimización",
            Filter = "Perfil ApexKernel (*.optprofile;*.json)|*.optprofile;*.json|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this,
                $"Se aplicarán los ajustes del perfil '{System.IO.Path.GetFileName(dialog.FileName)}'.\n\n" +
                "Algunos ajustes requieren administrador y otros pueden necesitar reiniciar la sesión. ¿Continuar?",
                "Confirmar importación de perfil", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _viewModel.ImportProfileAsync(dialog.FileName);
            SetStatus($"Perfil '{System.IO.Path.GetFileName(dialog.FileName)}' importado. Revisa la consola de actividad para ver los ajustes omitidos o fallidos.");
        }
        catch (Exception ex) { SetStatus($"No se pudo importar el perfil: {ex.Message}", ok: false); }
    }

    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar perfil de optimización",
            Filter = "Perfil ApexKernel (*.optprofile)|*.optprofile|Archivo JSON (*.json)|*.json",
            DefaultExt = ".optprofile",
            AddExtension = true,
            FileName = "ApexKernel-Profile"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await _viewModel.ExportProfileAsync(dialog.FileName);
            SetStatus($"Perfil exportado a '{dialog.FileName}'.");
        }
        catch (Exception ex) { SetStatus($"No se pudo exportar el perfil: {ex.Message}", ok: false); }
    }
}
