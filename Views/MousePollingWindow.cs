using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ApexKernelOptimizer.Views;

public sealed class MousePollingWindow : Window
{
    private const int WmInput = 0x00FF;
    private const uint RimTypeMouse = 0;
    private const uint RidInput = 0x10000003;
    private const uint RidevRemove = 0x00000001;
    private readonly TextBlock _rateText;
    private readonly TextBlock _stabilityText;
    private readonly TextBlock _statusText;
    private readonly DispatcherTimer _timer;
    private readonly Queue<double> _recentRates = new();
    private HwndSource? _source;
    private long _reports;
    private long _totalReports;
    private bool _registered;

    public MousePollingWindow()
    {
        Title = "Tester de reportes del mouse";
        Width = 680;
        Height = 520;
        MinWidth = 560;
        MinHeight = 440;
        Background = B("CanvasBrush", "#0F1318");
        Foreground = B("TextBrush", "#F2F5F7");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Header(
            "Tasa de reportes del mouse",
            "Cuenta los mensajes Raw Input WM_INPUT del ratón mientras esta ventana esté al frente. Solo se contabilizan reportes: no se guardan coordenadas ni botones."));

        panel.Children.Add(new TextBlock
        {
            Text = "TASA OBSERVADA DE REPORTES",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 4, 0, 6)
        });
        _rateText = new TextBlock
        {
            Text = "Esperando movimiento…",
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            Foreground = B("AccentBrush", "#22C55E"),
            Margin = new Thickness(0, 2, 0, 8)
        };
        _stabilityText = new TextBlock
        {
            Text = "Estabilidad: reuniendo muestras",
            FontSize = 14,
            Foreground = B("TextBrush", "#F2F5F7"),
            Margin = new Thickness(0, 4, 0, 8)
        };

        panel.Children.Add(new Border
        {
            Background = B("PanelBrush", "#191E24"),
            BorderBrush = B("BorderBrush", "#323C46"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Child = new StackPanel
            {
                Children = { _rateText, _stabilityText }
            }
        });

        _statusText = new TextBlock
        {
            Text = "Mueve el mouse mientras esta ventana esté activa. Solo se cuentan reportes; no se guardan coordenadas ni botones.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("MutedBrush", "#9DA8B3"),
            Margin = new Thickness(0, 14, 0, 0)
        };
        panel.Children.Add(_statusText);

        var reset = new Button
        {
            Content = "Reiniciar medición",
            Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 0)
        };
        reset.Click += (_, _) => ResetMeasurement();
        panel.Children.Add(reset);

        panel.Children.Add(new TextBlock
        {
            Text = "La cifra estima los mensajes WM_INPUT procesados por segundo. Puede ser menor que la tasa nominal del fabricante si el sistema pierde, agrupa o retrasa mensajes. " +
                   "Para comparar tasas, mueve el ratón de forma continua durante varios segundos.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("DimBrush", "#71808D"),
            FontSize = 11,
            Margin = new Thickness(0, 16, 0, 0)
        });
        Content = panel;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateRate();
        SourceInitialized += (_, _) => RegisterMouseInput();
        Closed += (_, _) => StopMonitoring();
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

    private void RegisterMouseInput()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WindowProc);
        var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 0, Target = hwnd };
        _registered = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        if (_registered)
        {
            _statusText.Text = "Captura Raw Input activa mientras ApexKernel esté al frente. Mueve el mouse para reunir muestras. No se guardan coordenadas ni botones.";
            _statusText.Foreground = B("AccentBrush", "#22C55E");
            _timer.Start();
        }
        else
        {
            _statusText.Text = $"Windows no permitió iniciar la captura Raw Input (error {Marshal.GetLastWin32Error()}).";
            _statusText.Foreground = B("DangerBrush", "#EF4444");
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmInput) CountMousePacket(lParam);
        return IntPtr.Zero;
    }

    private void CountMousePacket(IntPtr rawInputHandle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize || size > 65536) return;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize) == uint.MaxValue) return;
            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type == RimTypeMouse)
            {
                Interlocked.Increment(ref _reports);
                Interlocked.Increment(ref _totalReports);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void UpdateRate()
    {
        long count = Interlocked.Exchange(ref _reports, 0);
        if (count == 0)
        {
            _rateText.Text = "Sin reportes recientes";
            _rateText.Foreground = B("MutedBrush", "#9DA8B3");
            return;
        }
        double rate = count / _timer.Interval.TotalSeconds;
        _recentRates.Enqueue(rate);
        while (_recentRates.Count > 10) _recentRates.Dequeue();
        _rateText.Text = $"{rate:0} reportes/s";
        _rateText.Foreground = rate >= 120 ? B("AccentBrush", "#22C55E")
            : rate >= 60 ? B("WarningBrush", "#F59E0B") : B("DangerBrush", "#EF4444");
        _stabilityText.Text = _recentRates.Count < 3
            ? $"Muestra #{_recentRates.Count}/10 · acumulados: {Interlocked.Read(ref _totalReports):N0}"
            : $"Últimos {_recentRates.Count}s: {_recentRates.Min():0}–{_recentRates.Max():0} reportes/s · acumulados: {Interlocked.Read(ref _totalReports):N0}";
    }

    private void ResetMeasurement()
    {
        Interlocked.Exchange(ref _reports, 0);
        Interlocked.Exchange(ref _totalReports, 0);
        _recentRates.Clear();
        _rateText.Text = "Medición reiniciada · mueve el mouse";
        _rateText.Foreground = B("AccentBrush", "#22C55E");
        _stabilityText.Text = "Estabilidad: reuniendo muestras";
        _statusText.Text = _registered
            ? "Contadores puestos a cero. Mueve el ratón de forma continua para volver a medir."
            : "Contadores puestos a cero. La captura Raw Input no está activa.";
        _statusText.Foreground = _registered ? B("MutedBrush", "#9DA8B3") : B("DangerBrush", "#EF4444");
    }

    private void StopMonitoring()
    {
        _timer.Stop();
        _source?.RemoveHook(WindowProc);
        _source = null;
        if (_registered)
        {
            var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = RidevRemove, Target = IntPtr.Zero };
            _ = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
            _registered = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { public uint Type, Size; public IntPtr Device, WParam; }

    [DllImport("user32.dll", EntryPoint = "RegisterRawInputDevices", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputData", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
}
