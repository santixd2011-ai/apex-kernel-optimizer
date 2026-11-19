using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApexKernelOptimizer.Views;

/// <summary>
/// Ventana genérica de herramientas: título, descripción, entrada opcional,
/// botones de acción y salida de texto. Las acciones las resuelve MainViewModel.RunToolAsync.
/// </summary>
public sealed class SimpleToolWindow : Window
{
    private readonly Func<int, string, string, Task<string>> _runTool;
    private readonly int _toolId;
    private readonly TextBox _input;
    private readonly TextBox _output;

    public SimpleToolWindow(int toolId, string title, string description, (string Label, bool NeedsConfirmation)[] actions, Func<int, string, string, Task<string>> runTool)
    {
        _toolId = toolId;
        _runTool = runTool;
        Title = $"#{toolId} · {title}";
        Width = 780;
        Height = 620;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 12) });

        var inputHeader = new TextBlock { Text = "Entrada (opcional según la acción):", Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(inputHeader);
        _input = new TextBox { Height = 26, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(_input);

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach ((string label, bool needsConfirmation) in actions)
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8) };
            button.Click += async (_, _) => await RunActionAsync(label, needsConfirmation);
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);

        panel.Children.Add(new TextBlock { Text = "Salida:", Margin = new Thickness(0, 0, 0, 4) });
        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            Height = 320
        };
        panel.Children.Add(_output);
        Content = panel;
    }

    private async Task RunActionAsync(string action, bool needsConfirmation)
    {
        string input = _input.Text;
        if (needsConfirmation)
        {
            MessageBoxResult confirmation = MessageBox.Show(this,
                $"¿Ejecutar '{action}'?\n\nRevisa que el campo de entrada (si aplica) contenga el valor correcto antes de continuar.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;
        }
        _output.Text = "Ejecutando…";
        try
        {
            _output.Text = await _runTool(_toolId, action, input);
        }
        catch (Exception ex)
        {
            _output.Text = $"No se pudo ejecutar la acción: {ex.Message}";
        }
    }
}
