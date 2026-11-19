using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ApexKernelOptimizer.Views;

public sealed class ExplorerToolsWindow : Window
{
    private readonly TextBlock _status;

    public ExplorerToolsWindow()
    {
        Title = "Explorer y accesos de Windows";
        Width = 700;
        Height = 620;
        MinWidth = 580;
        MinHeight = 480;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Header(
            "Explorer y accesos rápidos",
            "Abre páginas de Configuración de Windows relacionadas con rendimiento y periféricos, o reinicia la shell cuando la barra de tareas deja de responder."));

        panel.Children.Add(new TextBlock
        {
            Text = "PÁGINAS DE CONFIGURACIÓN",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 2, 0, 8)
        });
        AddSettingsButton(panel, "Pantalla", "ms-settings:display");
        AddSettingsButton(panel, "Energía y suspensión", "ms-settings:powersleep");
        AddSettingsButton(panel, "Red", "ms-settings:network-status");
        AddSettingsButton(panel, "Modo de juego", "ms-settings:gaming-gamemode");
        AddSettingsButton(panel, "Mouse y panel táctil", "ms-settings:mousetouchpad");

        panel.Children.Add(new TextBlock
        {
            Text = "REINICIO DE LA SHELL",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 18, 0, 8)
        });
        var restart = new Button
        {
            Content = "Reiniciar Explorer y la barra de tareas",
            Padding = new Thickness(14, 9, 14, 9),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = B("YellowSoftBrush", "#3A321F"),
            BorderBrush = B("WarningBrush", "#F59E0B"),
            Foreground = B("WarningBrush", "#F59E0B")
        };
        restart.Click += async (_, _) => await RestartExplorerAsync();
        panel.Children.Add(restart);

        panel.Children.Add(new Border
        {
            Background = B("PanelBrush", "#191E24"),
            BorderBrush = B("BorderBrush", "#323C46"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 14, 0, 0),
            Child = new TextBlock
            {
                Text = "Al reiniciar Explorer pueden cerrarse las ventanas del Explorador abiertas y parpadear la barra de tareas. " +
                       "Guarda las operaciones de archivos antes de continuar; las demás aplicaciones no se reinician.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = B("WarningBrush", "#F59E0B"),
                FontSize = 11
            }
        });

        _status = new TextBlock
        {
            Text = "Elige una página de Configuración o reinicia la shell cuando sea necesario.",
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

    private void AddSettingsButton(Panel panel, string label, string uri)
    {
        var button = new Button
        {
            Content = label,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 3, 0, 3),
            Tag = uri
        };
        button.Click += (_, _) => OpenSettings(uri);
        panel.Children.Add(button);
    }

    private void SetStatus(string text, bool ok = true)
    {
        _status.Text = text;
        _status.Foreground = ok ? B("MutedBrush", "#9DA8B3") : B("DangerBrush", "#EF4444");
    }

    private void OpenSettings(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            SetStatus($"Se solicitó abrir '{uri}'. La disponibilidad de cada página depende de la versión y edición de Windows.");
        }
        catch (Exception ex) { SetStatus($"Windows no pudo abrir esa página: {ex.Message}", ok: false); }
    }

    private async Task RestartExplorerAsync()
    {
        if (MessageBox.Show(this,
                "Reiniciar Explorer cerrará sus ventanas abiertas durante unos segundos y reconstruirá la barra de tareas. Guarda cualquier operación de archivos antes de seguir. ¿Continuar?",
                "Confirmar reinicio de Explorer", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            SetStatus("No se encontró la ventana de la barra de tareas; no se cambió ningún proceso.", ok: false);
            return;
        }
        _ = GetWindowThreadProcessId(taskbar, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            SetStatus("No se pudo identificar de forma segura el proceso de Explorer; no se cambió ningún proceso.", ok: false);
            return;
        }

        try
        {
            using Process shell = Process.GetProcessById((int)processId);
            shell.Kill();
            await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await Task.Delay(900);
            if (FindWindow("Shell_TrayWnd", null) == IntPtr.Zero)
            {
                string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                _ = Process.Start(new ProcessStartInfo(explorerPath) { UseShellExecute = true });
            }

            for (int i = 0; i < 40 && FindWindow("Shell_TrayWnd", null) == IntPtr.Zero; i++)
                await Task.Delay(250);
            bool restored = FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
            SetStatus(restored
                ? $"Explorer se reinició correctamente (PID anterior {processId}) y la barra de tareas volvió a aparecer."
                : "Explorer terminó, pero Windows aún no mostró la barra de tareas. Abre el Administrador de tareas y ejecuta explorer.exe.",
                ok: restored);
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo completar el reinicio de Explorer: {ex.Message}. Si la barra no vuelve, abre el Administrador de tareas y ejecuta explorer.exe.", ok: false);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
