using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using ApexKernelOptimizer.Core.Daemon;
using ApexKernelOptimizer.Core.Engine;
using ApexKernelOptimizer.Core.Models;
using ApexKernelOptimizer.Core.Services;
using System.Windows.Threading;

namespace ApexKernelOptimizer.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string _logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexKernel", "Logs", "app.log");
    private readonly ExecutionEngine _executionEngine = new();
    private readonly HashSet<int> _suppressToggle = [];
    private readonly HashSet<int> _suppressOptionRefresh = [];
    private readonly HashSet<int> _backedUpIds = [];
    private string _searchText = "";
    private string _selectedRisk = "Todos los riesgos";
    private string _selectedRiskFilter = "Todos";
    private string _currentView = "Dashboard";
    private OptimizationCategory? _selectedCategory;
    private HardwareSnapshot? _hardware;
    private bool _isBusy;
    private int _operationProgress;
    private string _operationStatus = "Listo.";
    private double _memoryUsagePercent;
    private double _memoryTotalGb;
    private double _memoryAvailableGb;
    private bool _isLogExpanded = true;
    private readonly TelemetryService _telemetry = new();
    private readonly DispatcherTimer _telemetryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private string _selectedTheme = "Apex Neo";
    private double _cpuLoad, _gpuLoad, _netLatencyMs, _diskLoad;
    private int _processCount;
    private ProcessLassoNativeService? _daemon;
    private int _samplingTelemetry;

    public ObservableCollection<OptimizationItem> Items { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ICollectionView FilteredItems { get; }
    public string[] RiskFilters { get; } = ["Todos los riesgos", "Verde", "Amarillo", "Rojo"];
    public ObservableCollection<CategoryOption> Categories { get; } = [];
    public ObservableCollection<CategoryNavItem> CategoryNav { get; } = [];
    public ObservableCollection<QuickToolItem> QuickAccess { get; } = [];

    // Delegados proporcionados por MainWindow (ninguna vista externa depende de ellos).
    public Func<OptimizationItem, string?, bool>? ConfirmRiskChange { get; set; }
    public Func<string, bool>? ConfirmBatchAction { get; set; }
    public Func<int?>? SelectPrioritySeparationProfile { get; set; }
    public Action<int>? RequestOpenTool { get; set; }
    public Action? RequestOpenGraphicsReset { get; set; }

    public ICommand ShowDashboardCommand { get; }
    public ICommand SelectCategoryCommand { get; }
    public ICommand OpenToolByIdCommand { get; }
    public ICommand SetRiskFilterCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand ApplyAllSafeCommand { get; }
    public ICommand RevertBackedUpCommand { get; }
    public ICommand ApplyNormalProfileCommand { get; }
    public ICommand ApplyGamingProfileCommand { get; }
    public ICommand ApplyAlmostAllProfileCommand { get; }
    public ICommand CreateRestorePointCommand { get; }
    public ICommand ToggleCompetitiveModeCommand { get; }
    public ICommand PurgeStandbyCommand { get; }
    public ICommand ResetGraphicsDriverCommand { get; }
    public ICommand RefreshMemoryCommand { get; }
    public ICommand ToggleProModeCommand { get; }
    public ICommand RefreshProValuesCommand { get; }
    public ICommand ApplySelectedCommand { get; }
    public ICommand RevertSelectedCommand { get; }
    public ICommand ToggleItemCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand ApplyItemCommand { get; }
    public ICommand RestoreItemCommand { get; }
    public ICommand SetDefaultItemCommand { get; }
    public ICommand RefreshTelemetryCommand { get; }

    private static readonly Dictionary<OptimizationCategory, string> CategoryGlyphs = new()
    {
        [OptimizationCategory.Peripherals] = "⌨",
        [OptimizationCategory.CpuMemoryKernel] = "⚙",
        [OptimizationCategory.PowerThermal] = "⏻",
        [OptimizationCategory.GpuDisplay] = "◇",
        [OptimizationCategory.NetworkPing] = "⌁",
        [OptimizationCategory.AudioMultimedia] = "♫",
        [OptimizationCategory.UiPrivacyQoL] = "▦",
        [OptimizationCategory.ServicesStorage] = "▤",
        [OptimizationCategory.NativeTools] = "❖"
    };

    // ===== Catálogos de los perfiles generales =====
    private static readonly int[] NormalProfileIds =
    [
        1, 2, 3, 4, 6, 10, 11, 12, 17, 83, 87, 90, 91, 92, 94, 95, 96, 98, 99, 100,
        102, 104, 107, 113, 116, 119, 120, 128, 141, 142, 158, 159, 162, 164, 166,
        169, 170, 171, 172, 173, 184, 185, 187, 189, 193, 196, 197, 198, 199, 200
    ];

    private static readonly int[] GamingProfileIds =
    [
        23, 24, 25, 26, 27, 28, 30, 31, 34, 37, 39, 42, 44, 45, 46, 47, 48, 52, 53,
        55, 60, 61, 62, 63, 65, 66, 73, 74, 75, 79, 80, 81, 84, 94, 113, 115, 116,
        117, 118, 119, 120, 123, 125, 126, 128, 131, 145, 157, 158, 162, 194, 196,
        197, 198, 199, 200, 206
    ];

    private static readonly int[] AlmostAllDeniedIds = [35, 36, 58, 88, 127, 136, 137, 138, 140, 181];

    private const string AlmostAllWarningText =
        "NO RECOMENDADO: puede dejar el sistema inestable o bloquear juegos con anticheat; crea un punto de restauración y usa 'Revertir todo lo respaldado' para volver";

    public MainViewModel()
    {
        FilteredItems = CollectionViewSource.GetDefaultView(Items);
        FilteredItems.Filter = MatchesFilter;
        Categories.Add(new(null, "Todas las categorías"));
        Categories.Add(new(OptimizationCategory.Peripherals, "Mouse y periféricos"));
        Categories.Add(new(OptimizationCategory.CpuMemoryKernel, "CPU, kernel y memoria"));
        Categories.Add(new(OptimizationCategory.PowerThermal, "Energía y térmica"));
        Categories.Add(new(OptimizationCategory.GpuDisplay, "GPU y pantalla"));
        Categories.Add(new(OptimizationCategory.NetworkPing, "Red y ping"));
        Categories.Add(new(OptimizationCategory.AudioMultimedia, "Audio y multimedia"));
        Categories.Add(new(OptimizationCategory.UiPrivacyQoL, "Interfaz y privacidad"));
        Categories.Add(new(OptimizationCategory.ServicesStorage, "Servicios y almacenamiento"));
        Categories.Add(new(OptimizationCategory.NativeTools, "Herramientas y sincronización"));
        SelectedCategoryOption = Categories[0];

        foreach (CategoryOption option in Categories.Where(c => c.Category is not null))
            CategoryNav.Add(new CategoryNavItem(option.Category!.Value, option.Label, CategoryGlyphs[option.Category.Value]));
        QuickAccess.Add(new(143, "⌕", "Monitor Procesos"));
        QuickAccess.Add(new(21, "⚖", "Reglas por Juego"));
        QuickAccess.Add(new(149, "◉", "Tester Mouse"));
        QuickAccess.Add(new(82, "⌁", "Benchmark DNS"));
        QuickAccess.Add(new(105, "▤", "Gestor Servicios"));
        QuickAccess.Add(new(207, "⛁", "Respaldos"));

        ShowDashboardCommand = new RelayCommand(_ => CurrentView = "Dashboard");
        ToggleProModeCommand = new RelayCommand(_ =>
        {
            if (CurrentView == "Pro") { CurrentView = "List"; return; }
            CurrentView = "Pro";
            RefreshProValues();
        });
        RefreshProValuesCommand = new RelayCommand(_ => RefreshProValues());
        ApplySelectedCommand = new RelayCommand(async _ => await ApplySelectedAsync());
        RevertSelectedCommand = new RelayCommand(async _ => await RevertSelectedAsync());
        ToggleItemCommand = new RelayCommand(async parameter =>
        {
            if (parameter is not OptimizationItem item || IsBusy) return;
            bool desired = !item.IsEnabled;
            if (ConfirmBatchAction?.Invoke($"¿{(desired ? "Aplicar" : "Restaurar")} el ajuste #{item.Id} ({item.Title})?") != true) return;
            IsBusy = true;
            try
            {
                ExecutionResult result = await ApplyToggleAsync(item, desired);
                OperationStatus = result.Message;
                if (await _executionEngine.HasBackupAsync(item)) _backedUpIds.Add(item.Id);
                RefreshProValues();
                RefreshCounts();
            }
            catch (Exception ex) { AddLog($"Error en #{item.Id}: {ex.Message}"); }
            finally { IsBusy = false; }
        });
        SelectCategoryCommand = new RelayCommand(parameter =>
        {
            OptimizationCategory? category = parameter switch
            {
                CategoryNavItem nav => nav.Category,
                CategoryOption option => option.Category,
                OptimizationCategory value => value,
                _ => null
            };
            SelectedCategoryOption = Categories.FirstOrDefault(c => c.Category == category) ?? Categories[0];
            CurrentView = "List";
        });
        OpenToolByIdCommand = new RelayCommand(parameter =>
        {
            int? id = parameter switch
            {
                int value => value,
                QuickToolItem item => item.Id,
                _ => null
            };
            if (id is not null) RequestOpenTool?.Invoke(id.Value);
        });
        _selectedTheme = ThemeService.Load();
        OnPropertyChanged(nameof(SelectedTheme));
        ExportReportCommand = new RelayCommand(parameter => ExportReport(parameter?.ToString() ?? "html"));
        ApplyItemCommand = new RelayCommand(async parameter => await ApplyItemAsync(parameter as OptimizationItem));
        RestoreItemCommand = new RelayCommand(async parameter => await RestoreItemAsync(parameter as OptimizationItem));
        SetDefaultItemCommand = new RelayCommand(async parameter => await SetDefaultItemAsync(parameter as OptimizationItem));
        RefreshTelemetryCommand = new RelayCommand(async _ => await SampleTelemetryAsync());
        _telemetryTimer.Tick += async (_, _) => await SampleTelemetryAsync();
        _telemetryTimer.Start();
        _ = SampleTelemetryAsync();
        SetRiskFilterCommand = new RelayCommand(parameter => SelectedRiskFilter = parameter?.ToString() ?? "Todos");
        ClearSearchCommand = new RelayCommand(_ => SearchText = "");
        ToggleLogCommand = new RelayCommand(_ => IsLogExpanded = !IsLogExpanded);
        ApplyAllSafeCommand = new RelayCommand(async _ => await ApplyAllSafeAsync());
        RevertBackedUpCommand = new RelayCommand(async _ => await RevertBackedUpAsync());
        ApplyNormalProfileCommand = new RelayCommand(async _ => await ExecuteProfileAsync(
            "normal", NormalProfileIds,
            "Perfil 'Uso normal (equilibrado)': aplicará una selección curada de ajustes de comodidad, privacidad y estabilidad. No modifica seguridad, anticheat ni servicios críticos."));
        ApplyGamingProfileCommand = new RelayCommand(async _ => await ExecuteProfileAsync(
            "gaming", GamingProfileIds,
            "Perfil 'Modo gaming (baja latencia)': creará un punto de restauración y aplicará ajustes de CPU, GPU, red, audio y juego para reducir latencia."));
        ApplyAlmostAllProfileCommand = new RelayCommand(async _ => await ExecuteAlmostAllProfileAsync());
        CreateRestorePointCommand = new RelayCommand(async _ => await CreateRestorePointFromUiAsync());
        ToggleCompetitiveModeCommand = new RelayCommand(async _ => await ToggleCompetitiveModeAsync());
        PurgeStandbyCommand = new RelayCommand(async _ => await PurgeStandbyAsync());
        ResetGraphicsDriverCommand = new RelayCommand(_ =>
        {
            RequestOpenGraphicsReset?.Invoke();
            AddLog("Se abrió la herramienta de restablecimiento gráfico nativo (#146).");
        });
        RefreshMemoryCommand = new RelayCommand(_ => RefreshMemoryUsage());

        AddLog("Aplicación iniciada. Los ajustes habilitados respaldan el estado previo antes de aplicarse.");
    }

    // ===== Búsqueda, filtros y navegación =====
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) FilteredItems.Refresh(); } }
    public string SelectedRisk
    {
        get => _selectedRisk;
        set { if (Set(ref _selectedRisk, value)) FilteredItems.Refresh(); }
    }
    public string SelectedRiskFilter
    {
        get => _selectedRiskFilter;
        set
        {
            if (!Set(ref _selectedRiskFilter, value)) return;
            SelectedRisk = value switch
            {
                "Seguros" => "Verde",
                "Opcionales" => "Amarillo",
                "Agresivos" => "Rojo",
                _ => "Todos los riesgos"
            };
            FilteredItems.Refresh();
        }
    }
    public CategoryOption? SelectedCategoryOption
    {
        get => Categories.FirstOrDefault(c => c.Category == _selectedCategory);
        set { if (_selectedCategory == value?.Category) return; _selectedCategory = value?.Category; OnPropertyChanged(); FilteredItems.Refresh(); }
    }
    public string CurrentView { get => _currentView; set => Set(ref _currentView, value); }

    public string[] Themes => ThemeService.Themes;
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!Set(ref _selectedTheme, value)) return;
            ThemeService.Apply(value);
            AddLog($"Tema visual cambiado a {value}.");
        }
    }

    public double CpuLoad { get => _cpuLoad; private set => Set(ref _cpuLoad, value); }
    public double GpuLoad { get => _gpuLoad; private set => Set(ref _gpuLoad, value); }
    public double NetLatencyMs { get => _netLatencyMs; private set => Set(ref _netLatencyMs, value); }
    public double DiskLoad { get => _diskLoad; private set => Set(ref _diskLoad, value); }
    public int ProcessCount { get => _processCount; private set => Set(ref _processCount, value); }
    public ObservableCollection<double> CpuHistory { get; } = [];
    public ObservableCollection<double> RamHistory { get; } = [];
    public ObservableCollection<double> GpuHistory { get; } = [];
    public ObservableCollection<double> NetHistory { get; } = [];
    public ObservableCollection<double> DiskHistory { get; } = [];

    public string CpuPoints => ToPoints(CpuHistory);
    public string RamPoints => ToPoints(RamHistory);
    public string GpuPoints => ToPoints(GpuHistory);
    public string NetPoints => ToPoints(NetHistory, 200);
    public string DiskPoints => ToPoints(DiskHistory);

    private static string ToPoints(IEnumerable<double> values, double scale = 100)
    {
        double[] arr = values.ToArray();
        if (arr.Length == 0) return "0,40 160,40";
        double step = arr.Length > 1 ? 160.0 / (arr.Length - 1) : 160;
        return string.Join(" ", arr.Select((v, i) => $"{i * step:0.0},{40 - Math.Clamp(v, 0, scale) * 0.36:0.0}"));
    }
    public HardwareSnapshot? Hardware { get => _hardware; private set => Set(ref _hardware, value); }

    public int TotalCount => Items.Count;
    public int CatalogCount => Items.Count;
    public int ImplementedCount => Items.Count(item => item.IsImplemented);
    public int PendingCount => CatalogCount - ImplementedCount;
    public int AdjustmentCount => Items.Count(item => item.IsImplemented && !item.IsTool);
    public int ToolCount => Items.Count(item => item.IsImplemented && item.IsTool);
    public int GreenCount => Items.Count(i => i.RiskLevel == RiskLevel.Green);
    public int YellowCount => Items.Count(i => i.RiskLevel == RiskLevel.Yellow);
    public int RedCount => Items.Count(i => i.RiskLevel == RiskLevel.Red);
    public int ActiveCount => Items.Count(i => i.IsImplemented && !i.IsTool && i.IsEnabled);
    public int BackedUpCount => _backedUpIds.Count;
    public int GreenFilterCount => GreenCount;
    public int YellowFilterCount => YellowCount;
    public int RedFilterCount => RedCount;

    // ===== Dashboard =====
    public string CpuName => Hardware?.CpuName ?? "Detectando hardware…";
    public string CpuCores => Hardware is null ? "—" : $"{Hardware.PhysicalCores} físicos / {Hardware.LogicalProcessors} lógicos";
    public string X3dNotice => Hardware?.IsDualCcdX3d == true
        ? "Ryzen X3D de doble CCD: conserva Game Bar para el aparcado de núcleos."
        : "";
    public double MemoryTotalGb => _memoryTotalGb > 0 ? _memoryTotalGb : Hardware?.MemoryGb ?? 0;
    public double MemoryAvailableGb => _memoryAvailableGb;
    public double MemoryUsagePercent { get => _memoryUsagePercent; private set => Set(ref _memoryUsagePercent, value); }
    public string GraphicsModel => Hardware is null ? "Detectando…" :
        Hardware.GraphicsAdapters.Count == 0 ? "Adaptador no identificado" : string.Join(", ", Hardware.GraphicsAdapters);
    public string HagsStatus => Hardware?.SupportsHardwareGpuScheduling switch
    {
        true => Hardware.IsHardwareGpuSchedulingEnabled == true ? "HAGS compatible y activo" : "HAGS compatible, desactivado",
        false => "HAGS no compatible",
        _ => "HAGS: no se pudo consultar"
    };
    public string WindowsInfo => Hardware is null ? "Detectando…" : $"{Hardware.WindowsVersion} · {Hardware.WindowsEdition} (compilación {Hardware.WindowsBuild}.{Hardware.WindowsUbr})";
    public string DriveInfoText => Hardware?.SystemDriveType ?? "—";
    public string FormFactorText => Hardware is null ? "—" : Hardware.HasBattery ? "Portátil (batería detectada)" : "Desktop / sin batería";
    public string AntiCheatBadges => Hardware is null ? "Detectando…" : string.Join(" · ", new[]
    {
        Hardware.HasVanguard ? "Vanguard" : null,
        Hardware.HasFaceit ? "FACEIT AC" : null,
        Hardware.HasEasyAntiCheat ? "EAC" : null,
        Hardware.HasBattlEye ? "BattlEye" : null
    }.Where(name => name is not null).Cast<string>().DefaultIfEmpty("Sin anticheat detectado"));

    public string HardwareSummary => Hardware is null ? "Detectando hardware…" :
        $"{Hardware.CpuName} · {Hardware.PhysicalCores} núcleos / {Hardware.LogicalProcessors} hilos · {Hardware.MemoryGb:0.#} GB RAM" +
        (Hardware.IsDualCcdX3d ? " · Ryzen X3D doble CCD detectado" : "");
    public string GraphicsSummary => Hardware is null ? "" :
        (Hardware.GraphicsAdapters.Count == 0 ? "GPU: no se pudo leer el nombre del adaptador" : "GPU: " + string.Join(", ", Hardware.GraphicsAdapters)) +
        " · HAGS: " + (Hardware.SupportsHardwareGpuScheduling switch { true => Hardware.IsHardwareGpuSchedulingEnabled == true ? "compatible/activo" : "compatible/desactivado", false => "no compatible", _ => "no se pudo consultar" });
    public string PlatformSummary => Hardware is null ? "" :
        $"{Hardware.WindowsVersion} · Batería: {(Hardware.HasBattery ? "sí" : "no detectada")} · Unidad del sistema: {Hardware.SystemDriveType}";
    public string AntiCheatSummary => Hardware is null ? "" : FormatAntiCheat(Hardware);

    // ===== Progreso de operaciones masivas =====
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public int OperationProgress { get => _operationProgress; private set => Set(ref _operationProgress, value); }
    public string OperationStatus { get => _operationStatus; private set => Set(ref _operationStatus, value); }
    public bool IsCompetitiveMode => Items.FirstOrDefault(i => i.Id == 206)?.IsEnabled == true;
    public bool IsLogExpanded
    {
        get => _isLogExpanded;
        private set
        {
            if (!Set(ref _isLogExpanded, value)) return;
            OnPropertyChanged(nameof(LogToggleLabel));
            OnPropertyChanged(nameof(LogListHeight));
        }
    }
    public double LogListHeight => _isLogExpanded ? 110 : 0;
    public string LogToggleLabel => _isLogExpanded ? "▼ Ocultar consola" : "▲ Mostrar consola";

    public async Task InitializeAsync()
    {
        try
        {
            _daemon ??= new ProcessLassoNativeService();
            _daemon.Start();
        }
        catch (Exception ex) { AddLog($"No se pudo iniciar el demonio nativo: {ex.Message}"); }

        // El escaneo de hardware corre en paralelo mientras se carga y sincroniza el catálogo.
        Task<HardwareSnapshot> hardwareTask = new HardwareAndAnticheatScanner().ScanAsync();

        try
        {
            IReadOnlyList<OptimizationItem> loaded = await new CatalogProvider().LoadAsync();
            Items.Clear();
            _backedUpIds.Clear();

            // 1. Publicar los 227 ajustes de inmediato para que la UI responda al instante.
            foreach (OptimizationItem item in loaded)
            {
                item.PropertyChanged += async (_, args) =>
                {
                    if (args.PropertyName == nameof(OptimizationItem.IsEnabled) && !_suppressToggle.Contains(item.Id))
                        await OnToggleChangedAsync(item, item.IsEnabled);
                    else if (args.PropertyName == nameof(OptimizationItem.SelectedValue) && !_suppressOptionRefresh.Contains(item.Id))
                        await RefreshItemStateAsync(item);
                };
                Items.Add(item);
            }
            OnPropertyChanged(nameof(CatalogCount));
            OnPropertyChanged(nameof(ImplementedCount));
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(AdjustmentCount));
            OnPropertyChanged(nameof(ToolCount));
            OnPropertyChanged(nameof(GreenCount));
            OnPropertyChanged(nameof(YellowCount));
            OnPropertyChanged(nameof(RedCount));
            AddLog($"Catálogo cargado: {Items.Count} ajustes documentados.");

            // 2. Verificar el estado real en Windows con concurrencia controlada (8 operaciones).
            using var gate = new SemaphoreSlim(8, 8);
            var checks = loaded
                .Where(item => item.IsImplemented && !item.IsTool)
                .Select(async item =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        bool applied = await _executionEngine.IsAppliedAsync(item);
                        bool hasBackup = await _executionEngine.HasBackupAsync(item);
                        bool hasDefault = await _executionEngine.HasOfficialDefaultAsync(item);
                        string defaultText = await _executionEngine.ReadOfficialDefaultTextAsync(item);
                        string appliedOption = _executionEngine.DetectAppliedOption(item);
                        int optionIndex = _executionEngine.DetectOptionIndex(item);
                        string currentValue = _executionEngine.ReadCurrentValue(item);
                        return (item, applied, hasBackup, hasDefault, defaultText, appliedOption, optionIndex, currentValue,
                            error: (string?)null);
                    }
                    catch (Exception ex)
                    {
                        return (item, applied: false, hasBackup: false, hasDefault: false, defaultText: "—",
                            appliedOption: "(no legible)", optionIndex: -1, currentValue: "—",
                            error: ex.Message);
                    }
                    finally { gate.Release(); }
                }).ToArray();

            var results = await Task.WhenAll(checks);
            foreach (var r in results)
            {
                _suppressToggle.Add(r.item.Id);
                try { r.item.IsEnabled = r.applied; }
                finally { _suppressToggle.Remove(r.item.Id); }
                if (r.hasBackup) _backedUpIds.Add(r.item.Id);
                r.item.HasBackup = r.hasBackup;
                r.item.HasDefault = r.hasDefault;
                r.item.DefaultValueText = r.defaultText;
                r.item.AppliedOptionText = r.appliedOption;
                r.item.CurrentValueText = r.currentValue;
                if (r.optionIndex >= 0 && r.optionIndex < r.item.Options.Count)
                {
                    _suppressOptionRefresh.Add(r.item.Id);
                    try { r.item.SelectedValue = r.item.Options[r.optionIndex]; }
                    finally { _suppressOptionRefresh.Remove(r.item.Id); }
                }
                r.item.Status = BuildStatus(r.item, r.applied, r.hasBackup);
                if (r.error is not null) AddLog($"#{r.item.Id} estado no legible sin permisos: {r.error}");
            }
            RefreshCounts();
            FilteredItems.Refresh();
        }
        catch (Exception ex) { AddLog($"No se pudo cargar el catálogo: {ex.Message}"); }

        try
        {
            Hardware = await hardwareTask;
            OnPropertyChanged(nameof(HardwareSummary));
            OnPropertyChanged(nameof(GraphicsSummary));
            OnPropertyChanged(nameof(PlatformSummary));
            OnPropertyChanged(nameof(AntiCheatSummary));
            OnPropertyChanged(nameof(CpuName));
            OnPropertyChanged(nameof(CpuCores));
            OnPropertyChanged(nameof(X3dNotice));
            OnPropertyChanged(nameof(GraphicsModel));
            OnPropertyChanged(nameof(HagsStatus));
            OnPropertyChanged(nameof(WindowsInfo));
            OnPropertyChanged(nameof(DriveInfoText));
            OnPropertyChanged(nameof(FormFactorText));
            OnPropertyChanged(nameof(AntiCheatBadges));
            foreach (OptimizationItem tool in Items.Where(item => item.IsTool))
                tool.Status = tool.Id == 153
                    ? "Visor disponible; muestra el árbol USB/HID Plug and Play actual."
                    : "Herramienta nativa disponible · Clic para abrir";
            AddLog("Escaneo de hardware completado. Se usará para evaluar aplicabilidad; no se fija ningún modelo de PC.");
        }
        catch (Exception ex) { AddLog($"El escaneo terminó con datos parciales: {ex.Message}"); }

        RefreshMemoryUsage();
        RefreshCounts();
    }
    public void AddLog(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.Invoke(() => Logs.Insert(0, line));
        }
        else
        {
            Logs.Insert(0, line);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
            File.AppendAllText(_logFile, line + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public void ClearLog()
    {
        Logs.Clear();
        try { File.WriteAllText(_logFile, ""); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ===== Memoria RAM en vivo (P/Invoke nativo existente en la app) =====
    public void RefreshMemoryUsage()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        try
        {
            if (!GlobalMemoryStatusEx(ref status)) return;
            double total = status.TotalPhysical / 1073741824d;
            double available = status.AvailablePhysical / 1073741824d;
            _memoryTotalGb = total;
            _memoryAvailableGb = available;
            MemoryUsagePercent = total > 0 ? Math.Min(100, Math.Max(0, (1 - available / total) * 100)) : 0;
            OnPropertyChanged(nameof(MemoryTotalGb));
            OnPropertyChanged(nameof(MemoryAvailableGb));
        }
        catch (Exception ex) { AddLog($"No se pudo leer el uso de memoria: {ex.Message}"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    // ===== Herramientas y servicios (firmas usadas por Views/*) =====
    public async Task<string> GetToolContentAsync(OptimizationItem item)
    {
        if (item.Id == 153)
            return Hardware is null ? "El escaneo de hardware aún no está disponible." : string.Join(Environment.NewLine, Hardware.UsbTopology);
        if (item.Id == 148)
            return await Task.Run(TimingDiagnostics.MeasureSleepOne);
        if (item.Id == 201)
            return await BypassIoDiagnostics.QueryAsync();
        if (item.Id == 207)
            return await _executionEngine.GetBackupIndexSummaryAsync();
        if (item.Id == 210)
            return await RefreshStatusesAsync();
        return "Esta herramienta aún no tiene una vista asociada.";
    }

    public async Task<string> RunToolAsync(int id, string action, string input)
    {
        string result = await _executionEngine.RunToolAsync(id, action, input);
        AddLog($"Herramienta #{id} · {action}: ejecutada.");
        return result;
    }

    public Task<IReadOnlyList<ServiceManagerEntry>> ListServiceManagerEntriesAsync(bool thirdPartyOnly, CancellationToken cancellationToken = default) =>
        _executionEngine.ListServiceManagerEntriesAsync(thirdPartyOnly, cancellationToken);

    public async Task<ExecutionResult> SetServiceModeAsync(string name, string mode, CancellationToken cancellationToken = default)
    {
        ExecutionResult result = await _executionEngine.SetServiceModeAsync(name, mode, cancellationToken);
        AddLog($"Gestor de servicios · {name}: {result.Message}");
        return result;
    }

    public Task<IReadOnlyList<StartupEntry>> ListStartupEntriesAsync(CancellationToken cancellationToken = default) =>
        _executionEngine.ListStartupEntriesAsync(cancellationToken);

    public async Task<ExecutionResult> SetStartupEntryEnabledAsync(StartupEntry entry, bool enabled, CancellationToken cancellationToken = default)
    {
        ExecutionResult result = await _executionEngine.SetStartupEntryEnabledAsync(entry, enabled, cancellationToken);
        AddLog($"Gestor de arranque · {entry.Name}: {result.Message}");
        return result;
    }

    public Task<IReadOnlyList<GpuPreferenceEntry>> GetGpuPreferenceEntriesAsync(CancellationToken cancellationToken = default) =>
        _executionEngine.GetGpuPreferenceEntriesAsync(cancellationToken);

    public Task<IReadOnlyList<GameCompatibilityEntry>> GetGameCompatibilityEntriesAsync(CancellationToken cancellationToken = default) =>
        _executionEngine.GetGameCompatibilityEntriesAsync(cancellationToken);

    public Task<IReadOnlyList<GamePriorityStatus>> GetGamePriorityStatusAsync(string exePath, CancellationToken cancellationToken = default) =>
        _executionEngine.GetGamePriorityStatusAsync(exePath, cancellationToken);

    public async Task<ExecutionResult> SetGamePriorityAsync(string exePath, int optimizationId, bool enabled, CancellationToken cancellationToken = default)
    {
        ExecutionResult result = await _executionEngine.SetGamePriorityAsync(exePath, optimizationId, enabled, cancellationToken);
        AddLog($"Prioridad IFEO {Path.GetFileName(exePath)} #{optimizationId}: {result.Message}");
        return result;
    }

    public async Task<ExecutionResult> SetGpuPreferenceAsync(string exePath, bool highPerformance, CancellationToken cancellationToken = default)
    {
        ExecutionResult result = await _executionEngine.SetGpuPreferenceAsync(exePath, highPerformance, cancellationToken);
        AddLog($"Preferencia de GPU {Path.GetFileName(exePath)}: {result.Message}");
        return result;
    }

    public async Task<ExecutionResult> SetGameCompatibilityAsync(string exePath, bool enabled, CancellationToken cancellationToken = default)
    {
        ExecutionResult result = await _executionEngine.SetGameCompatibilityAsync(exePath, enabled, cancellationToken);
        AddLog($"Compatibilidad FSO/DPI {Path.GetFileName(exePath)}: {result.Message}");
        return result;
    }

    public Task<ShaderCacheStatus> GetShaderCacheStatusAsync(CancellationToken cancellationToken = default) =>
        _executionEngine.GetShaderCacheStatusAsync(cancellationToken);

    public async Task<ExecutionResult> QuarantineShaderCachesAsync(CancellationToken cancellationToken = default)
    {
        ExecutionResult result = await _executionEngine.QuarantineShaderCachesAsync(cancellationToken);
        AddLog($"Caché de shaders: {result.Message}");
        return result;
    }

    public async Task<ExecutionResult> RestoreShaderCachesAsync(CancellationToken cancellationToken = default)
    {
        ExecutionResult result = await _executionEngine.RestoreShaderCachesAsync(cancellationToken);
        AddLog($"Caché de shaders: {result.Message}");
        return result;
    }

    public Task<IReadOnlyList<DisplayRefreshOption>> GetDisplayRefreshOptionsAsync(CancellationToken cancellationToken = default) =>
        _executionEngine.GetDisplayRefreshOptionsAsync(cancellationToken);

    public async Task<ExecutionResult> BeginDisplayRefreshPreviewAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        ExecutionResult result = await _executionEngine.BeginDisplayRefreshPreviewAsync(deviceName, cancellationToken);
        AddLog($"Frecuencia de pantalla {deviceName}: {result.Message}");
        return result;
    }

    public async Task<ExecutionResult> ConfirmDisplayRefreshPreviewAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        ExecutionResult result = await _executionEngine.ConfirmDisplayRefreshPreviewAsync(deviceName, cancellationToken);
        AddLog($"Frecuencia de pantalla {deviceName}: {result.Message}");
        return result;
    }

    public async Task<ExecutionResult> RestoreDisplayRefreshAsync(string deviceName, bool onlyIfTargetStillCurrent = false, CancellationToken cancellationToken = default)
    {
        ExecutionResult result = await _executionEngine.RestoreDisplayRefreshAsync(deviceName, onlyIfTargetStillCurrent, cancellationToken);
        AddLog($"Frecuencia de pantalla {deviceName}: {result.Message}");
        return result;
    }

    public async Task<string> RefreshStatusesAsync(CancellationToken cancellationToken = default)
    {
        int checkedCount = 0, activeCount = 0, backedUpCount = 0, failures = 0;
        foreach (OptimizationItem item in Items.Where(item => item.IsImplemented && !item.IsTool))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool applied = await _executionEngine.IsAppliedAsync(item, cancellationToken);
                bool backup = await _executionEngine.HasBackupAsync(item, cancellationToken);
                _suppressToggle.Add(item.Id);
                item.IsEnabled = applied;
                _suppressToggle.Remove(item.Id);
                if (backup) _backedUpIds.Add(item.Id); else _backedUpIds.Remove(item.Id);
                item.Status = applied
                    ? backup ? "Activo y respaldado por ApexKernel; se puede restaurar." : "Ya estaba activo en Windows; ApexKernel no tiene el estado anterior para restaurarlo."
                    : "Sin aplicar; se guardará el estado previo al activarlo.";
                checkedCount++;
                if (applied) activeCount++;
                if (backup) backedUpCount++;
            }
            catch (Exception ex)
            {
                _suppressToggle.Remove(item.Id);
                failures++;
                item.Status = $"No se pudo consultar: {ex.Message}";
            }
        }
        string result = $"Revisión completada: {checkedCount} ajustes, {activeCount} activos, {backedUpCount} con respaldo y {failures} errores de consulta.";
        AddLog(result);
        RefreshCounts();
        return result;
    }

    public async Task ExportProfileAsync(string filePath)
    {
        var profile = new OptimizationProfile
        {
            EnabledIds = Items.Where(item => item.IsImplemented && item.IsEnabled).Select(item => item.Id).Order().ToList()
        };
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        AddLog($"Perfil exportado ({profile.EnabledIds.Count} ajustes disponibles). ");
    }

    public async Task ImportProfileAsync(string filePath)
    {
        OptimizationProfile? profile = JsonSerializer.Deserialize<OptimizationProfile>(await File.ReadAllTextAsync(filePath));
        if (profile is null || profile.Version != 1) throw new InvalidDataException("El perfil no es compatible con esta versión.");
        HashSet<int> enabledIds = profile.EnabledIds.ToHashSet();
        foreach (OptimizationItem item in Items.Where(item => item.IsImplemented && !item.IsTool))
        {
            bool desired = enabledIds.Contains(item.Id);
            if (desired == item.IsEnabled) continue;
            _suppressToggle.Add(item.Id);
            item.IsEnabled = desired;
            _suppressToggle.Remove(item.Id);
            await OnToggleChangedAsync(item, desired);
        }
        int unsupported = enabledIds.Count(id => !Items.Any(item => item.Id == id && item.IsImplemented));
        AddLog($"Perfil importado. Ajustes no disponibles omitidos: {unsupported}.");
        RefreshCounts();
    }

    // ===== Acciones masivas del Panel de Control =====
    private async Task ApplyAllSafeAsync()
    {
        if (IsBusy) return;
        if (ConfirmBatchAction?.Invoke("Se creará un punto de restauración y después se aplicarán todos los ajustes verdes (seguros) compatibles con este hardware. ¿Continuar?") != true)
        {
            AddLog("Aplicación masiva de ajustes seguros cancelada por el usuario.");
            return;
        }
        IsBusy = true;
        try
        {
            OperationStatus = "Creando punto de restauración…";
            OperationProgress = 0;
            if (!await CreateRestorePointAsync(confirm: false))
            {
                OperationStatus = "Aplicación masiva detenida: no se creó el punto de restauración.";
                AddLog(OperationStatus);
                return;
            }
            List<OptimizationItem> targets = Items
                .Where(i => i.IsImplemented && !i.IsTool && i.RiskLevel == RiskLevel.Green && !i.IsEnabled && i.Id != 17 && IsHardwareApplicable(i))
                .ToList();
            int applied = 0, failed = 0;
            for (int index = 0; index < targets.Count; index++)
            {
                OptimizationItem item = targets[index];
                OperationStatus = $"Aplicando #{item.Id} · {item.Title} ({index + 1}/{targets.Count})";
                OperationProgress = (index + 1) * 100 / Math.Max(1, targets.Count);
                ExecutionResult result = await ApplyToggleAsync(item, true);
                if (result.Success) { applied++; if (await _executionEngine.HasBackupAsync(item)) _backedUpIds.Add(item.Id); }
                else failed++;
            }
            OperationStatus = $"Lote seguro completado: {applied} aplicados, {failed} con errores.";
            AddLog(OperationStatus);
            RefreshCounts();
        }
        catch (Exception ex) { AddLog($"No se pudo completar el lote seguro: {ex.Message}"); OperationStatus = "Error en el lote seguro."; }
        finally { IsBusy = false; RefreshCounts(); }
    }

    private async Task RevertBackedUpAsync()
    {
        if (IsBusy) return;
        List<OptimizationItem> targets = Items.Where(i => i.IsImplemented && !i.IsTool && i.IsEnabled && _backedUpIds.Contains(i.Id)).ToList();
        if (targets.Count == 0) { AddLog("No hay ajustes activos con respaldo de ApexKernel para revertir."); return; }
        if (ConfirmBatchAction?.Invoke($"Se restaurarán {targets.Count} ajustes activos que tienen respaldo exacto en state_backup.json. ¿Continuar?") != true)
        {
            AddLog("Reversión masiva cancelada por el usuario.");
            return;
        }
        IsBusy = true;
        try
        {
            int reverted = 0, failed = 0;
            for (int index = 0; index < targets.Count; index++)
            {
                OptimizationItem item = targets[index];
                OperationStatus = $"Restaurando #{item.Id} · {item.Title} ({index + 1}/{targets.Count})";
                OperationProgress = (index + 1) * 100 / Math.Max(1, targets.Count);
                ExecutionResult result = await ApplyToggleAsync(item, false);
                if (result.Success) { reverted++; _backedUpIds.Remove(item.Id); }
                else failed++;
            }
            OperationStatus = $"Reversión completada: {reverted} restaurados, {failed} con errores.";
            AddLog(OperationStatus);
        }
        catch (Exception ex) { AddLog($"No se pudo completar la reversión: {ex.Message}"); OperationStatus = "Error en la reversión."; }
        finally { IsBusy = false; RefreshCounts(); }
    }

    // ===== Perfiles generales de optimización =====
    private async Task ExecuteAlmostAllProfileAsync()
    {
        IReadOnlyList<int> ids = Items
            .Where(i => i.IsImplemented && !i.IsTool && !AlmostAllDeniedIds.Contains(i.Id) &&
                        i.RiskLevel is RiskLevel.Green or RiskLevel.Yellow)
            .Select(i => i.Id)
            .ToList();
        await ExecuteProfileAsync("almost-all", ids,
            "Perfil 'Aplicar casi todo': aplicará todos los ajustes verdes y ámbar compatibles con este hardware, salvo la lista de exclusión de seguridad/estabilidad.",
            AlmostAllWarningText);
    }

    private async Task ExecuteProfileAsync(string key, IReadOnlyList<int> ids, string description, string? specialWarning = null)
    {
        if (IsBusy) return;
        List<OptimizationItem> candidates = GetProfileCandidates(ids);
        if (candidates.Count == 0)
        {
            OperationStatus = $"Perfil '{ProfileName(key)}': no hay ajustes pendientes que aplicar en este equipo.";
            AddLog(OperationStatus);
            return;
        }

        string restoreNote = key is "gaming" or "almost-all"
            ? " Se creará un punto de restauración antes de empezar."
            : "";
        string message = string.IsNullOrEmpty(specialWarning)
            ? $"{description}\n\nSe aplicarán {candidates.Count} ajuste(s).{restoreNote} ¿Continuar?"
            : $"{specialWarning}\n\n{description}\n\nSe aplicarán {candidates.Count} ajuste(s).{restoreNote} ¿Continuar?";
        if (ConfirmBatchAction?.Invoke(message) != true)
        {
            AddLog($"Perfil '{ProfileName(key)}' cancelado por el usuario; no se modificó ningún ajuste.");
            return;
        }
        await ApplyProfileAsync(key, ids);
    }

    private async Task ApplyProfileAsync(string key, IReadOnlyList<int> ids)
    {
        if (IsBusy) return;
        List<OptimizationItem> targets = GetProfileCandidates(ids);
        string profileName = ProfileName(key);
        if (targets.Count == 0)
        {
            OperationStatus = $"Perfil '{profileName}': no hay ajustes pendientes que aplicar en este equipo.";
            AddLog(OperationStatus);
            return;
        }

        int skipped = ids.Distinct().Count() - targets.Count;
        IsBusy = true;
        AddLog($"Aplicando perfil '{profileName}': {targets.Count} ajuste(s) pendientes, {skipped} omitido(s).");
        try
        {
            if (key is "gaming" or "almost-all")
            {
                OperationStatus = $"'{profileName}': creando punto de restauración…";
                OperationProgress = 0;
                if (!await CreateRestorePointAsync(confirm: false))
                {
                    OperationStatus = $"Perfil '{profileName}' detenido: no se pudo crear el punto de restauración.";
                    AddLog(OperationStatus);
                    return;
                }
            }

            int applied = 0, failed = 0;
            var failedIds = new List<int>();
            for (int index = 0; index < targets.Count; index++)
            {
                OptimizationItem item = targets[index];
                OperationStatus = $"'{profileName}' · #{item.Id} {item.Title} ({index + 1}/{targets.Count})";
                OperationProgress = (index + 1) * 100 / targets.Count;
                ExecutionResult result = await ApplyToggleAsync(item, true);
                if (result.Success)
                {
                    applied++;
                    if (await _executionEngine.HasBackupAsync(item)) _backedUpIds.Add(item.Id);
                }
                else
                {
                    failed++;
                    failedIds.Add(item.Id);
                }
            }

            string summary = $"Perfil '{profileName}' completado: {applied} aplicados, {skipped} omitidos, {failed} fallidos." +
                (failedIds.Count > 0 ? $" IDs fallidos: {string.Join(", ", failedIds)}." : "");
            OperationStatus = summary;
            OperationProgress = 100;
            AddLog(summary);
        }
        catch (Exception ex)
        {
            OperationStatus = $"Error al aplicar el perfil '{profileName}': {ex.Message}";
            AddLog(OperationStatus);
        }
        finally { IsBusy = false; RefreshCounts(); }
    }

    private List<OptimizationItem> GetProfileCandidates(IReadOnlyList<int> ids)
    {
        HashSet<int> wanted = ids.ToHashSet();
        return Items
            .Where(i => wanted.Contains(i.Id) && !i.IsTool && i.IsImplemented && !i.IsEnabled &&
                        i.IsHardwareApplicable && IsHardwareApplicable(i))
            .ToList();
    }

    private static string ProfileName(string key) => key switch
    {
        "normal" => "Uso normal (equilibrado)",
        "gaming" => "Modo gaming (baja latencia)",
        _ => "Aplicar casi todo (NO RECOMENDADO)"
    };

    private async Task CreateRestorePointFromUiAsync()
    {
        if (IsBusy) return;
        if (ConfirmBatchAction?.Invoke("Se creará un punto de restauración de Windows antes de seguir modificando el sistema. ¿Continuar?") != true)
        {
            AddLog("Creación de punto de restauración cancelada por el usuario.");
            return;
        }
        await CreateRestorePointAsync(confirm: false);
    }

    private async Task<bool> CreateRestorePointAsync(bool confirm)
    {
        if (confirm && ConfirmBatchAction?.Invoke("Se creará un punto de restauración de Windows. ¿Continuar?") != true)
        {
            AddLog("Creación de punto de restauración cancelada por el usuario.");
            return false;
        }
        bool outerBusy = IsBusy;
        if (!outerBusy) IsBusy = true;
        try
        {
            OperationStatus = "Creando punto de restauración de Windows…";
            OperationProgress = 10;
            string output;
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'ApexKernel Optimizer' -RestorePointType 'MODIFY_SETTINGS'; if ($?) { 'OK' }\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process? process = Process.Start(psi);
            if (process is null)
            {
                output = "No se pudo iniciar PowerShell.";
            }
            else
            {
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                string text = (stdout + Environment.NewLine + stderr).Trim();
                output = process.ExitCode == 0 && text.Contains("OK", StringComparison.OrdinalIgnoreCase)
                    ? "Punto de restauración creado correctamente."
                    : "No se pudo crear el punto de restauración: " + (text.Length == 0 ? $"código de salida {process.ExitCode}." : text);
            }
            bool success = output.StartsWith("Punto de restauración creado", StringComparison.Ordinal);
            OperationStatus = output;
            OperationProgress = 100;
            AddLog(output);
            return success;
        }
        catch (Exception ex)
        {
            OperationStatus = $"No se pudo crear el punto de restauración: {ex.Message}";
            AddLog(OperationStatus);
            return false;
        }
        finally
        {
            if (!outerBusy) IsBusy = false;
        }
    }

    private async Task ToggleCompetitiveModeAsync()
    {
        if (IsBusy) return;
        OptimizationItem? item = Items.FirstOrDefault(i => i.Id == 206);
        if (item is null) { AddLog("No se encontró el conmutador de perfil #206 en el catálogo."); return; }
        bool desired = !item.IsEnabled;
        if (ConfirmBatchAction?.Invoke(desired
            ? "Se activará el plan de energía Competitivo (#206). ¿Continuar?"
            : "Se volverá al plan Eco/Estudio (#206). ¿Continuar?") != true)
        {
            AddLog("Cambio de perfil competitivo/eco cancelado por el usuario.");
            return;
        }
        IsBusy = true;
        try
        {
            OperationStatus = desired ? "Activando modo competitivo…" : "Activando modo Eco/Estudio…";
            OperationProgress = 30;
            ExecutionResult result = await ApplyToggleAsync(item, desired);
            OperationStatus = result.Message;
            OperationProgress = 100;
            OnPropertyChanged(nameof(IsCompetitiveMode));
            RefreshCounts();
        }
        catch (Exception ex) { AddLog($"No se pudo cambiar el perfil de energía: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    private async Task PurgeStandbyAsync()
    {
        if (IsBusy) return;
        OptimizationItem? item = Items.FirstOrDefault(i => i.Id == 29);
        if (item is null) { AddLog("No se encontró el limpiador de standby #29 en el catálogo."); return; }
        if (item.IsEnabled)
        {
            AddLog("#29 Limpiador Automático de Standby List ya está activo en el demonio nativo; purgará cuando la RAM disponible baje del umbral.");
            return;
        }
        if (ConfirmBatchAction?.Invoke("#29 activa el limpiador automático de la lista Standby incluido en el demonio nativo de ApexKernel (purga cuando la RAM disponible baja del umbral). ¿Activarlo?") != true)
        {
            AddLog("Activación del limpiador de standby cancelada por el usuario.");
            return;
        }
        IsBusy = true;
        try
        {
            ExecutionResult result = await ApplyToggleAsync(item, true);
            OperationStatus = result.Message;
            OperationProgress = 100;
            if (await _executionEngine.HasBackupAsync(item)) _backedUpIds.Add(item.Id);
            RefreshCounts();
        }
        catch (Exception ex) { AddLog($"No se pudo activar el limpiador de standby: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    private async Task SampleTelemetryAsync()
    {
        if (Interlocked.CompareExchange(ref _samplingTelemetry, 1, 0) != 0) return;
        try
        {
            RefreshMemoryUsage();
            TelemetrySample sample = await _telemetry.SampleAsync().ConfigureAwait(false);
            CpuLoad = sample.Cpu;
            GpuLoad = sample.Gpu;
            NetLatencyMs = sample.LatencyMs;
            DiskLoad = sample.Disk;
            ProcessCount = sample.Processes;
            Push(CpuHistory, sample.Cpu);
            Push(RamHistory, MemoryUsagePercent);
            Push(GpuHistory, sample.Gpu);
            Push(NetHistory, Math.Clamp(sample.LatencyMs < 0 ? 0 : sample.LatencyMs, 0, 200));
            Push(DiskHistory, sample.Disk);
            OnPropertyChanged(nameof(CpuPoints));
            OnPropertyChanged(nameof(RamPoints));
            OnPropertyChanged(nameof(GpuPoints));
            OnPropertyChanged(nameof(NetPoints));
            OnPropertyChanged(nameof(DiskPoints));
        }
        catch (Exception ex) { AddLog($"Telemetría no disponible: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _samplingTelemetry, 0); }
    }

    public void Dispose()
    {
        _telemetryTimer.Stop();
        _daemon?.Dispose();
        _daemon = null;
    }

    private static void Push(ObservableCollection<double> history, double value)
    {
        history.Add(value);
        while (history.Count > 60) history.RemoveAt(0);
    }

    private void ExportReport(string format)
    {
        try
        {
            IReadOnlyList<ReportService.ChangeRow> rows = ReportService.BuildRows(Items);
            string content = ReportService.Export(rows, format);
            string ext = format.ToLowerInvariant() switch { "json" => "json", "md" or "markdown" => "md", _ => "html" };
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexKernel", "Reports");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"apexkernel-cambios-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}");
            File.WriteAllText(path, content, Encoding.UTF8);
            AddLog($"Informe {ext.ToUpperInvariant()} exportado: {path} ({rows.Count} ajustes con respaldo).");
            OperationStatus = $"Informe exportado: {path}";
            try { Process.Start(new ProcessStartInfo("explorer.exe", Path.GetDirectoryName(path)!) { UseShellExecute = true }); }
            catch { }
        }
        catch (Exception ex) { AddLog($"No se pudo exportar el informe: {ex.Message}"); }
    }

    private void RefreshProValues()
    {
        foreach (OptimizationItem item in Items.Where(i => i.IsImplemented && !i.IsTool))
        {
            try { item.CurrentValueText = _executionEngine.ReadCurrentValue(item); }
            catch { item.CurrentValueText = "(no legible)"; }
        }
        AddLog($"Vista Pro: {Items.Count(i => i.IsImplemented && !i.IsTool)} valores actuales actualizados.");
    }

    private async Task ApplySelectedAsync()
    {
        if (IsBusy) return;
        List<OptimizationItem> targets = Items.Where(i => i.IsSelected && i.IsImplemented && !i.IsTool && !i.IsEnabled).ToList();
        if (targets.Count == 0) { OperationStatus = "No hay ajustes seleccionados para aplicar."; return; }
        if (ConfirmBatchAction?.Invoke($"¿Aplicar {targets.Count} ajustes seleccionados? Se respaldará el estado previo de cada uno.") != true) return;
        IsBusy = true;
        int ok = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                OptimizationItem item = targets[i];
                OperationProgress = (int)((i + 1) * 100.0 / targets.Count);
                OperationStatus = $"Aplicando #{item.Id} {item.Title}…";
                ExecutionResult result = await ApplyToggleAsync(item, true);
                if (result.Success) { ok++; if (await _executionEngine.HasBackupAsync(item)) _backedUpIds.Add(item.Id); }
            }
            RefreshProValues();
            RefreshCounts();
            OperationStatus = $"Aplicados {ok} de {targets.Count} ajustes seleccionados.";
        }
        catch (Exception ex) { AddLog($"Error al aplicar la selección: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    private async Task RevertSelectedAsync()
    {
        if (IsBusy) return;
        List<OptimizationItem> targets = Items.Where(i => i.IsSelected && _backedUpIds.Contains(i.Id)).ToList();
        if (targets.Count == 0) { OperationStatus = "No hay ajustes seleccionados con respaldo para restaurar."; return; }
        if (ConfirmBatchAction?.Invoke($"¿Restaurar {targets.Count} ajustes seleccionados a su estado original?") != true) return;
        IsBusy = true;
        int ok = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                OptimizationItem item = targets[i];
                OperationProgress = (int)((i + 1) * 100.0 / targets.Count);
                OperationStatus = $"Restaurando #{item.Id} {item.Title}…";
                ExecutionResult result = await ApplyToggleAsync(item, false);
                if (result.Success) { ok++; _backedUpIds.Remove(item.Id); }
            }
            RefreshProValues();
            RefreshCounts();
            OperationStatus = $"Restaurados {ok} de {targets.Count} ajustes seleccionados.";
        }
        catch (Exception ex) { AddLog($"Error al restaurar la selección: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    private static string BuildStatus(OptimizationItem item, bool applied, bool hasBackup)
    {
        if (!applied)
            return $"Actual: {item.AppliedOptionText} · la opción elegida «{item.SelectedValue}» escribiría {item.TargetValueText}.";
        return hasBackup
            ? $"Activo: {item.AppliedOptionText} · se puede Restaurar o usar Defecto."
            : $"Activo: {item.AppliedOptionText} · Defecto restaura el valor oficial.";
    }

    /// <summary>Recalcula si el ajuste está aplicado para la opción actualmente elegida.</summary>
    private async Task RefreshItemStateAsync(OptimizationItem item)
    {
        try
        {
            bool applied = await _executionEngine.IsAppliedAsync(item);
            _suppressToggle.Add(item.Id);
            try { item.IsEnabled = applied; }
            finally { _suppressToggle.Remove(item.Id); }
            item.HasBackup = await _executionEngine.HasBackupAsync(item);
            item.HasDefault = await _executionEngine.HasOfficialDefaultAsync(item);
            item.CurrentValueText = _executionEngine.ReadCurrentValue(item);
            item.AppliedOptionText = _executionEngine.DetectAppliedOption(item);
            item.Status = BuildStatus(item, applied, item.HasBackup);
        }
        catch (Exception ex)
        {
            item.Status = $"No se pudo comprobar el estado: {ex.Message}";
        }
    }

    private async Task ApplyItemAsync(OptimizationItem? item)
    {
        if (item is null || IsBusy) return;
        if (ConfirmBatchAction?.Invoke($"¿Aplicar el ajuste #{item.Id} ({item.Title})? Se guardará el estado previo.") != true) return;
        IsBusy = true;
        try
        {
            ExecutionResult result = await ApplyToggleAsync(item, true);
            OperationStatus = result.Message;
            await RefreshItemStateAsync(item);
            RefreshCounts();
        }
        catch (Exception ex) { AddLog($"Error al aplicar #{item.Id}: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    private async Task RestoreItemAsync(OptimizationItem? item)
    {
        if (item is null || IsBusy) return;
        if (ConfirmBatchAction?.Invoke($"¿Restaurar el ajuste #{item.Id} a su estado anterior?") != true) return;
        IsBusy = true;
        try
        {
            ExecutionResult result = await ApplyToggleAsync(item, false);
            OperationStatus = result.Message;
            await RefreshItemStateAsync(item);
            RefreshCounts();
        }
        catch (Exception ex) { AddLog($"Error al restaurar #{item.Id}: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    private async Task SetDefaultItemAsync(OptimizationItem? item)
    {
        if (item is null || IsBusy) return;
        string def = await _executionEngine.ReadOfficialDefaultTextAsync(item);
        if (ConfirmBatchAction?.Invoke($"¿Dejar #{item.Id} en su valor oficial de Windows?\n{def}") != true) return;
        IsBusy = true;
        try
        {
            _suppressToggle.Add(item.Id);
            ExecutionResult result;
            try { result = await _executionEngine.SetDefaultAsync(item); }
            finally { _suppressToggle.Remove(item.Id); }
            item.Status = result.Message;
            await RefreshItemStateAsync(item);
            OperationStatus = result.Message;
            AddLog($"#{item.Id} Defecto: {result.Message}");
            RefreshProValues();
            RefreshCounts();
        }
        catch (Exception ex) { AddLog($"Error al aplicar Defecto en #{item.Id}: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    private async Task<ExecutionResult> ApplyToggleAsync(OptimizationItem item, bool enabled)
    {
        _suppressToggle.Add(item.Id);
        try { item.IsEnabled = enabled; }
        finally { _suppressToggle.Remove(item.Id); }
        ExecutionResult result = await _executionEngine.SetEnabledAsync(item, enabled, IsHardwareApplicable(item));
        item.Status = result.Message;
        AddLog($"#{item.Id} {item.Title}: {result.Message}");
        return result;
    }

    private async Task OnToggleChangedAsync(OptimizationItem item, bool enabled)
    {
        // Protección contextual ANTES de aplicar cualquier cambio.
        string? contextWarning = null;
        if (enabled && (Hardware?.HasVanguard == true || Hardware?.HasFaceit == true) && item.Id is 35 or 36 or 136 or 138)
            contextWarning = "Riot Vanguard / FACEIT AC detectado en este PC. Desactivar VBS/HVCI, las mitigaciones Spectre/Meltdown, la lista de bloqueo de drivers o DEP/NX puede bloquear Valorant o FACEIT hasta que reviertas el ajuste; vuelve a activarlo para jugar.";
        if (enabled && Hardware?.IsDualCcdX3d == true && item.Id == 88)
            contextWarning = "Ryzen X3D de doble CCD detectado: desactivar Xbox Game Bar perjudica el aparcado de núcleos (core parking) y reduce FPS en este tipo de procesador.";

        bool needsConfirm = enabled && (item.RiskLevel != RiskLevel.Green || contextWarning is not null);
        if (needsConfirm)
        {
            string warning = string.IsNullOrWhiteSpace(item.WarningMessage)
                ? "El ajuste puede cambiar funciones o preferencias de Windows."
                : item.WarningMessage!;
            string message = contextWarning is null ? warning : contextWarning + "\n\n" + warning;
            if (ConfirmRiskChange?.Invoke(item, message) != true)
            {
                _suppressToggle.Add(item.Id);
                item.IsEnabled = false;
                _suppressToggle.Remove(item.Id);
                AddLog($"#{item.Id} {item.Title}: no se aplicó; confirmación de riesgo cancelada.");
                return;
            }
            if (contextWarning is not null) AddLog($"#{item.Id} {item.Title}: confirmación contextual aceptada — {contextWarning}");
        }

        bool hardwareApplicable = IsHardwareApplicable(item);
        ExecutionResult result;
        if (item.Id == 17 && enabled)
        {
            int? selected = SelectPrioritySeparationProfile?.Invoke();
            if (selected is null)
            {
                _suppressToggle.Add(item.Id);
                item.IsEnabled = false;
                _suppressToggle.Remove(item.Id);
                AddLog("#17 Win32PrioritySeparation: selección cancelada; no se modificó Windows.");
                return;
            }
            result = await _executionEngine.SetPrioritySeparationAsync(item, true, selected.Value);
        }
        else result = await _executionEngine.SetEnabledAsync(item, enabled, hardwareApplicable);
        item.Status = result.Message;
        AddLog($"#{item.Id} {item.Title}: {result.Message}");
        if (result.Success)
        {
            if (enabled && await _executionEngine.HasBackupAsync(item)) _backedUpIds.Add(item.Id);
            else if (!enabled) _backedUpIds.Remove(item.Id);
            OnPropertyChanged(nameof(IsCompetitiveMode));
        }
        if (!result.Success)
        {
            _suppressToggle.Add(item.Id);
            item.IsEnabled = !enabled;
            _suppressToggle.Remove(item.Id);
        }
        RefreshCounts();
    }

    private bool IsHardwareApplicable(OptimizationItem item) => item.Id switch
    {
        46 => Hardware is not null && !Hardware.HasBattery,
        52 => Hardware?.SupportsHardwareGpuScheduling == true,
        193 => (Hardware?.MemoryGb ?? 0) >= 16,
        188 => Hardware is not null && IsWerPolicyEdition(Hardware.WindowsEdition),
        89 => Hardware is not null && Hardware.WindowsBuild >= 15063 && IsWerPolicyEdition(Hardware.WindowsEdition),
        97 => Hardware is not null && Hardware.WindowsBuild >= 22000 && IsWerPolicyEdition(Hardware.WindowsEdition),
        156 => Hardware is not null && Hardware.WindowsBuild >= 17134 && IsWerPolicyEdition(Hardware.WindowsEdition),
        161 => Hardware is not null && Hardware.WindowsBuild >= 22000 && IsWerPolicyEdition(Hardware.WindowsEdition),
        169 => Hardware is not null && IsSupportedNoLockScreen(Hardware),
        21 or 22 or 80 => Hardware is not null && Hardware.PhysicalCores >= 6,
        28 => (Hardware?.MemoryGb ?? 0) >= 16,
        119 => (Hardware?.MemoryGb ?? 0) >= 8,
        186 => Hardware is not null && Hardware.SystemDriveType.Contains("SSD", StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private static bool IsWerPolicyEdition(string edition) =>
        edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase) ||
        edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase) ||
        edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) ||
        edition.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedNoLockScreen(HardwareSnapshot hardware) =>
        IsWerPolicyEdition(hardware.WindowsEdition) &&
        (hardware.WindowsBuild >= 22000 ||
         hardware.WindowsBuild > 19041 && hardware.WindowsBuild < 22000 ||
         hardware.WindowsBuild == 19041 && hardware.WindowsUbr >= 1202);

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CatalogCount));
        OnPropertyChanged(nameof(GreenCount));
        OnPropertyChanged(nameof(YellowCount));
        OnPropertyChanged(nameof(RedCount));
        OnPropertyChanged(nameof(GreenFilterCount));
        OnPropertyChanged(nameof(YellowFilterCount));
        OnPropertyChanged(nameof(RedFilterCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(BackedUpCount));
        OnPropertyChanged(nameof(IsCompetitiveMode));
        foreach (CategoryNavItem nav in CategoryNav)
        {
            nav.Count = Items.Count(i => i.Category == nav.Category);
            nav.ActiveCount = Items.Count(i => i.Category == nav.Category && i.IsImplemented && !i.IsTool && i.IsEnabled);
        }
    }

    private bool MatchesFilter(object value)
    {
        if (value is not OptimizationItem item) return false;
        bool riskMatches = SelectedRiskFilter switch
        {
            "Seguros" => item.RiskLevel == RiskLevel.Green,
            "Opcionales" => item.RiskLevel == RiskLevel.Yellow,
            "Agresivos" => item.RiskLevel == RiskLevel.Red,
            "Activos" => item.IsEnabled,
            "Respaldados" => _backedUpIds.Contains(item.Id),
            _ => SelectedRisk switch
            {
                "Verde" => item.RiskLevel == RiskLevel.Green,
                "Amarillo" => item.RiskLevel == RiskLevel.Yellow,
                "Rojo" => item.RiskLevel == RiskLevel.Red,
                _ => true
            }
        };
        bool categoryMatches = _selectedCategory is null || item.Category == _selectedCategory;
        bool textMatches = string.IsNullOrWhiteSpace(SearchText) ||
            $"{item.Id} {item.CategoryLabel} {item.RiskLabel} {item.Title} {item.ShortDescription} {item.TechnicalSummary} {item.WarningMessage}".Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase);
        return riskMatches && categoryMatches && textMatches;
    }

    private static string FormatAntiCheat(HardwareSnapshot hardware)
    {
        string[] detected = new[]
        {
            hardware.HasVanguard ? "Vanguard" : null,
            hardware.HasFaceit ? "FACEIT" : null,
            hardware.HasEasyAntiCheat ? "Easy Anti-Cheat" : null,
            hardware.HasBattlEye ? "BattlEye" : null
        }.Where(name => name is not null).Cast<string>().ToArray();
        return "Anticheat detectados: " + (detected.Length == 0 ? "ninguno por nombre de servicio" : string.Join(", ", detected));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        if (name is nameof(SearchText) or nameof(SelectedRisk) or nameof(SelectedCategoryOption)) OnPropertyChanged(nameof(FilteredItems));
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value!; }
            remove { CommandManager.RequerySuggested -= value!; }
        }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
    }
}

public sealed record CategoryOption(OptimizationCategory? Category, string Label)
{
    public override string ToString() => Label;
}

public sealed class CategoryNavItem : INotifyPropertyChanged
{
    private int _count;
    private int _activeCount;

    public CategoryNavItem(OptimizationCategory category, string label, string glyph)
    {
        Category = category;
        Label = label;
        Glyph = glyph;
    }
    public OptimizationCategory Category { get; }
    public string Label { get; }
    public string Glyph { get; }
    public int Count { get => _count; set { if (_count == value) return; _count = value; OnChanged(nameof(Count)); OnChanged(nameof(CountLabel)); } }
    public int ActiveCount { get => _activeCount; set { if (_activeCount == value) return; _activeCount = value; OnChanged(nameof(ActiveCount)); } }
    public string CountLabel => $"({Count})";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record QuickToolItem(int Id, string Glyph, string Label);
