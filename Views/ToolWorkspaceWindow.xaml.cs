using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ApexKernelOptimizer.Core.Models;

namespace ApexKernelOptimizer.Views;

/// <summary>
/// Espacio de trabajo moderno para las 28 herramientas simples: acciones explicadas,
/// entrada validada, confirmación integrada, resultado con historial y exportación.
/// </summary>
public partial class ToolWorkspaceWindow : Window
{
    private readonly int _id;
    private readonly ToolDefinition _definition;
    private readonly Func<int, string, string, Task<string>> _runTool;
    private readonly StringBuilder _sessionLog = new();
    private ToolAction? _pending;
    private bool _busy;

    public ToolWorkspaceWindow(OptimizationItem item, Func<int, string, string, Task<string>> runTool)
    {
        InitializeComponent();

        _id = item.Id;
        _definition = ToolUiCatalog.For(item.Id);
        _runTool = runTool;

        Title = $"#{item.Id} · {item.Title}";
        IdText.Text = $"#{item.Id}";
        AreaText.Text = _definition.Area;
        RiskText.Text = item.RiskLevel.ToString().ToUpperInvariant();
        (Color badge, Color text) = item.RiskLevel switch
        {
            RiskLevel.Yellow => (Color.FromRgb(0x3A, 0x32, 0x1F), Color.FromRgb(0xF5, 0x9E, 0x0B)),
            RiskLevel.Red => (Color.FromRgb(0x3D, 0x24, 0x29), Color.FromRgb(0xEF, 0x44, 0x44)),
            _ => (Color.FromRgb(0x20, 0x36, 0x2E), Color.FromRgb(0x22, 0xC5, 0x5E))
        };
        RiskBadge.Background = new SolidColorBrush(badge);
        RiskText.Foreground = new SolidColorBrush(text);

        TitleText.Text = item.Title;
        DescriptionText.Text = item.ShortDescription;
        InputHintText.Text = string.IsNullOrWhiteSpace(_definition.Notes)
            ? ToolUiCatalog.InputPlaceholder(_id)
            : _definition.Notes;
        InputBox.ToolTip = ToolUiCatalog.InputPlaceholder(_id);
        TechnicalText.Text = string.IsNullOrWhiteSpace(item.TechnicalSummary)
            ? "Sin detalle técnico adicional en el catálogo."
            : item.TechnicalSummary;
        NotesText.Text = "Las acciones se ejecutan en el equipo real. Consulta primero el estado cuando exista esa opción " +
            "y conserva el respaldo para poder revertir. Las reparaciones pueden tardar varios minutos.";
        ActionsList.ItemsSource = _definition.Actions;
        StatusText.Text = $"{_definition.Actions.Count} acciones disponibles · Empieza por 'Consultar estado actual'.";
    }

    private void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ToolAction? readOnly = _definition.Actions.FirstOrDefault(action => action.IsReadOnly);
        if (readOnly is null)
        {
            StatusText.Text = "Esta herramienta no tiene una acción de solo lectura; selecciona una acción manualmente.";
            return;
        }
        _ = ExecuteAsync(readOnly);
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { Tag: ToolAction action }) return;
        if (action.NeedsInput && string.IsNullOrWhiteSpace(InputBox.Text))
        {
            StatusText.Text = $"'{action.Label}' necesita un parámetro. Escríbelo en el campo de entrada.";
            InputBox.Focus();
            return;
        }
        if (action.NeedsConfirmation)
        {
            _pending = action;
            ConfirmText.Text = $"Se ejecutará '{action.Label}'{(action.NeedsInput ? $" con el parámetro «{InputBox.Text.Trim()}»" : "")}. " +
                "Esta acción puede modificar el sistema. ¿Continuar?";
            ConfirmBar.Visibility = Visibility.Visible;
            StatusText.Text = "Esperando confirmación.";
            return;
        }
        _ = ExecuteAsync(action);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        if (_pending is not null) _ = ExecuteAsync(_pending);
        _pending = null;
    }

    private void CancelConfirm_Click(object sender, RoutedEventArgs e)
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        _pending = null;
        StatusText.Text = "Acción cancelada; no se modificó nada.";
    }

    private void ClearInput_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        StatusText.Text = "Campo de entrada borrado.";
    }

    private async Task ExecuteAsync(ToolAction action)
    {
        if (_busy) return;
        _busy = true;
        BusyBar.IsIndeterminate = true;
        SetActionsEnabled(false);
        StatusText.Text = $"Ejecutando '{action.Label}'…";

        string input = InputBox.Text.Trim();
        _sessionLog.AppendLine($"=== {action.Label} · {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        if (!string.IsNullOrWhiteSpace(input)) _sessionLog.AppendLine("Entrada: " + input);
        OutputBox.Text = _sessionLog.ToString();
        ScrollOutputToEnd();

        var stopwatch = Stopwatch.StartNew();
        string result;
        try
        {
            result = await _runTool(_id, action.Label, input);
        }
        catch (Exception ex)
        {
            result = $"No se pudo ejecutar '{action.Label}': {ex.Message}";
        }
        stopwatch.Stop();

        _sessionLog.AppendLine(result);
        _sessionLog.AppendLine($"--- finalizado en {stopwatch.Elapsed.TotalSeconds:0.0} s ---");
        _sessionLog.AppendLine();
        OutputBox.Text = _sessionLog.ToString();
        ScrollOutputToEnd();

        string summary = FirstLine(result);
        HistoryList.Items.Add($"{DateTime.Now:HH:mm:ss}  {action.Label}  ·  {summary}");
        HistoryList.SelectedIndex = HistoryList.Items.Count - 1;
        StatusText.Text = $"'{action.Label}' completado en {stopwatch.Elapsed.TotalSeconds:0.0} s · {summary}";
        BusyBar.IsIndeterminate = false;
        SetActionsEnabled(true);
    }

    private void SetActionsEnabled(bool enabled)
    {
        RefreshStatusButton.IsEnabled = enabled;
        InputBox.IsEnabled = enabled;
        foreach (Button button in FindVisualChildren<Button>(ActionsList))
            button.IsEnabled = enabled;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (T nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void ScrollOutputToEnd()
    {
        OutputBox.CaretIndex = OutputBox.Text.Length;
        OutputBox.ScrollToEnd();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OutputBox.Text)) { StatusText.Text = "No hay resultado para copiar."; return; }
        Clipboard.SetText(OutputBox.Text);
        StatusText.Text = "Resultado copiado al portapapeles.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OutputBox.Text)) { StatusText.Text = "No hay resultado para guardar."; return; }
        var dialog = new SaveFileDialog
        {
            Title = "Guardar resultado de la herramienta",
            Filter = "Archivo de texto (*.txt)|*.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"ApexKernel-Herramienta-{_id}-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, OutputBox.Text, new UTF8Encoding(false));
            StatusText.Text = $"Resultado guardado en {dialog.FileName}";
        }
        catch (Exception ex) { StatusText.Text = $"No se pudo guardar: {ex.Message}"; }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        OutputBox.Clear();
        _sessionLog.Clear();
        HistoryList.Items.Clear();
        StatusText.Text = "Resultado e historial borrados.";
    }

    private static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            return line.Length <= 110 ? line : line[..110] + "…";
        return "sin salida";
    }
}
