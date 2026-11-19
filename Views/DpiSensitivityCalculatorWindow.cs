using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApexKernelOptimizer.Views;

public sealed class DpiSensitivityCalculatorWindow : Window
{
    private readonly TextBox _currentDpi = InputBox("800");
    private readonly TextBox _currentSensitivity = InputBox("1");
    private readonly TextBox _targetDpi = InputBox("1600");
    private readonly TextBlock _result = new()
    {
        Text = "Ingresa los valores y pulsa «Calcular equivalencia».",
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
        Foreground = B("MutedBrush", "#9DA8B3")
    };

    public DpiSensitivityCalculatorWindow()
    {
        Title = "Calculadora de sensibilidad por DPI";
        Width = 560;
        Height = 540;
        MinWidth = 480;
        MinHeight = 470;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Header(
            "Sensibilidad por DPI",
            "Convierte la sensibilidad entre DPIs conservando el eDPI. Válido para juegos con sensibilidad lineal y entrada raw."));

        panel.Children.Add(Field("DPI actual del ratón", _currentDpi, "Valores habituales: 400, 800, 1600, 3200."));
        panel.Children.Add(Field("Sensibilidad actual del juego", _currentSensitivity, "Por ejemplo: 0.8, 1.0, 2.5."));
        panel.Children.Add(Field("DPI objetivo", _targetDpi, "Introduce el DPI al que quieres jugar."));

        var presets = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        presets.Children.Add(new TextBlock
        {
            Text = "DPI objetivo rápido:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = B("MutedBrush", "#9DA8B3"),
            FontSize = 11
        });
        foreach (string dpi in new[] { "400", "800", "1600", "3200" })
            presets.Children.Add(Preset(dpi));
        panel.Children.Add(presets);

        var calculate = new Button
        {
            Content = "Calcular equivalencia",
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(14, 9, 14, 9),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsDefault = true
        };
        calculate.Click += (_, _) => Calculate();
        panel.Children.Add(calculate);

        panel.Children.Add(new Border
        {
            Background = B("PanelBrush", "#191E24"),
            BorderBrush = B("AccentBrush", "#22C55E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 14, 0, 0),
            Child = _result
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Fórmula: sensibilidad objetivo = sensibilidad actual × DPI actual ÷ DPI objetivo. " +
                   "Esto conserva el eDPI. La distancia física recorrida solo se mantiene en juegos con escala lineal y entrada raw; " +
                   "si el juego aplica aceleración o transformaciones no lineales, ajusta a ojo.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            FontSize = 11,
            Margin = new Thickness(0, 14, 0, 0)
        });
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

    private static StackPanel Field(string label, TextBox input, string hint)
    {
        var body = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        body.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Foreground = B("TextBrush", "#F2F5F7"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        body.Children.Add(input);
        body.Children.Add(new TextBlock
        {
            Text = hint,
            FontSize = 10,
            Foreground = B("DimBrush", "#71808D"),
            Margin = new Thickness(2, 3, 0, 0)
        });
        return body;
    }

    private Button Preset(string dpi)
    {
        var button = new Button
        {
            Content = dpi,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 11
        };
        button.Click += (_, _) => ApplyPreset(dpi);
        return button;
    }

    private static TextBox InputBox(string value) => new()
    {
        Text = value,
        Padding = new Thickness(10, 7, 10, 7),
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = B("PanelAltBrush", "#20262E"),
        Foreground = B("TextBrush", "#F2F5F7"),
        CaretBrush = B("AccentBrush", "#22C55E"),
        BorderBrush = B("BorderBrush", "#323C46"),
        BorderThickness = new Thickness(1)
    };

    private void Calculate()
    {
        if (!TryRead(_currentDpi.Text, out double currentDpi) || currentDpi is < 1 or > 100000 ||
            !TryRead(_currentSensitivity.Text, out double currentSensitivity) || currentSensitivity <= 0 || currentSensitivity > 100000 ||
            !TryRead(_targetDpi.Text, out double targetDpi) || targetDpi is < 1 or > 100000)
        {
            SetResult("Revisa los datos: el DPI debe estar entre 1 y 100 000 y la sensibilidad debe ser mayor que cero.", ok: false);
            return;
        }
        double effectiveDpi = currentDpi * currentSensitivity;
        double convertedSensitivity = effectiveDpi / targetDpi;
        SetResult($"Sensibilidad equivalente a {targetDpi:G8} DPI: {convertedSensitivity:G8}\n" +
                  $"eDPI conservado: {effectiveDpi:G8}\n" +
                  $"Factor de conversión: ×{currentDpi / targetDpi:G6}", ok: true);
    }

    private void SetResult(string text, bool ok)
    {
        _result.Text = text;
        _result.Foreground = ok ? B("AccentBrush", "#22C55E") : B("DangerBrush", "#EF4444");
    }

    private void ApplyPreset(string dpi) => _targetDpi.Text = dpi;

    private static bool TryRead(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
