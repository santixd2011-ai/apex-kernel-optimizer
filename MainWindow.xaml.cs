using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ApexKernelOptimizer.ViewModels;
using ApexKernelOptimizer.Views;
using ApexKernelOptimizer.Core.Models;

namespace ApexKernelOptimizer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly string _logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexKernel", "Logs");

    public MainWindow()
    {
        InitializeComponent();
        _viewModel.ConfirmRiskChange = ConfirmRiskChange;
        _viewModel.ConfirmBatchAction = ConfirmBatchAction;
        _viewModel.SelectPrioritySeparationProfile = SelectPrioritySeparationProfile;
        _viewModel.RequestOpenTool = OpenToolById;
        _viewModel.RequestOpenGraphicsReset = () =>
            new ApexKernelOptimizer.Views.GraphicsDriverResetWindow { Owner = this }.Show();
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
        };
        Closed += (_, _) => _viewModel.Dispose();
    }

    private int? SelectPrioritySeparationProfile()
    {
        var dialog = new ApexKernelOptimizer.Views.Win32PrioritySeparationWindow { Owner = this };
        return dialog.ShowDialog() == true ? dialog.SelectedValue : null;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _viewModel.ClearLog();

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _viewModel.Logs));
            _viewModel.AddLog("Contenido de la consola copiado al portapapeles.");
        }
        catch (Exception ex) { _viewModel.AddLog($"No se pudo copiar el log: {ex.Message}"); }
    }

    private bool ConfirmRiskChange(Core.Models.OptimizationItem item, string? messageOverride)
    {
        string warning = string.IsNullOrWhiteSpace(messageOverride)
            ? string.IsNullOrWhiteSpace(item.WarningMessage) ? "El ajuste puede cambiar funciones o preferencias de Windows." : item.WarningMessage!
            : messageOverride!;
        return MessageBox.Show(this,
            $"#{item.Id}: {item.Title}\n\n{warning}\n\n¿Quieres aplicar el ajuste? Se guardará una copia de los valores actuales para poder restaurarlos.",
            "Confirmar ajuste de riesgo medio", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private bool ConfirmBatchAction(string message) =>
        MessageBox.Show(this, message, "Confirmar operación", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private async void OpenToolById(int id)
    {
        OptimizationItem? item = _viewModel.Items.FirstOrDefault(i => i.Id == id);
        if (item is null) { _viewModel.AddLog($"No se encontró la herramienta #{id} en el catálogo."); return; }
        await OpenToolAsync(item);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_logFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", _logFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { _viewModel.AddLog($"No se pudo abrir la carpeta de logs: {ex.Message}"); }
    }

    private async void OpenTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OptimizationItem item }) return;
        await OpenToolAsync(item);
    }

    private async Task OpenToolAsync(OptimizationItem item)
    {
        if (item.Id == 149)
        {
            new ApexKernelOptimizer.Views.MousePollingWindow { Owner = this }.Show();
            _viewModel.AddLog("Se abrió el medidor de reportes WM_INPUT del mouse.");
            return;
        }
        if (item.Id == 16)
        {
            new ApexKernelOptimizer.Views.DpiSensitivityCalculatorWindow { Owner = this }.ShowDialog();
            _viewModel.AddLog("Se abrió la calculadora de sensibilidad equivalente por DPI.");
            return;
        }
        if (item.Id == 54)
        {
            new ApexKernelOptimizer.Views.GpuPreferenceManagerWindow(_viewModel) { Owner = this }.ShowDialog();
            return;
        }
        if (item.Id is 18 or 19 or 20)
        {
            new ApexKernelOptimizer.Views.GameProcessPrioritiesWindow(_viewModel, item.Id) { Owner = this }.ShowDialog();
            return;
        }
        if (item.Id == 69)
        {
            new ApexKernelOptimizer.Views.GameCompatibilityManagerWindow(_viewModel) { Owner = this }.ShowDialog();
            return;
        }
        if (item.Id == 72)
        {
            new ApexKernelOptimizer.Views.ShaderCacheManagerWindow(_viewModel) { Owner = this }.ShowDialog();
            return;
        }
        if (item.Id == 68)
        {
            new ApexKernelOptimizer.Views.DisplayRefreshManagerWindow(_viewModel) { Owner = this }.ShowDialog();
            return;
        }
        if (item.Id == 174)
        {
            new ApexKernelOptimizer.Views.ExplorerToolsWindow { Owner = this }.Show();
            _viewModel.AddLog("Se abrió el centro de Explorer y accesos rápidos de Configuración.");
            return;
        }
        if (item.Id == 146)
        {
            new ApexKernelOptimizer.Views.GraphicsDriverResetWindow { Owner = this }.Show();
            _viewModel.AddLog("Se abrió la herramienta de restablecimiento gráfico nativo.");
            return;
        }
        if (item.Id == 192)
        {
            SearchTextBox.Focus();
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
            _viewModel.AddLog("Se enfocó el buscador global y los filtros por riesgo/categoría.");
            return;
        }
        if (item.Id == 209)
        {
            new ApexKernelOptimizer.Views.ProfilesManagerWindow(_viewModel) { Owner = this }.Show();
            _viewModel.AddLog("Se abrió el gestor de perfiles de optimización.");
            return;
        }
        switch (item.Id)
        {
            case 21 or 22:
                new ApexKernelOptimizer.Views.ProcessRulesWindow { Owner = this }.ShowDialog();
                _viewModel.AddLog("Se abrió el gestor de reglas del demonio nativo por juego.");
                return;
            case 105:
                new ApexKernelOptimizer.Views.ServiceManagerWindow(_viewModel, thirdPartyOnly: false, item.Id) { Owner = this }.ShowDialog();
                return;
            case 205:
                new ApexKernelOptimizer.Views.ServiceManagerWindow(_viewModel, thirdPartyOnly: true, item.Id) { Owner = this }.ShowDialog();
                return;
            case 143:
                new ApexKernelOptimizer.Views.ProcessMonitorWindow(item.Id) { Owner = this }.Show();
                _viewModel.AddLog("Se abrió el monitor avanzado de procesos.");
                return;
            case 152:
                new ApexKernelOptimizer.Views.StartupManagerWindow(_viewModel, item.Id) { Owner = this }.ShowDialog();
                return;
            case 155:
                new ApexKernelOptimizer.Views.AuditConsoleWindow(item.Id) { Owner = this }.Show();
                _viewModel.AddLog("Se abrió la consola de auditoría del sistema.");
                return;
            default:
                break;
        }
        if (ToolUiCatalog.Supports(item.Id))
        {
            new ApexKernelOptimizer.Views.ToolWorkspaceWindow(item,
                (id, action, input) => _viewModel.RunToolAsync(id, action, input)) { Owner = this }.ShowDialog();
            return;
        }
        var content = new System.Windows.Controls.TextBox
        {
            Text = "Consultando…",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Margin = new Thickness(14)
        };
        var window = new Window
        {
            Title = $"#{item.Id} · {item.Title}",
            Owner = this,
            Width = 760,
            Height = 600,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        window.Show();
        try { content.Text = await _viewModel.GetToolContentAsync(item); }
        catch (Exception ex) { content.Text = $"No se pudo completar la medición: {ex.Message}"; }
        _viewModel.AddLog($"Se abrió la herramienta #{item.Id}: {item.Title}.");
    }

    private async void ExportProfile_Click(object sender, RoutedEventArgs e)
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
        try { await _viewModel.ExportProfileAsync(dialog.FileName); }
        catch (Exception ex) { _viewModel.AddLog($"No se pudo exportar el perfil: {ex.Message}"); }
    }

    private async void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar perfil de optimización",
            Filter = "Perfil ApexKernel (*.optprofile;*.json)|*.optprofile;*.json|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await _viewModel.ImportProfileAsync(dialog.FileName); }
        catch (Exception ex) { _viewModel.AddLog($"No se pudo importar el perfil: {ex.Message}"); }
    }
}
