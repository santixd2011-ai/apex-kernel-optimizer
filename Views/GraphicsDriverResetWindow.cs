using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApexKernelOptimizer.Views;

public sealed class GraphicsDriverResetWindow : Window
{
    private readonly TextBlock _status;

    public GraphicsDriverResetWindow()
    {
        Title = "Restablecer controlador gráfico";
        Width = 580;
        Height = 470;
        MinWidth = 500;
        MinHeight = 410;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Header(
            "Restablecimiento gráfico",
            "Envía la combinación Win + Ctrl + Shift + B de Windows para reiniciar el subsistema gráfico sin cerrar aplicaciones."));

        panel.Children.Add(new TextBlock
        {
            Text = "¿CUÁNDO USARLO?",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 2, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Útil cuando la pantalla parpadea, el escritorio se congela tras cambiar de resolución o el controlador deja de responder sin llegar a fallar del todo. " +
                   "No instala ni cambia el controlador, y no sustituye a reiniciar el equipo.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("TextBrush", "#F2F5F7")
        });

        panel.Children.Add(new Border
        {
            Background = B("YellowSoftBrush", "#3A321F"),
            BorderBrush = B("WarningBrush", "#F59E0B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 14, 0, 0),
            Child = new TextBlock
            {
                Text = "AVISO: la pantalla puede parpadear o quedar momentáneamente en negro y escucharse un pitido. Guarda tu trabajo antes de continuar.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = B("WarningBrush", "#F59E0B")
            }
        });

        var button = new Button
        {
            Content = "Enviar Win + Ctrl + Shift + B",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 16, 0, 0)
        };
        button.Click += (_, _) => ResetGraphics();
        panel.Children.Add(button);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 14, 0, 0)
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

    private void SetStatus(string text, bool ok = true)
    {
        _status.Text = text;
        _status.Foreground = ok ? B("MutedBrush", "#9DA8B3") : B("DangerBrush", "#EF4444");
    }

    private void ResetGraphics()
    {
        if (MessageBox.Show(this,
                "La pantalla puede parpadear o quedar momentáneamente en negro mientras Windows intenta restablecer el controlador. ¿Enviar Win + Ctrl + Shift + B?",
                "Confirmar restablecimiento gráfico", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Input[] chord =
        [
            Key(0x5B, false), Key(0x11, false), Key(0x10, false), Key(0x42, false),
            Key(0x42, true), Key(0x10, true), Key(0x11, true), Key(0x5B, true)
        ];
        uint sent = SendInput((uint)chord.Length, chord, Marshal.SizeOf<Input>());
        if (sent == chord.Length)
        {
            SetStatus("Windows recibió la combinación completa (8 eventos). Si la pantalla no se restablece, prueba a reiniciar el equipo.");
            return;
        }

        // Always release modifiers in case Windows accepted only part of the sequence.
        Input[] releases = [Key(0x42, true), Key(0x10, true), Key(0x11, true), Key(0x5B, true)];
        _ = SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
        SetStatus($"Windows solo aceptó {sent} de 8 eventos. Se liberaron los modificadores; puedes probar manualmente Win + Ctrl + Shift + B.", ok: false);
    }

    private static Input Key(ushort virtualKey, bool keyUp) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = keyUp ? 0x0002u : 0u } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public IntPtr ExtraInfo; }

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
}
