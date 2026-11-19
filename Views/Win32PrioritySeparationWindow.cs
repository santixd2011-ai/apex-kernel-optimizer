using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApexKernelOptimizer.Views;

public sealed class Win32PrioritySeparationWindow : Window
{
    private readonly ComboBox _profiles = new();
    private readonly TextBlock _detail;
    public int SelectedValue { get; private set; } = 36;

    public Win32PrioritySeparationWindow()
    {
        Title = "Perfil de planificación de CPU";
        Width = 520;
        Height = 400;
        ResizeMode = ResizeMode.NoResize;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(Header(
            "Planificación de CPU",
            "Elige el perfil global de Win32PrioritySeparation. Windows puede ignorar o reinterpretar estos valores según la versión del sistema y la carga."));

        stack.Children.Add(new TextBlock
        {
            Text = "PERFIL DE PLANIFICACIÓN",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 2, 0, 6)
        });
        _profiles.Items.Add(new PriorityOption(36, "Máximos FPS (36)", "Favorece intervalos cortos y respuestas rápidas; suele ser el perfil preferido para juegos."));
        _profiles.Items.Add(new PriorityOption(40, "Menor input lag (40)", "Prioriza la capacidad de respuesta de primer plano frente al rendimiento sostenido en segundo plano."));
        _profiles.Items.Add(new PriorityOption(38, "Equilibrado (38)", "Punto medio entre respuesta de primer plano y rendimiento de fondo."));
        _profiles.SelectedIndex = 0;
        stack.Children.Add(_profiles);

        _detail = new TextBlock
        {
            Text = "",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 10, 0, 0)
        };
        stack.Children.Add(_detail);
        _profiles.SelectionChanged += (_, _) => UpdateDetail();

        stack.Children.Add(new Border
        {
            Background = B("PanelBrush", "#191E24"),
            BorderBrush = B("BorderBrush", "#323C46"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 14, 0, 0),
            Child = new TextBlock
            {
                Text = "El cambio afecta a todo el sistema y requiere administrador. Puede necesitarse reiniciar la sesión para notar el efecto.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = B("WarningBrush", "#F59E0B"),
                FontSize = 11
            }
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var apply = new Button
        {
            Content = "Aplicar perfil",
            MinWidth = 120,
            Padding = new Thickness(10, 7, 10, 7),
            IsDefault = true
        };
        apply.Click += (_, _) =>
        {
            if (_profiles.SelectedItem is not PriorityOption option) return;
            SelectedValue = option.Value;
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancelar", MinWidth = 100, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        Content = stack;
        UpdateDetail();
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

    private void UpdateDetail()
    {
        if (_profiles.SelectedItem is not PriorityOption option) return;
        _detail.Text = $"Valor {option.Value}: {option.Description}";
    }

    private sealed record PriorityOption(int Value, string Label, string Description)
    {
        public override string ToString() => Label;
    }
}
