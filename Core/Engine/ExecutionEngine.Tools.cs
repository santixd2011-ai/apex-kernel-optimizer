using Microsoft.Win32;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ApexKernelOptimizer.Core.Models;

namespace ApexKernelOptimizer.Core.Engine;

/// <summary>Valor original de una propiedad de búfer de red antes de aplicar un preset.</summary>
public sealed record BufferValueBackup(string InterfaceDescription, string RegistryName, string Value);

/// <summary>Conjunto de valores originales respaldados para una propiedad de búfer.</summary>
public sealed record BufferBackup(string DisplayName, List<BufferValueBackup> Values);

/// <summary>Límite de potencia leído antes de aplicar un valor o preset.</summary>
public sealed record PowerLimitBackup(string Device, double Watts);

public sealed partial class ExecutionEngine
{
    // ============ Ajustes NVIDIA DRS (toggles #60/#61/#62/#65/#66/#194) ============

    private sealed record DrsToggleItem(int Id, string? Profile, string? Exe, uint[] SettingIds, uint[] ApplyValues);

    private static readonly DrsToggleItem[] DrsTable =
    [
        new(60, null, null, [0x1057EB71u], [1u]),                            // Power management mode = Rendimiento máximo
        new(61, null, null, [0x00CE2691u], [0xFFFFFFF6u]),                   // Texture filtering quality = Alta calidad
        new(62, null, null, [0x0064B541u], [1u]),                            // Preferred refresh rate = Más alta disponible
        new(65, "ApexKernel · Valorant", "VALORANT-Win64-Shipping.exe", [0x20C1221Eu], [2u]), // Threaded optimization = Off
        new(66, null, null, [0x007BA09Eu], [1u]),                            // Maximum pre-rendered frames = 1
        new(194, null, null, [0x00198FFFu, 0x00AC8497u], [1u, 0xFFFFFFFFu])  // Shader Cache activa + tamaño sin límite (≥100 GB)
    ];

    private static DrsToggleItem GetDrsItem(int id) => DrsTable.First(entry => entry.Id == id);

    private static bool IsDrsToggleApplied(int id)
    {
        DrsToggleItem entry = GetDrsItem(id);
        int matched = 0;
        for (int i = 0; i < entry.SettingIds.Length; i++)
        {
            (string error, uint value) = NvApiDrs.ReadDrsSetting(entry.Profile, entry.SettingIds[i]);
            if (error.Length > 0) return false;
            if (value != entry.ApplyValues[i]) return false;
            matched++;
        }
        return matched > 0;
    }

    private async Task<ExecutionResult> SetDrsToggleAsync(int id, bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DrsToggleItem entry = GetDrsItem(id);
            if (enabled)
            {
                List<(uint SettingId, uint Value)> settings = entry.SettingIds
                    .Select((settingId, index) => (settingId, entry.ApplyValues[index]))
                    .ToList();
                string result = NvApiDrs.ApplyDrsValues(entry.Profile, entry.Exe, settings);
                return result.StartsWith("Perfiles de NVIDIA", StringComparison.Ordinal)
                    ? new(true, result)
                    : new(false, result);
            }
            var messages = new StringBuilder();
            foreach (uint settingId in entry.SettingIds)
                messages.AppendLine(NvApiDrs.ResetDrsSetting(entry.Profile, settingId));
            string text = messages.ToString().Trim();
            bool failed = text.Contains("código", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("No se pudo", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("No se encontró", StringComparison.OrdinalIgnoreCase);
            return failed
                ? new(false, text)
                : new(true, text.Length == 0 ? "El perfil NVIDIA no tenía anulaciones que restaurar." : text);
        }
        catch (Exception ex)
        {
            return new(false, $"No se pudo cambiar el perfil NVIDIA: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    // ============ Registro dinámico para herramientas (MSI mode #56, afinidad #57, políticas #144, WU #111) ============

    private static string ToolRegistryBackupKey(int toolId) => @"Windows\Tools\" + toolId;

    public async Task<bool> HasToolRegistryBackupAsync(int toolId, CancellationToken cancellationToken = default) =>
        await _backup.ReadOriginalAsync<DevicePowerSnapshot>(ToolRegistryBackupKey(toolId), cancellationToken) is not null;

    public async Task<ExecutionResult> ApplyToolRegistryAsync(int toolId, IReadOnlyList<RegistryChange> changes, CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator()) return new(false, "Esta herramienta requiere ejecutar la aplicación como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await _backup.ReadOriginalAsync<DevicePowerSnapshot>(ToolRegistryBackupKey(toolId), cancellationToken) is not null)
                return new(false, "Ya hay un respaldo pendiente de esta herramienta; restáuralo antes de aplicar de nuevo.");
            var originals = new List<DevicePowerKeySnapshot>();
            foreach (RegistryChange change in changes)
                originals.Add(new(change.KeyPath, [new DevicePowerValueSnapshot(change.ValueName, ReadSnapshot(change))]));
            await _backup.SaveOriginalAsync(ToolRegistryBackupKey(toolId), new DevicePowerSnapshot(originals), cancellationToken);
            try
            {
                foreach (RegistryChange change in changes)
                    ApplyRegistryChange(change);
            }
            catch
            {
                try { await RestoreToolRegistryCoreAsync(toolId, changes, CancellationToken.None); } catch { }
                try { await _backup.RemoveOriginalAsync(ToolRegistryBackupKey(toolId), CancellationToken.None); } catch { }
                throw;
            }
            return new(true, $"Se aplicaron {changes.Count} cambio(s) de registro; el estado previo quedó respaldado.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException or Win32Exception or OperationCanceledException)
        {
            return new(false, $"No se pudo aplicar el cambio de registro: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    public async Task<ExecutionResult> RestoreToolRegistryAsync(int toolId, IReadOnlyList<RegistryChange> changes, CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator()) return new(false, "Esta herramienta requiere ejecutar la aplicación como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await _backup.ReadOriginalAsync<DevicePowerSnapshot>(ToolRegistryBackupKey(toolId), cancellationToken) is null)
                return new(false, "No hay respaldo previo de esta herramienta; no se restauró nada.");
            await RestoreToolRegistryCoreAsync(toolId, changes, cancellationToken);
            await _backup.RemoveOriginalAsync(ToolRegistryBackupKey(toolId), cancellationToken);
            return new(true, "Se restauró el registro original de la herramienta.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException or Win32Exception or OperationCanceledException)
        {
            return new(false, $"No se pudo restaurar el registro: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task RestoreToolRegistryCoreAsync(int toolId, IReadOnlyList<RegistryChange> changes, CancellationToken cancellationToken)
    {
        DevicePowerSnapshot? saved = await _backup.ReadOriginalAsync<DevicePowerSnapshot>(ToolRegistryBackupKey(toolId), cancellationToken);
        if (saved is null) return;
        var byKey = saved.Keys.ToDictionary(key => key.RegistryPath, StringComparer.OrdinalIgnoreCase);
        foreach (RegistryChange change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byKey.TryGetValue(change.KeyPath, out DevicePowerKeySnapshot? keySnapshot)) continue;
            DevicePowerValueSnapshot? value = keySnapshot.Values.FirstOrDefault(entry => string.Equals(entry.Name, change.ValueName, StringComparison.OrdinalIgnoreCase));
            if (value is null) continue;
            if (!value.Original.Exists)
            {
                using RegistryKey? key = OpenKey(change, writable: true);
                if (key is not null && key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase)) key.DeleteValue(change.ValueName, false);
                continue;
            }
            RestoreValue(change, value.Original);
        }
    }

    // ============ Gestor de servicios (#105 / #205) ============

    private static readonly (string Category, string Label, string Service)[] ManagedServiceCatalog =
    [
        ("Telemetría", "Experiencias de usuario conectadas y telemetría", "DiagTrack"),
        ("Telemetría", "Servicio de directivas de diagnóstico", "diagsvc"),
        ("Telemetría", "Servicio dmwappush de telemetría", "dmwappushservice"),
        ("Telemetría", "Servicio de informe de errores", "WerSvc"),
        ("Telemetría", "Soporte de informe de errores", "wercplsupport"),
        ("Impresoras", "Cola de impresión", "Spooler"),
        ("Impresoras", "Notificaciones de impresora", "PrintNotify"),
        ("Impresoras", "Flujo de trabajo de impresión", "PrintWorkflowUserSvc"),
        ("Impresoras", "Fax", "Fax"),
        ("Impresoras", "Adquisición de imágenes de Windows", "stisvc"),
        ("Xbox", "Administrador de autenticación de Xbox Live", "XblAuthManager"),
        ("Xbox", "Guardado de juegos de Xbox Live", "XblGameSave"),
        ("Xbox", "Servicio de red de Xbox Live", "XboxNetApiSvc"),
        ("Xbox", "Servicios de juegos", "GamingServices"),
        ("Xbox", "Accesibilidad de servicios de juegos", "GamingServicesNet"),
        ("Remoto", "Escritorio remoto", "TermService"),
        ("Remoto", "Redirección de sesiones RDP", "UmRdpService"),
        ("Remoto", "Registro remoto", "RemoteRegistry"),
        ("Remoto", "Configuración de Escritorio remoto", "SessionEnv"),
        ("Remoto", "Asistente de sesión remota", "RasSstp"),
        ("Hyper-V", "Administración de máquinas virtuales", "vmms"),
        ("Hyper-V", "Host de computación de contenedores", "vmcompute"),
        ("Hyper-V", "Servicio de host de Hyper-V", "HvHost"),
        ("Hyper-V", "Servicio de instantáneas de invitado", "vmicvss"),
        ("Hyper-V", "Sincronización horaria de invitado", "vmictimesync"),
        ("Tarjetas Inteligentes", "Tarjeta inteligente", "SCardSvr"),
        ("Tarjetas Inteligentes", "Directiva de extracción de tarjetas", "ScPolicySvc"),
        ("Tarjetas Inteligentes", "Enumeración de dispositivos de tarjeta", "ScDeviceEnum"),
        ("Tarjetas Inteligentes", "Copia de seguridad de Windows", "SDRSVC"),
        ("Telefonía", "Servicio de telefonía", "PhoneSvc"),
        ("Telefonía", "Administrador de conexión de acceso remoto", "RasMan"),
        ("Telefonía", "API de telefonía", "TapiSrv"),
        ("Telefonía", "Conexión compartida a Internet", "SharedAccess"),
        ("Telefonía", "Agente de directivas de red", "napagent")
    ];

    public async Task<IReadOnlyList<ServiceManagerEntry>> ListServiceManagerEntriesAsync(bool thirdPartyOnly, CancellationToken cancellationToken = default)
    {
        var entries = new List<ServiceManagerEntry>();
        if (!thirdPartyOnly)
        {
            foreach ((string category, string label, string service) in ManagedServiceCatalog)
                entries.Add(await BuildServiceEntryAsync(service, category, label, isThirdParty: false, cancellationToken));
        }
        else
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false);
            if (root is null) return entries;
            string windowsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "");
            foreach (string name in root.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using RegistryKey? key = root.OpenSubKey(name, writable: false);
                if (key is null) continue;
                string? imagePath = key.GetValue("ImagePath") as string;
                if (string.IsNullOrWhiteSpace(imagePath)) continue;
                string expanded = Environment.ExpandEnvironmentVariables(imagePath).Trim('"');
                if (expanded.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase)) continue;
                string? displayName = key.GetValue("DisplayName") as string;
                entries.Add(await BuildServiceEntryAsync(name, "Terceros", displayName ?? name, isThirdParty: true, cancellationToken));
            }
        }
        return entries;
    }

    private async Task<ServiceManagerEntry> BuildServiceEntryAsync(string name, string category, string label, bool isThirdParty, CancellationToken cancellationToken)
    {
        string displayName = label;
        uint start = 0xFFFFFFFF;
        try
        {
            ServiceStateSnapshot state = await ReadServiceStateAsync(name, cancellationToken);
            start = state.StartType;
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name, writable: false);
            string? registered = key?.GetValue("DisplayName") as string;
            if (!string.IsNullOrWhiteSpace(registered)) displayName = registered;
        }
        catch { }
        string mode = start switch
        {
            2 => "Automático",
            3 => "Manual",
            4 => "Deshabilitado",
            _ => "Desconocido"
        };
        bool hasBackup = await _backup.ReadOriginalAsync<ServiceStateSnapshot>(@"Windows\Services\Manager\" + name, cancellationToken) is not null;
        return new ServiceManagerEntry(name, displayName, mode, category, isThirdParty, start != 0xFFFFFFFF, hasBackup);
    }

    public async Task<ExecutionResult> SetServiceModeAsync(string name, string mode, CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator()) return new(false, "Cambiar servicios requiere ejecutar la aplicación como administrador.");
        uint target = mode switch
        {
            "Automático" => 2,
            "Manual" => 3,
            "Deshabilitado" => 4,
            _ => 0xFFFFFFFF
        };
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (target == 0xFFFFFFFF)
            {
                ServiceStateSnapshot? original = await _backup.ReadOriginalAsync<ServiceStateSnapshot>(@"Windows\Services\Manager\" + name, cancellationToken);
                if (original is null) return new(false, "No hay respaldo previo de este servicio; no se restauró nada.");
                try { await RestoreServiceStateAsync(name, original, cancellationToken); } catch { }
                await _backup.RemoveOriginalAsync(@"Windows\Services\Manager\" + name, cancellationToken);
                return new(true, $"{name}: se restauró el estado original.");
            }
            ServiceStateSnapshot current = await ReadServiceStateAsync(name, cancellationToken);
            if (current.CurrentState is not (ServiceStopped or ServiceRunning))
                return new(false, $"{name} está en transición; espera a que termine y vuelve a intentarlo.");
            if (await _backup.ReadOriginalAsync<ServiceStateSnapshot>(@"Windows\Services\Manager\" + name, cancellationToken) is null)
                await _backup.SaveOriginalAsync(@"Windows\Services\Manager\" + name, current, cancellationToken);
            ChangeServiceStartType(name, target);
            ServiceStatusProcess status = QueryServiceStatusByName(name);
            if (target == ServiceDisabled && status.CurrentState == ServiceRunning) await SetServiceRunningAsync(name, false, cancellationToken);
            else if (target == 2 && status.CurrentState != ServiceRunning) await SetServiceRunningAsync(name, true, cancellationToken);
            else if (target == 3 && status.CurrentState == ServiceRunning) await SetServiceRunningAsync(name, false, cancellationToken);
            return new(true, $"{name}: modo {mode} aplicado; el estado previo quedó respaldado.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar {name}: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    // ============ Gestor de arranque (#152) ============

    public async Task<IReadOnlyList<StartupEntry>> ListStartupEntriesAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<StartupEntry>();
        await Task.Run(() =>
        {
            foreach ((string hive, string label) in new[] { ("HKCU", "Usuario (HKCU)"), ("HKLM", "Equipo (HKLM)") })
            {
                RegistryKey? baseKey = hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                using RegistryKey? run = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: false);
                if (run is null) continue;
                foreach (string name in run.GetValueNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string command = run.GetValue(name) as string ?? "";
                    bool enabled = ReadStartupApproved(hive, name);
                    entries.Add(new StartupEntry("Registro", name, command, enabled, label));
                }
            }
            string startupFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            if (Directory.Exists(startupFolder))
            {
                foreach (string file in Directory.GetFiles(startupFolder))
                    entries.Add(new StartupEntry("Carpeta", Path.GetFileName(file), file, true, "Carpeta de inicio del usuario"));
            }
        }, cancellationToken);
        return entries;
    }

    private static bool ReadStartupApproved(string hive, string name)
    {
        RegistryKey? baseKey = hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
        using RegistryKey? approved = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: false);
        if (approved is null) return true;
        bool present = approved.GetValueNames().Any(valueName => string.Equals(valueName, name, StringComparison.OrdinalIgnoreCase));
        if (!present) return true;
        if (approved.GetValue(name) is not byte[] data || data.Length == 0) return true;
        return (data[0] & 1) == 0;
    }

    public async Task<ExecutionResult> SetStartupEntryEnabledAsync(StartupEntry entry, bool enabled, CancellationToken cancellationToken = default)
    {
        if (entry.Kind == "Carpeta")
            return new(false, "Los accesos de la carpeta de inicio no se pueden desactivar; elimínalos manualmente si no los necesitas.");
        string hive = entry.Location.Contains("HKCU", StringComparison.Ordinal) ? "HKCU" : "HKLM";
        if (hive == "HKLM" && !IsAdministrator()) return new(false, "Modificar entradas del equipo requiere ejecutar la aplicación como administrador.");
        await Task.Run(() =>
        {
            RegistryKey? baseKey = hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            using RegistryKey? approved = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true);
            if (approved is null) return;
            byte[] data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            approved.SetValue(entry.Name, data, RegistryValueKind.Binary);
        }, cancellationToken);
        return new(true, $"{entry.Name}: {(enabled ? "activada" : "desactivada")} en el inicio.");
    }

    // ============ Despachador de herramientas ============

    public async Task<string> RunToolAsync(int id, string action, string input, CancellationToken cancellationToken = default)
    {
        try
        {
            return id switch
            {
                15 => await RunUsbScannerToolAsync(action, cancellationToken),
                51 => await RunPowerPlanGeneratorToolAsync(action, cancellationToken),
                56 => await RunMsiToolAsync(action, cancellationToken),
                57 => await RunAffinityToolAsync(action, cancellationToken),
                64 => await RunNvidiaPanelToolAsync(action, cancellationToken),
                67 => RunVibranceTool(action, input),
                71 => await RunPowerLimitToolAsync(action, input, cancellationToken),
                82 => await RunDnsToolAsync(action, input, cancellationToken),
                108 => await RunTrimToolAsync(action, cancellationToken),
                109 => await RunDeepCleanToolAsync(action, cancellationToken),
                110 => await RunAppxToolAsync(action, input, cancellationToken),
                111 => await RunWindowsUpdateToolAsync(action, cancellationToken),
                112 => await RunRepairToolAsync(action, cancellationToken),
                129 => await RunBufferToolAsync(action, input, "Receive Buffers", cancellationToken),
                130 => await RunBufferToolAsync(action, input, "Transmit Buffers", cancellationToken),
                144 => await RunBrowserToolAsync(action, cancellationToken),
                147 => await RunGhostDeviceToolAsync(action, cancellationToken),
                150 => await RunStoreToolAsync(action, input, cancellationToken),
                151 => await RunTrustedInstallerToolAsync(action, input, cancellationToken),
                154 => await RunPeripheralProfilesToolAsync(action, cancellationToken),
                176 => await RunSafeModeToolAsync(action, cancellationToken),
                177 => await RunInstallerFixToolAsync(action, cancellationToken),
                182 => await RunDefenderToolAsync(action, input, cancellationToken),
                183 => RunQuantumCalculatorTool(action, input),
                195 => await RunGameInputFixToolAsync(action, cancellationToken),
                203 => await RunRebarToolAsync(action, input, cancellationToken),
                204 => await RunMonitorSyncToolAsync(action, cancellationToken),
                208 => await RunHardwareShieldToolAsync(action, cancellationToken),
                _ => $"La herramienta #{id} no tiene acciones disponibles en esta versión."
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"No se pudo ejecutar la herramienta: {ex.Message}";
        }
    }

    // ============ Utilidades compartidas de presentación para herramientas simples ============

    private static string ToolHeader(string title, string? subtitle = null)
    {
        var text = new StringBuilder();
        text.AppendLine($"===== {title} =====");
        if (!string.IsNullOrWhiteSpace(subtitle)) text.AppendLine(subtitle);
        return text.ToString();
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB"
        : $"{bytes / 1024.0 / 1024.0:0.0} MB";

    private static string ReadRegistryDisplay(RegistryKey? key, string valueName, string whenMissing)
    {
        if (key is null) return "clave no presente";
        object? raw = key.GetValue(valueName, null);
        if (raw is null) return whenMissing;
        RegistryValueKind kind = key.GetValueKind(valueName);
        if (raw is byte[] bytes) return $"0x{Convert.ToHexString(bytes)} ({bytes.Length} bytes, {kind})";
        return $"{raw} ({kind})";
    }

    private static string NormalizeExeName(string value)
    {
        string exe = Path.GetFileName(value.Trim());
        if (exe.Length == 0) exe = "juego.exe";
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";
    }

    // ============ Infraestructura compartida de las acciones añadidas ============

    private const string AudioClassGuid = "{4d36e96c-e325-11ce-bfc1-08002be10318}";

    private static string ToolBackupKey(int toolId, string suffix) => @"Windows\Tools\" + toolId + @"\" + suffix;

    private static string ApexReportFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ApexKernel");

    private static async Task<string> WriteApexReportAsync(string prefix, string content, CancellationToken cancellationToken)
    {
        string folder = ApexReportFolder();
        string path = Path.Combine(folder, prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(path, content, cancellationToken);
            int lines = content.Split('\n').Length;
            return $"Archivo: {path}{Environment.NewLine}Tamaño: {new FileInfo(path).Length} bytes · {lines} líneas.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return "No se pudo guardar el informe: " + ex.Message;
        }
    }

    private static string NvidiaSmiPath()
    {
        string smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        if (!File.Exists(smi)) smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        return File.Exists(smi) ? smi : "nvidia-smi.exe";
    }

    private static double? ParseDoubleInvariant(string? text) =>
        double.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value : null;

    /// <summary>Lectura de solo lectura del estado de vibración mediante NvAPI (el wrapper propio solo permite escribir).</summary>
    private static class DigitalVibranceReader
    {
        private const uint IdInitialize = 0x0150E828;
        private const uint IdEnumPhysicalGPUs = 0xE5AC921F;
        private const uint IdGetScanoutIntensityState = 0xE81CE836;

        [StructLayout(LayoutKind.Sequential)]
        private struct NvScanoutIntensityState
        {
            public uint Version;
            public uint Cmd;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
            public int[] Coefficient;
        }

        private delegate IntPtr QueryInterfaceDelegate(uint id);
        private delegate int StatusNoArgDelegate();
        private delegate int EnumGpusDelegate(out IntPtr handles, out uint count);
        private delegate int GetStateDelegate(IntPtr handle, IntPtr state);

        public static (bool Ok, int Percent, string Message) Read()
        {
            IntPtr library = IntPtr.Zero;
            try
            {
                library = NativeLibrary.Load("nvapi64.dll");
                if (library == IntPtr.Zero) return (false, 0, "No se encontró nvapi64.dll; no parece haber un controlador NVIDIA instalado.");
                IntPtr query = NativeLibrary.GetExport(library, "nvapi_QueryInterface");
                if (query == IntPtr.Zero) return (false, 0, "nvapi64.dll no expone nvapi_QueryInterface; no se pudo leer el estado.");
                var queryFn = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(query);
                IntPtr initPtr = queryFn(IdInitialize);
                if (initPtr == IntPtr.Zero) return (false, 0, "Esta versión de nvapi64.dll no ofrece NvAPI_Initialize.");
                int status = Marshal.GetDelegateForFunctionPointer<StatusNoArgDelegate>(initPtr)();
                if (status != 0) return (false, 0, $"NvAPI_Initialize devolvió {status} (no hay GPU NVIDIA activa).");
                IntPtr enumPtr = queryFn(IdEnumPhysicalGPUs);
                IntPtr getPtr = queryFn(IdGetScanoutIntensityState);
                if (enumPtr == IntPtr.Zero || getPtr == IntPtr.Zero)
                    return (false, 0, "El controlador no expone la lectura del estado de intensidad (función no disponible).");
                status = Marshal.GetDelegateForFunctionPointer<EnumGpusDelegate>(enumPtr)(out IntPtr handles, out uint count);
                if (status != 0 || count == 0 || handles == IntPtr.Zero)
                    return (false, 0, $"No se detectaron GPU NVIDIA al leer la vibración (código {status}).");
                var state = new NvScanoutIntensityState
                {
                    Version = (uint)(Marshal.SizeOf<NvScanoutIntensityState>() | (1 << 16)),
                    Coefficient = new int[9]
                };
                IntPtr statePtr = Marshal.AllocHGlobal(Marshal.SizeOf<NvScanoutIntensityState>());
                try
                {
                    Marshal.StructureToPtr(state, statePtr, false);
                    status = Marshal.GetDelegateForFunctionPointer<GetStateDelegate>(getPtr)(Marshal.ReadIntPtr(handles), statePtr);
                    if (status != 0) return (false, 0, $"El controlador no devolvió el estado de vibración (código {status}).");
                    NvScanoutIntensityState read = Marshal.PtrToStructure<NvScanoutIntensityState>(statePtr)!;
                    int percent = (read.Coefficient[0] - 4096) / 16;
                    if (percent is < 0 or > 100)
                        return (false, 0, $"El controlador devolvió un valor de intensidad fuera de rango ({read.Coefficient[0]}).");
                    return (true, percent, $"GPU detectadas: {count} · coeficiente leído: {read.Coefficient[0]}.");
                }
                finally { Marshal.FreeHGlobal(statePtr); }
            }
            catch (Exception ex)
            {
                return (false, 0, "No se pudo leer la vibración digital: " + ex.Message);
            }
            finally
            {
                if (library != IntPtr.Zero) { try { NativeLibrary.Free(library); } catch { } }
            }
        }
    }

    private async Task<string> RunUsbScannerToolAsync(string action, CancellationToken cancellationToken)
    {
        if (action is not ("Escanear" or "Exportar informe" or "Resumen rápido")) return "Acción no reconocida.";
        HardwareSnapshot snapshot = await new HardwareAndAnticheatScanner().ScanAsync(cancellationToken);
        if (action == "Resumen rápido")
        {
            string anticheats = (snapshot.HasVanguard ? "Vanguard " : "") + (snapshot.HasFaceit ? "FACEIT " : "") +
                                (snapshot.HasEasyAntiCheat ? "EAC " : "") + (snapshot.HasBattlEye ? "BattlEye" : "");
            var quick = new StringBuilder();
            quick.Append(ToolHeader("Resumen rápido USB/HID", "Solo lectura: datos esenciales del árbol Plug and Play."));
            quick.AppendLine($"Entradas USB/HID: {snapshot.UsbTopology.Count}.");
            quick.AppendLine($"CPU: {snapshot.PhysicalCores} núcleos físicos / {snapshot.LogicalProcessors} lógicos · RAM: {snapshot.MemoryGb:0.#} GB.");
            quick.AppendLine($"Unidad del sistema: {snapshot.SystemDriveType} · GPU: {(snapshot.GraphicsAdapters.Count == 0 ? "no detectada" : string.Join(", ", snapshot.GraphicsAdapters))}.");
            quick.AppendLine($"Anticheat de kernel: {(anticheats.Trim().Length == 0 ? "ninguno detectado" : anticheats.Trim())}.");
            quick.AppendLine($"Sistema: {snapshot.WindowsVersion} (build {snapshot.WindowsBuild}.{snapshot.WindowsUbr}).");
            quick.AppendLine("→ Si un periférico aparece tras un hub compartido, conéctalo a un puerto raíz para reducir latencia.");
            return quick.ToString();
        }
        var lines = new StringBuilder();
        lines.Append(ToolHeader("Inventario USB/HID Plug and Play", "Solo lectura: no se modifica ningún dispositivo."));
        lines.AppendLine($"Entradas detectadas en el árbol: {snapshot.UsbTopology.Count}.");
        foreach (string line in snapshot.UsbTopology) lines.AppendLine("  " + line);
        lines.AppendLine();
        lines.AppendLine("Resumen del equipo:");
        lines.AppendLine($"  CPU: {snapshot.PhysicalCores} núcleos físicos / {snapshot.LogicalProcessors} procesadores lógicos.");
        lines.AppendLine($"  RAM: {snapshot.MemoryGb:0.#} GB · Unidad del sistema: {snapshot.SystemDriveType}.");
        lines.AppendLine("→ Un periférico conectado mediante un hub compartido puede añadir latencia; revisa su ruta de instancia.");
        if (action == "Exportar informe")
        {
            string export = await WriteApexReportAsync("InventarioUSB", lines.ToString(), cancellationToken);
            return ToolHeader("Informe USB exportado", $"Entradas incluidas: {snapshot.UsbTopology.Count}.") + export +
                   Environment.NewLine + "→ 'Resumen rápido' muestra solo las cifras principales sin recorrer el árbol completo.";
        }
        return lines.ToString();
    }

    private async Task<string> RunPowerPlanGeneratorToolAsync(string action, CancellationToken cancellationToken)
    {
        if (action == "Listar")
        {
            string list = await RunPowerShellStaticAsync("powercfg /list", cancellationToken);
            string active = await RunPowerShellStaticAsync("powercfg /getactivescheme", cancellationToken);
            return ToolHeader("Planes de energía de Windows", "El plan activo aparece marcado con * en el listado.") +
                   list.Trim() + Environment.NewLine + Environment.NewLine +
                   active.Trim() + Environment.NewLine +
                   "→ 'Generar ...' duplica un plan base sin tocar el original; actívalo con powercfg /setactive <GUID>.";
        }
        (Guid baseScheme, string name) = action switch
        {
            "Generar Equilibrado" => (Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), "ApexKernel Equilibrado"),
            "Generar Competitivo" => (SchemeMin, "ApexKernel Competitivo"),
            "Generar Máximo" => (Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61"), "ApexKernel Máximo"),
            _ => (Guid.Empty, "")
        };
        if (baseScheme == Guid.Empty) return "Acción no reconocida.";
        string output = await RunPowerShellAsync($"powercfg /duplicatescheme {baseScheme}", cancellationToken);
        Match match = Regex.Match(output, @"\{[0-9a-fA-F-]+\}");
        if (!match.Success) return "No se pudo duplicar el plan base " + baseScheme + ": " + output.Trim();
        string guid = match.Value;
        await RunPowerShellAsync($"powercfg /changename {guid} \"{name}\" \"Plan generado por ApexKernel\"", cancellationToken);
        return ToolHeader("Plan de energía generado") +
               $"Plan base duplicado: {baseScheme}\nNuevo plan: {name}\nGUID del nuevo plan: {guid}\n" +
               $"→ Actívalo con: powercfg /setactive {guid}\n" +
               "→ Para volver al plan anterior, pulsa 'Listar' y ejecuta powercfg /setactive con su GUID.";
    }

    private Task<List<(string InstancePath, string Label)>> GetPciClassDevicesAsync(string[] classGuids, CancellationToken cancellationToken)
    {
        var candidates = new List<(string, string)>();
        using RegistryKey? enumRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI", writable: false);
        if (enumRoot is null) return Task.FromResult(candidates);
        foreach (string vendor in enumRoot.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RegistryKey? vendorKey = enumRoot.OpenSubKey(vendor, writable: false);
            if (vendorKey is null) continue;
            foreach (string device in vendorKey.GetSubKeyNames())
            {
                using RegistryKey? deviceKey = vendorKey.OpenSubKey(device, writable: false);
                if (deviceKey is null) continue;
                foreach (string instance in deviceKey.GetSubKeyNames())
                {
                    string instancePath = @"SYSTEM\CurrentControlSet\Enum\PCI\" + vendor + "\\" + device + "\\" + instance;
                    using RegistryKey? instanceKey = Registry.LocalMachine.OpenSubKey(instancePath, writable: false);
                    if (instanceKey is null) continue;
                    string? classGuid = instanceKey.GetValue("ClassGUID") as string;
                    if (classGuid is null || !classGuids.Contains(classGuid, StringComparer.OrdinalIgnoreCase)) continue;
                    string description = instanceKey.GetValue("DeviceDesc") as string ?? "";
                    if (description.Contains(';')) description = description[(description.IndexOf(';') + 1)..];
                    candidates.Add((instancePath, description));
                }
            }
        }
        return Task.FromResult(candidates);
    }

    private static readonly string[] MsiClassGuids =
    [
        "{4d36e968-e325-11ce-bfc1-08002be10318}", // Adaptadores de pantalla
        "{4d36e96c-e325-11ce-bfc1-08002be10318}"  // Audio/MEDIA (HDAudio y GPU de audio)
    ];

    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    private async Task<string> RunMsiToolAsync(string action, CancellationToken cancellationToken)
    {
        string[] classes = action switch
        {
            "Solo GPU" => [DisplayClassGuid],
            "Solo audio" => [AudioClassGuid],
            _ => MsiClassGuids
        };
        List<(string InstancePath, string Label)> devices = await GetPciClassDevicesAsync(classes, cancellationToken);
        if (devices.Count == 0)
            return action == "Solo GPU" ? "No se encontraron GPU PCI compatibles."
                 : action == "Solo audio" ? "No se encontraron dispositivos PCI de audio compatibles."
                 : "No se encontraron dispositivos PCI de pantalla o audio compatibles.";
        if (action == "Verificar")
        {
            var lines = new StringBuilder();
            lines.Append(ToolHeader("Estado MSI (interrupciones señalizadas por mensaje)",
                "Solo lectura: MSISupported=1 activa MSI; 0 (o ausente) usa interrupciones heredadas."));
            foreach ((string path, string label) in devices)
            {
                string keyPath = path + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
                lines.AppendLine($"• {label}");
                lines.AppendLine($"    Clave: HKLM\\{keyPath}");
                lines.AppendLine($"    MSISupported: {ReadRegistryDisplay(key, "MSISupported", "0 (sin valor; usa el valor por defecto del controlador)")}");
            }
            lines.AppendLine("→ 'Activar' escribe MSISupported=1 y respalda el valor previo; 'Solo GPU' y 'Solo audio' limitan el ámbito; 'Restaurar' lo devuelve.");
            lines.AppendLine("→ El cambio requiere reiniciar para que el controlador reasigne las interrupciones.");
            return lines.ToString();
        }
        if (action is "Activar" or "Solo GPU" or "Solo audio")
        {
            var changes = new List<RegistryChange>();
            foreach ((string path, _) in devices)
            {
                changes.Add(new RegistryChange
                {
                    Hive = "HKLM",
                    KeyPath = path + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties",
                    ValueName = "MSISupported",
                    ValueKind = "DWord",
                    ApplyValue = "1",
                    RevertValue = "0"
                });
            }
            ExecutionResult result = await ApplyToolRegistryAsync(56, changes, cancellationToken);
            string scope = action == "Solo GPU" ? "solo GPU" : action == "Solo audio" ? "solo audio" : "pantalla y audio";
            return result.Success
                ? ToolHeader("MSI activado", $"Ámbito: {scope} · dispositivos modificados: {changes.Count}.") + result.Message +
                  Environment.NewLine + "→ Reinicia el equipo para que el modo MSI surta efecto; 'Verificar' mostrará MSISupported=1." +
                  Environment.NewLine + "→ Para volver atrás: 'Restaurar' devuelve los valores previos respaldados."
                : result.Message;
        }
        if (action == "Restaurar")
        {
            var changes = new List<RegistryChange>();
            foreach ((string path, _) in devices)
            {
                changes.Add(new RegistryChange
                {
                    Hive = "HKLM",
                    KeyPath = path + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties",
                    ValueName = "MSISupported",
                    ValueKind = "DWord",
                    ApplyValue = "0",
                    RevertValue = "0"
                });
            }
            ExecutionResult result = await RestoreToolRegistryAsync(56, changes, cancellationToken);
            return result.Success
                ? ToolHeader("MSI restaurado") + result.Message + Environment.NewLine + "→ Reinicia para que surta efecto."
                : result.Message;
        }
        return "Acción no reconocida.";
    }

    // La política de afinidad documentada por Microsoft vive bajo la clave de DISPOSITIVO,
    // no bajo una clave global con la ruta aplanada (esto último no produce ningún efecto).
    private static string AffinityPolicyPath(string instancePath) =>
        instancePath + @"\Device Parameters\Interrupt Management\Affinity Policy";

    private async Task<string> ApplyGpuAffinityAsync(IReadOnlyList<(string InstancePath, string Label)> gpus, ulong mask,
        string title, string detail, CancellationToken cancellationToken)
    {
        if (gpus.Count == 0) return "No se encontraron GPU compatibles.";
        int logicals = Environment.ProcessorCount;
        string assignmentBase64 = Convert.ToBase64String(BitConverter.GetBytes(mask)); // KAFFINITY de 64 bits, little endian
        var changes = new List<RegistryChange>();
        foreach ((string path, _) in gpus)
        {
            string keyPath = AffinityPolicyPath(path);
            changes.Add(new RegistryChange { Hive = "HKLM", KeyPath = keyPath, ValueName = "DevicePolicy", ValueKind = "DWord", ApplyValue = "4", RevertValue = "0" });
            changes.Add(new RegistryChange { Hive = "HKLM", KeyPath = keyPath, ValueName = "AssignmentSetOverride", ValueKind = "Binary", ApplyValue = assignmentBase64, RevertValue = assignmentBase64 });
        }
        ExecutionResult result = await ApplyToolRegistryAsync(57, changes, cancellationToken);
        return result.Success
            ? ToolHeader(title, $"GPU modificadas: {gpus.Count} · procesadores lógicos detectados: {logicals}.") +
              result.Message + Environment.NewLine + $"Máscara: 0x{mask:X} ({detail})." +
              (logicals > 64 ? " Solo se pueden direccionar 64 CPU por grupo de procesadores." : "") +
              Environment.NewLine + "→ Requiere reinicio; comprueba el resultado con 'Escanear' y revierte con 'Restaurar' (estado previo respaldado)."
            : result.Message;
    }

    private async Task<string> RunAffinityToolAsync(string action, CancellationToken cancellationToken)
    {
        List<(string InstancePath, string Label)> gpus = await GetPciClassDevicesAsync([DisplayClassGuid], cancellationToken);
        if (action == "Escanear")
        {
            var lines = new StringBuilder();
            lines.Append(ToolHeader("Afinidad de interrupciones de GPU", "Solo lectura: muestra la política vigente por dispositivo."));
            if (gpus.Count == 0) lines.AppendLine("No se encontraron GPU PCI compatibles.");
            foreach ((string path, string label) in gpus)
            {
                string policyPath = AffinityPolicyPath(path);
                using RegistryKey? policy = Registry.LocalMachine.OpenSubKey(policyPath, writable: false);
                lines.AppendLine($"• {label}");
                lines.AppendLine($"    Clave: HKLM\\{policyPath}");
                lines.AppendLine($"    DevicePolicy: {ReadRegistryDisplay(policy, "DevicePolicy", "0 (MachineDefault: decide Windows)")}");
                lines.AppendLine($"    AssignmentSetOverride: {ReadRegistryDisplay(policy, "AssignmentSetOverride", "sin máscara")}");
            }
            lines.AppendLine("→ DevicePolicy=4 significa 'usar solo los procesadores de AssignmentSetOverride' (bit N = CPU lógica N).");
            lines.AppendLine("→ 'Aplicar' excluye CPU 0 y 1; 'Excluir Core 0' solo CPU 0; 'Todos los núcleos' permite todas; 'Restaurar' recupera el estado previo.");
            return lines.ToString();
        }
        if (action == "Excluir Core 0" || action == "Todos los núcleos")
        {
            if (gpus.Count == 0) return "No se encontraron GPU PCI compatibles.";
            int logicals = Environment.ProcessorCount;
            if (action == "Excluir Core 0" && logicals <= 1)
                return $"El equipo solo tiene {logicals} procesador lógico; no quedan núcleos libres para excluir el Core 0.";
            int usable = Math.Min(logicals, 64);
            ulong mask = 0;
            for (int cpu = action == "Excluir Core 0" ? 1 : 0; cpu < usable; cpu++) mask |= 1UL << cpu;
            if (action == "Todos los núcleos" && logicals > 64) mask = ulong.MaxValue;
            return await ApplyGpuAffinityAsync(gpus, mask,
                action == "Excluir Core 0" ? "Afinidad de GPU: excluir Core 0" : "Afinidad de GPU: todos los núcleos",
                action == "Excluir Core 0" ? $"CPU 1..{usable - 1} (se excluye solo CPU 0)" : $"CPU 0..{usable - 1} (todas las CPU del grupo)",
                cancellationToken);
        }
        if (action == "Aplicar")
        {
            if (gpus.Count == 0) return "No se encontraron GPU compatibles.";
            int logicals = Environment.ProcessorCount;
            if (logicals <= 2)
                return $"El equipo solo tiene {logicals} procesadores lógicos; no quedan núcleos libres para excluir los dos primeros.";
            int usable = Math.Min(logicals, 64);
            ulong mask = 0;
            for (int cpu = 2; cpu < usable; cpu++) mask |= 1UL << cpu;
            string assignmentBase64 = Convert.ToBase64String(BitConverter.GetBytes(mask)); // KAFFINITY de 64 bits, little endian
            var changes = new List<RegistryChange>();
            foreach ((string path, _) in gpus)
            {
                string keyPath = AffinityPolicyPath(path);
                changes.Add(new RegistryChange { Hive = "HKLM", KeyPath = keyPath, ValueName = "DevicePolicy", ValueKind = "DWord", ApplyValue = "4", RevertValue = "0" });
                changes.Add(new RegistryChange { Hive = "HKLM", KeyPath = keyPath, ValueName = "AssignmentSetOverride", ValueKind = "Binary", ApplyValue = assignmentBase64, RevertValue = assignmentBase64 });
            }
            ExecutionResult result = await ApplyToolRegistryAsync(57, changes, cancellationToken);
            return result.Success
                ? ToolHeader("Afinidad de GPU aplicada", $"GPU modificadas: {gpus.Count} · procesadores lógicos detectados: {logicals}.") +
                  result.Message + Environment.NewLine +
                  $"Máscara: 0x{mask:X} (CPU 2..{usable - 1}; se excluyen CPU 0 y 1)." +
                  (logicals > 64 ? " Solo se pueden direccionar 64 CPU por grupo de procesadores." : "") +
                  Environment.NewLine + "→ Requiere reinicio; comprueba el resultado con 'Escanear'."
                : result.Message;
        }
        if (action == "Restaurar")
        {
            var changes = new List<RegistryChange>();
            foreach ((string path, _) in gpus)
            {
                string keyPath = AffinityPolicyPath(path);
                changes.Add(new RegistryChange { Hive = "HKLM", KeyPath = keyPath, ValueName = "DevicePolicy", ValueKind = "DWord", ApplyValue = "0", RevertValue = "0" });
                changes.Add(new RegistryChange { Hive = "HKLM", KeyPath = keyPath, ValueName = "AssignmentSetOverride", ValueKind = "Binary", ApplyValue = "AAAAAAAAAAA=", RevertValue = "AAAAAAAAAAA=" });
            }
            ExecutionResult result = await RestoreToolRegistryAsync(57, changes, cancellationToken);
            return result.Success
                ? ToolHeader("Afinidad de GPU restaurada") + result.Message + Environment.NewLine + "→ Reinicia para que surta efecto."
                : result.Message;
        }
        return "Acción no reconocida.";
    }

    private async Task<string> RunNvidiaPanelToolAsync(string action, CancellationToken cancellationToken)
    {
        if (action == "Ver versión de driver")
        {
            string smi = NvidiaSmiPath();
            string quotedSmi = "'" + smi.Replace("'", "''") + "'";
            string output = "";
            try { output = (await RunPowerShellStaticAsync($"& {quotedSmi} --query-gpu=name,driver_version,vbios_version --format=csv", cancellationToken)).Trim(); } catch { }
            string fileVersion = "";
            try
            {
                string driver = Path.Combine(Environment.SystemDirectory, "drivers", "nvlddmkm.sys");
                fileVersion = File.Exists(driver) ? FileVersionInfo.GetVersionInfo(driver).FileVersion ?? "" : "";
            }
            catch { }
            var version = new StringBuilder();
            version.Append(ToolHeader("Versión del controlador NVIDIA", $"nvidia-smi: {smi}"));
            version.AppendLine(output.Length == 0 ? "nvidia-smi no devolvió datos de versión." : output);
            version.AppendLine($"Archivo nvlddmkm.sys: {(fileVersion.Length == 0 ? "no disponible" : fileVersion)}");
            version.AppendLine("→ Solo lectura: no se modifica el controlador. Para actualizarlo usa el Panel de NVIDIA o GeForce Experience.");
            return version.ToString();
        }
        string panel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "Control Panel Client", "nvcplui.exe");
        if (!File.Exists(panel))
            return "No se encontró el Panel de Control de NVIDIA en:" + Environment.NewLine + "  " + panel +
                Environment.NewLine + "Comprueba que el controlador NVIDIA esté instalado; en algunos sistemas el panel está en Program Files (x86).";
        if (action == "Abrir panel")
        {
            try { Process.Start(new ProcessStartInfo(panel) { UseShellExecute = true }); }
            catch (Exception ex) { return "No se pudo abrir el Panel de Control de NVIDIA: " + ex.Message; }
            return ToolHeader("Panel de control NVIDIA", $"Ejecutable: {panel}") +
                "Ruta: 'Ajustar la configuración y el tamaño del escritorio' → 'Ajustar el tamaño y la posición del escritorio' → 'Sin escalado'.\n" +
                "→ Esta herramienta solo abre el panel oficial; el ajuste de escalado lo aplicas tú desde esa interfaz.";
        }
        if (action == "Abrir ajustes de resolución")
        {
            try { Process.Start(new ProcessStartInfo(panel) { UseShellExecute = true }); }
            catch (Exception ex) { return "No se pudo abrir el Panel de Control de NVIDIA: " + ex.Message; }
            return ToolHeader("Ajustes de resolución NVIDIA", $"Ejecutable abierto: {panel}") +
                "En el panel: 'Mostrar' → 'Cambiar resolución' → selecciona resolución, frecuencia y 'Sin escalado'.\n" +
                "→ También puedes abrir los ajustes de Windows con el comando ms-settings:display.\n" +
                "→ Esta acción solo abre la interfaz oficial; no cambia ningún valor por sí misma.";
        }
        return "Acción no reconocida.";
    }

    private static string RunVibranceTool(string action, string input)
    {
        if (action == "Leer valor actual")
        {
            (bool ok, int level, string message) = DigitalVibranceReader.Read();
            return ToolHeader("Vibración digital NVIDIA", "Solo lectura: estado actual de las salidas NVIDIA.") +
                   (ok ? $"Nivel actual: {level}%{Environment.NewLine}Detalle: {message}"
                       : "No se pudo obtener el nivel actual. " + message) + Environment.NewLine +
                   "→ El ajuste no se conserva tras reiniciar; tras un reinicio el nivel suele volver al neutro (0%).";
        }
        int? preset = action switch
        {
            "Neutro (0)" => 0,
            "Preset Suave (25)" => 25,
            "Preset Competitivo (50)" => 50,
            "Preset Intenso (80)" => 80,
            _ => null
        };
        if (preset.HasValue) return ApplyVibrance(preset.Value, action);
        if (action != "Aplicar vibración") return "Acción no reconocida.";
        if (!int.TryParse(input?.Trim(), out int percent) || percent is < 0 or > 100)
            return "Introduce un nivel de 0 a 100 (por ejemplo 50) en el campo de texto.";
        return ApplyVibrance(percent, "Aplicar vibración");
    }

    private static string ApplyVibrance(int percent, string label)
    {
        string result = NvApiDrs.SetDigitalVibrance(percent);
        return ToolHeader("Vibración digital NVIDIA", $"Nivel solicitado: {percent}% · Acción: {label}.") + result + Environment.NewLine +
               "→ Para revertir: 'Neutro (0)' vuelve al color neutro, o 'Leer valor actual' muestra el nivel vigente." +
               Environment.NewLine + "→ El ajuste actúa sobre las salidas NVIDIA y no se conserva tras reiniciar.";
    }

    private async Task<string> ApplyPowerLimitAsync(double watts, string subtitle, string smi, string quotedSmi, CancellationToken cancellationToken)
    {
        string before = "", after = "", output = "";
        string query = $"& {quotedSmi} --query-gpu=power.limit --format=csv,noheader,nounits";
        try { before = (await RunPowerShellStaticAsync(query, cancellationToken)).Trim(); } catch { }
        double? previous = ParseDoubleInvariant(before);
        string backupKey = ToolBackupKey(71, "PowerLimit");
        try
        {
            if (previous.HasValue && await _backup.ReadOriginalAsync<PowerLimitBackup>(backupKey, cancellationToken) is null)
                await _backup.SaveOriginalAsync(backupKey, new PowerLimitBackup(smi, previous.Value), cancellationToken);
        }
        catch { }
        try { output = (await RunPowerShellAsync($"& {quotedSmi} -pl {watts.ToString(System.Globalization.CultureInfo.InvariantCulture)}", cancellationToken)).Trim(); }
        catch (Exception ex)
        {
            return ToolHeader("Límite de potencia", subtitle) + "No se pudo aplicar el límite: " + ex.Message;
        }
        try { after = (await RunPowerShellStaticAsync(query, cancellationToken)).Trim(); } catch { }
        return ToolHeader("Límite de potencia aplicado", subtitle) +
               $"Antes: {(before.Length == 0 ? "no disponible" : before + " W")}\n" +
               $"Después: {(after.Length == 0 ? "sin lectura (puede requerir administrador)" : after + " W")}\n" +
               (output.Length > 0 ? "Detalle: " + output + "\n" : "") +
               (previous.HasValue ? "Respaldo guardado del valor anterior.\n" : "") +
               "→ Para revertir: 'Restaurar límite' vuelve al valor respaldado (o al predeterminado de fábrica si no había respaldo).";
    }

    private async Task<string> RunPowerLimitToolAsync(string action, string input, CancellationToken cancellationToken)
    {
        string smi = NvidiaSmiPath();
        string quotedSmi = "'" + smi.Replace("'", "''") + "'";
        if (action == "Consultar")
        {
            string output = await RunPowerShellStaticAsync($"& {quotedSmi} -q -d POWER", cancellationToken);
            return ToolHeader("Límite de potencia de GPU NVIDIA", $"nvidia-smi: {smi}") +
                   (output.Trim().Length == 0 ? "nvidia-smi no devolvió datos de potencia." : output.Trim()) +
                   Environment.NewLine + "→ 'Power Limit Constraints' indica el rango admitido por tu GPU; 'Aplicar límite' fija el valor.";
        }
        if (action == "Ver rango")
        {
            string output = "";
            try { output = await RunPowerShellStaticAsync($"& {quotedSmi} -q -d POWER", cancellationToken); } catch (Exception ex) { output = ex.Message; }
            Match constraints = Regex.Match(output, @"(?im)^\s*Power Limit Constraints\s*:\s*([0-9]+(?:\.[0-9]+)?)\s*-\s*([0-9]+(?:\.[0-9]+)?)");
            Match fallback = Regex.Match(output, @"(?im)^\s*Max Power Limit\s*:\s*([0-9]+(?:\.[0-9]+)?)");
            Match defaultLimit = Regex.Match(output, @"(?im)^\s*Default Power Limit\s*:\s*([0-9]+(?:\.[0-9]+)?)");
            Match current = Regex.Match(output, @"(?im)^\s*Current Power Limit\s*:\s*([0-9]+(?:\.[0-9]+)?)");
            var range = new StringBuilder();
            range.Append(ToolHeader("Rango de límite de potencia", $"nvidia-smi: {smi}"));
            range.AppendLine(constraints.Success
                ? $"Rango admitido: {constraints.Groups[1].Value} - {constraints.Groups[2].Value} W"
                : $"Rango admitido: no disponible{(fallback.Success ? " (máximo declarado: " + fallback.Groups[1].Value + " W)" : "")}");
            range.AppendLine(defaultLimit.Success ? $"Límite predeterminado: {defaultLimit.Groups[1].Value} W" : "Límite predeterminado: no disponible");
            range.AppendLine(current.Success ? $"Límite actual: {current.Groups[1].Value} W" : "Límite actual: no disponible");
            if (output.Trim().Length > 0) range.AppendLine().AppendLine("Salida completa de nvidia-smi:").AppendLine(output.Trim());
            range.AppendLine("→ 'Preset 80%' y 'Preset 100%' calculan el valor sobre el máximo del rango; 'Restaurar límite' vuelve al valor previo.");
            return range.ToString();
        }
        if (action == "Restaurar límite")
        {
            string backupKey = ToolBackupKey(71, "PowerLimit");
            PowerLimitBackup? backup = await _backup.ReadOriginalAsync<PowerLimitBackup>(backupKey, cancellationToken);
            double? watts = backup?.Watts;
            string source = "respaldo anterior";
            if (watts is null)
            {
                string output = "";
                try { output = await RunPowerShellStaticAsync($"& {quotedSmi} -q -d POWER", cancellationToken); } catch { }
                Match def = Regex.Match(output, @"(?im)^\s*Default Power Limit\s*:\s*([0-9]+(?:\.[0-9]+)?)");
                if (def.Success) watts = ParseDoubleInvariant(def.Groups[1].Value);
                source = "predeterminado de fábrica";
            }
            if (watts is null || watts < 50 || watts > 1000)
                return "No hay un límite respaldado ni se pudo leer el predeterminado de fábrica; usa 'Consultar' y 'Aplicar límite' con el valor deseado.";
            string result = await ApplyPowerLimitAsync(watts.Value, $"Restauración desde {source} · objetivo {watts.Value:0.#} W · nvidia-smi: {smi}", smi, quotedSmi, cancellationToken);
            if (backup is not null) await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return result;
        }
        if (action is "Aplicar límite" or "Preset 80%" or "Preset 100%")
        {
            double? watts = null;
            if (action == "Aplicar límite")
            {
                string raw = (input ?? "").Trim().Replace(',', '.');
                watts = ParseDoubleInvariant(raw);
                if (watts is null || watts < 50 || watts > 1000)
                    return "Introduce un límite válido en vatios (entre 50 y 1000, por ejemplo 320). Usa 'Consultar' para ver el rango exacto de tu GPU.";
            }
            else
            {
                string output = "";
                try { output = await RunPowerShellStaticAsync($"& {quotedSmi} -q -d POWER", cancellationToken); } catch { }
                Match max = Regex.Match(output, @"(?im)^\s*Power Limit Constraints\s*:\s*[0-9]+(?:\.[0-9]+)?\s*-\s*([0-9]+(?:\.[0-9]+)?)");
                if (!max.Success) max = Regex.Match(output, @"(?im)^\s*Max Power Limit\s*:\s*([0-9]+(?:\.[0-9]+)?)");
                double? maximum = max.Success ? ParseDoubleInvariant(max.Groups[1].Value) : null;
                if (maximum is null)
                {
                    Match def = Regex.Match(output, @"(?im)^\s*Default Power Limit\s*:\s*([0-9]+(?:\.[0-9]+)?)");
                    if (def.Success) maximum = ParseDoubleInvariant(def.Groups[1].Value);
                }
                if (maximum is null) return "No se pudo determinar el máximo de tu GPU con nvidia-smi; usa 'Ver rango' y 'Aplicar límite' con un valor manual.";
                watts = Math.Round(maximum.Value * (action == "Preset 80%" ? 0.8 : 1.0));
                if (watts < 50 || watts > 1000) return $"El valor calculado ({watts.Value:0.#} W) queda fuera del rango seguro de esta herramienta; usa 'Aplicar límite' manualmente.";
            }
            return await ApplyPowerLimitAsync(watts.Value, $"Valor solicitado: {watts.Value:0.#} W · nvidia-smi: {smi}", smi, quotedSmi, cancellationToken);
        }
        return "Acción no reconocida.";
    }

    private static readonly (string Address, string Label)[] DnsCandidates =
    [
        ("1.1.1.1", "Cloudflare primario"),
        ("1.0.0.1", "Cloudflare secundario"),
        ("8.8.8.8", "Google primario"),
        ("8.8.4.4", "Google secundario"),
        ("9.9.9.9", "Quad9 primario"),
        ("149.112.112.112", "Quad9 secundario"),
        ("208.67.222.222", "OpenDNS primario"),
        ("4.2.2.2", "Level3")
    ];

    private async Task<string> RunDnsToolAsync(string action, string input, CancellationToken cancellationToken)
    {
        if (action == "Consultar DNS")
        {
            string script =
                "Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up' | ForEach-Object { " +
                "$a = $_; $c = Get-DnsClientServerAddress -InterfaceIndex $a.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue; " +
                "\"$($a.Name) (índice $($a.InterfaceIndex), $($a.InterfaceType)): \" + (@($c.ServerAddresses) -join ', ') } | Out-String";
            string output = await RunPowerShellStaticAsync(script, cancellationToken);
            return ToolHeader("DNS actual de los adaptadores físicos activos", "Solo lectura.") +
                   (output.Trim().Length == 0 ? "No se encontraron adaptadores físicos activos." : output.Trim()) +
                   Environment.NewLine + "→ 'Benchmark' mide la latencia de DNS públicos; 'Aplicar DNS' cambia el adaptador físico activo.";
        }
        if (action == "Benchmark")
        {
            var lines = new StringBuilder();
            var results = new List<(string Address, string Label, double AverageMs)>();
            lines.Append(ToolHeader("Benchmark de servidores DNS", "3 consultas por servidor; menor latencia = mejor respuesta."));
            foreach ((string address, string label) in DnsCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double total = 0;
                int ok = 0;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    double ms = await MeasureDnsAsync(address, cancellationToken);
                    if (ms >= 0) { total += ms; ok++; }
                }
                double average = ok > 0 ? total / ok : -1;
                results.Add((address, label, average));
                lines.AppendLine($"• {address} ({label}): {(average >= 0 ? average.ToString("0.0") + " ms" : "sin respuesta")} — aciertos {ok}/3");
            }
            List<(string Address, string Label, double AverageMs)> best = results.Where(r => r.AverageMs >= 0).OrderBy(r => r.AverageMs).ToList();
            if (best.Count > 0)
            {
                lines.AppendLine();
                lines.AppendLine($"Ganador: {best[0].Address} ({best[0].Label}, {best[0].AverageMs:0.0} ms)" +
                    (best.Count > 1 ? $"; secundario recomendado: {best[1].Address} ({best[1].Label})." : "."));
                lines.AppendLine("→ Introduce esas IP en 'Aplicar DNS' separadas por un espacio (primario secundario).");
            }
            else lines.AppendLine("→ Ningún servidor respondió; revisa la conexión o el puerto UDP 53.");
            return lines.ToString();
        }
        if (action == "Aplicar DNS")
        {
            string[] parts = (input ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is 0 or > 2 || parts.Any(part => !IPAddress.TryParse(part, out _)))
                return "Introduce una o dos IP válidas separadas por un espacio (por ejemplo: 1.1.1.1 1.0.0.1).";
            string primary = parts[0];
            string secondary = parts.Length > 1 ? parts[1] : "";
            string script =
                "$nic = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { Status -eq 'Up' -and InterfaceType -ne 'Tunnel' } | Sort-Object InterfaceMetric | Select-Object -First 1; " +
                "if (-not $nic) { $nic = Get-NetAdapter | Where-Object { Status -eq 'Up' -and Virtual -eq $false -and InterfaceType -ne 'Tunnel' } | Select-Object -First 1 }; " +
                "if (-not $nic) { throw 'No se encontró un adaptador de red físico activo.' }; " +
                "$before = (@(Get-DnsClientServerAddress -InterfaceIndex $nic.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses) -join ', '; " +
                "netsh interface ip set dns name=\"$($nic.Name)\" static __PRIMARY__; " +
                (secondary.Length > 0 ? "netsh interface ip set dns name=\"$($nic.Name)\" static __SECONDARY__ index=2; " : "") +
                "$after = (@(Get-DnsClientServerAddress -InterfaceIndex $nic.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses) -join ', '; " +
                "\"Adaptador: $($nic.Name) (índice $($nic.InterfaceIndex), $($nic.InterfaceType))\"; " +
                "\"DNS anterior: $before\"; \"DNS actual: $after\"";
            script = script.Replace("__PRIMARY__", primary).Replace("__SECONDARY__", secondary);
            string output = await RunPowerShellAsync(script, cancellationToken);
            return ToolHeader("DNS aplicado", "Primario: " + primary + (secondary.Length > 0 ? " · Secundario: " + secondary : " (sin secundario)")) +
                   output.Trim() + Environment.NewLine +
                   "→ Para volver al DNS anterior, vuelve a ejecutar esta acción con las IP que aparecen en 'DNS anterior'.";
        }
        return "Acción no reconocida.";
    }

    private static async Task<double> MeasureDnsAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            byte[] query =
            [
                0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                0x03, (byte)'c', (byte)'o', (byte)'m', 0x00, 0x00, 0x01, 0x00, 0x01
            ];
            using var client = new UdpClient();
            client.Client.ReceiveTimeout = 1500;
            client.Client.SendTimeout = 1500;
            Stopwatch watch = Stopwatch.StartNew();
            await client.SendAsync(query, query.Length, address, 53);
            UdpReceiveResult response = await client.ReceiveAsync(new CancellationTokenSource(1500).Token);
            watch.Stop();
            return response.Buffer.Length >= 12 ? watch.Elapsed.TotalMilliseconds : -1;
        }
        catch (OperationCanceledException) { return -1; }
        catch (SocketException) { return -1; }
        catch (ObjectDisposedException) { return -1; }
    }

    private async Task<string> RunTrimToolAsync(string action, CancellationToken cancellationToken)
    {
        if (action == "Verificar")
        {
            string trimState = await RunPowerShellStaticAsync("fsutil behavior query DisableDeleteNotify", cancellationToken);
            string mediaType = await RunPowerShellStaticAsync("Get-PhysicalDisk | Select-Object FriendlyName, MediaType, HealthStatus | Format-Table -AutoSize | Out-String", cancellationToken);
            return ToolHeader("Estado de TRIM", "DisableDeleteNotify = 0 significa TRIM activado a nivel de sistema.") +
                   trimState.Trim() + Environment.NewLine + Environment.NewLine + mediaType.Trim() +
                   Environment.NewLine + "→ 'Optimizar' ejecuta ReTrim sobre la unidad del sistema y muestra el resultado.";
        }
        if (action == "Optimizar")
        {
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            string driveLetter = systemRoot.Length > 0 ? char.ToUpperInvariant(systemRoot[0]).ToString() : "C";
            string script =
                "$ssd = @(Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object MediaType -eq 'SSD' | Select-Object -ExpandProperty FriendlyName); " +
                "\"Unidad del sistema: " + driveLetter + ":\\\"; \"SSD detectados: \" + $ssd.Count; " +
                "if ($ssd.Count -gt 0) { \"Modelos: \" + ($ssd -join ', ') }; " +
                "Optimize-Volume -DriveLetter " + driveLetter + " -ReTrim -Verbose";
            string output = await RunPowerShellStaticAsync(script, cancellationToken);
            return ToolHeader("TRIM / ReTrim ejecutado", $"Volumen optimizado: {driveLetter}:\\") + output.Trim() +
                   Environment.NewLine + "→ Pulsa 'Verificar' para confirmar el estado y la salud de las unidades.";
        }
        if (action == "Forzar ReTrim")
        {
            string script =
                "$ErrorActionPreference='SilentlyContinue'; " +
                "$drives = @(Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.DriveType -eq 'Fixed' -and $_.DriveLetter }); " +
                "\"Volúmenes fijos detectados: \" + $drives.Count; " +
                "foreach ($d in $drives) { \"--- Unidad $($d.DriveLetter): ---\"; Optimize-Volume -DriveLetter $d.DriveLetter -ReTrim; " +
                "if ($?) { \"  ReTrim completado.\" } else { \"  ReTrim no completado (revisa permisos o tipo de unidad).\" } }";
            string output = await RunPowerShellStaticAsync(script, cancellationToken);
            return ToolHeader("ReTrim forzado en volúmenes fijos", "Ejecuta Optimize-Volume -ReTrim en todas las unidades fijas detectadas.") +
                   (output.Trim().Length == 0 ? "PowerShell no devolvió salida." : output.Trim()) +
                   Environment.NewLine + "→ Operación de mantenimiento: no elimina archivos. Para confirmar el estado usa 'Verificar'.";
        }
        if (action == "Estado SMART")
        {
            string script =
                "$ErrorActionPreference='SilentlyContinue'; " +
                "'--- Discos físicos ---'; " +
                "Get-PhysicalDisk | Select-Object FriendlyName, MediaType, HealthStatus, OperationalStatus | Format-Table -AutoSize | Out-String; " +
                "'--- Contadores de fiabilidad (SMART) ---'; " +
                "$counters = @(Get-PhysicalDisk | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue); " +
                "if ($counters.Count -eq 0) { 'El controlador no expone contadores SMART para estos discos.' } " +
                "else { $counters | Select-Object DeviceId, Temperature, Wear, ReadErrorsTotal, WriteErrorsTotal, PowerOnHours | Format-Table -AutoSize | Out-String }";
            string output = await RunPowerShellStaticAsync(script, cancellationToken);
            return ToolHeader("Estado SMART de los discos", "Solo lectura: salud, temperatura y contadores de error cuando el controlador los expone.") +
                   (output.Trim().Length == 0 ? "No se obtuvieron datos SMART." : output.Trim()) +
                   Environment.NewLine + "→ HealthStatus=Healthy indica que el disco no reporta fallos; 'Forzar ReTrim' no altera estos contadores.";
        }
        return "Acción no reconocida.";
    }

    private async Task<string> RunDeepCleanToolAsync(string action, CancellationToken cancellationToken)
    {
        var lines = new StringBuilder();
        if (action == "Analizar")
        {
            long total = 0;
            lines.Append(ToolHeader("Análisis de temporales y cachés", "Solo lectura: no se elimina ningún archivo."));
            foreach (string folder in DeepCleanTargets())
            {
                long bytes = await Task.Run(() => GetFolderSizeSafe(folder), cancellationToken);
                total += bytes;
                lines.AppendLine($"• {folder}");
                lines.AppendLine($"    {(bytes > 0 ? FormatBytes(bytes) : "vacía o inexistente")}");
            }
            lines.AppendLine($"Total susceptible de limpieza: {FormatBytes(total)}.");
            lines.AppendLine("→ 'Limpiar' elimina los archivos de estas rutas; cierra los juegos antes para no borrar cachés en uso.");
            return lines.ToString();
        }
        if (action == "Limpiar")
        {
            long freed = 0;
            lines.Append(ToolHeader("Limpieza de temporales y cachés"));
            foreach (string folder in DeepCleanTargets())
            {
                long bytes = await Task.Run(() => CleanFolderSafe(folder), cancellationToken);
                freed += bytes;
                lines.AppendLine($"• {folder}: {FormatBytes(bytes)} eliminados.");
            }
            lines.AppendLine($"Total liberado: {FormatBytes(freed)}.");
            lines.AppendLine("→ Los archivos bloqueados por otros procesos no se borraron; vuelve a 'Analizar' tras reiniciar.");
            return lines.ToString();
        }
        if (action is "Solo temporales" or "Solo cachés GPU")
        {
            List<string> targets = action == "Solo temporales" ? TempCleanTargets() : GpuCacheCleanTargets();
            long before = 0, freed = 0;
            var report = new StringBuilder();
            report.Append(ToolHeader(action == "Solo temporales" ? "Limpieza de temporales" : "Limpieza de cachés GPU",
                "Solo se eliminan las rutas reconocidas de esta categoría."));
            foreach (string folder in targets)
            {
                long size = await Task.Run(() => GetFolderSizeSafe(folder), cancellationToken);
                before += size;
                long removed = await Task.Run(() => CleanFolderSafe(folder), cancellationToken);
                freed += removed;
                report.AppendLine($"• {folder}: {FormatBytes(size)} antes · {FormatBytes(removed)} eliminados.");
            }
            report.AppendLine($"Total antes: {FormatBytes(before)} · liberado: {FormatBytes(freed)} · restante estimado: {FormatBytes(Math.Max(0, before - freed))}.");
            report.AppendLine("→ No hay reversión automática: se borraron solo archivos temporales/cachés que los programas regeneran; los archivos en uso se omitieron.");
            return report.ToString();
        }
        if (action == "Exportar informe")
        {
            string text = await RunDeepCleanToolAsync("Analizar", cancellationToken);
            string export = await WriteApexReportAsync("InformeLimpieza", text, cancellationToken);
            return ToolHeader("Informe de limpieza exportado") + export +
                   Environment.NewLine + "→ El informe solo contiene lecturas: no se ha eliminado ningún archivo al exportarlo.";
        }
        return "Acción no reconocida.";
    }

    private static List<string> TempCleanTargets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return
        [
            Path.Combine(local, "Temp"),
            Path.Combine(windows, "Temp"),
            Path.Combine(windows, "SoftwareDistribution", "Download")
        ];
    }

    private static List<string> GpuCacheCleanTargets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            Path.Combine(local, "D3DSCache"),
            Path.Combine(local, "NVIDIA", "DXCache"),
            Path.Combine(local, "NVIDIA", "GLCache")
        ];
    }

    private static List<string> DeepCleanTargets() => [.. TempCleanTargets(), .. GpuCacheCleanTargets()];

    private static long GetFolderSizeSafe(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Sum(file => { try { return new FileInfo(file).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    private static long CleanFolderSafe(string folder)
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(folder)) return 0;
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    long length = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += length;
                }
                catch { }
            }
        }
        catch { }
        return freed;
    }

    private async Task<string> RunAppxToolAsync(string action, string input, CancellationToken cancellationToken)
    {
        if (action == "Listar")
        {
            string output = await RunPowerShellStaticAsync(
                "Get-AppxPackage -PackageTypeFilter Main | Where-Object { -not $_.NonRemovable } | Sort-Object Name | ForEach-Object { $_.Name + '  |  ' + $_.PackageFullName } | Out-String",
                cancellationToken);
            return ToolHeader("Paquetes Appx del usuario actual", "Solo lectura: usa el valor de la columna izquierda como nombre para Desinstalar/Restaurar.") +
                   (output.Trim().Length == 0 ? "No hay paquetes listables." : output.Trim());
        }
        if (action == "Desinstalar")
        {
            string package = (input ?? "").Trim();
            if (package.Length == 0) return "Escribe el nombre del paquete (por ejemplo Microsoft.BingWeather) y vuelve a pulsar.";
            string script =
                "$name = '" + EscapePsSingleQuotes(package) + "'; " +
                "$pkg = @(Get-AppxPackage -Name $name -ErrorAction SilentlyContinue); " +
                "if ($pkg.Count -eq 0) { throw 'No se encontró ningún paquete con ese nombre; pulsa Listar para ver los nombres exactos.' }; " +
                "\"Paquetes coincidentes: \" + $pkg.Count; " +
                "$pkg | ForEach-Object { \"  $($_.Name) $($_.Version) — $($_.PackageFullName)\" }; " +
                "$pkg | Remove-AppxPackage -ErrorAction Stop; " +
                "\"Restantes tras la desinstalación: \" + @(Get-AppxPackage -Name $name -ErrorAction SilentlyContinue).Count";
            string output = await RunPowerShellAsync(script, cancellationToken);
            return ToolHeader("Paquete Appx desinstalado", $"Nombre indicado: {package}") + output.Trim() +
                   Environment.NewLine + "→ Solo se quita para el usuario actual; 'Restaurar' con el mismo nombre vuelve a registrarlo.";
        }
        if (action == "Restaurar")
        {
            string package = (input ?? "").Trim();
            if (package.Length == 0) return "Escribe el nombre del paquete a restaurar (por ejemplo Microsoft.BingWeather).";
            string script =
                "$name = '" + EscapePsSingleQuotes(package) + "'; " +
                "$pkg = Get-AppxPackage -AllUsers -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1; " +
                "if (-not $pkg) { 'El paquete no está instalado para ningún usuario; instálalo desde la Store.' } " +
                "else { \"Manifest: $($pkg.InstallLocation)\\AppxManifest.xml\"; " +
                "Add-AppxPackage -Register -DisableDevelopmentMode \"$($pkg.InstallLocation)\\AppxManifest.xml\" -ErrorAction Stop; " +
                "\"Registrado de nuevo: $($pkg.Name) $($pkg.Version)\" }";
            string output = await RunPowerShellAsync(script, cancellationToken);
            return ToolHeader("Paquete Appx restaurado", $"Nombre indicado: {package}") + output.Trim() +
                   Environment.NewLine + "→ Comprueba el resultado con 'Listar'.";
        }
        return "Acción no reconocida.";
    }

    private async Task<string> RunWindowsUpdateToolAsync(string action, CancellationToken cancellationToken)
    {
        const string policyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
        RegistryChange noAutoUpdate = new() { Hive = "HKLM", KeyPath = policyKey, ValueName = "NoAutoUpdate", ValueKind = "DWord", ApplyValue = "1", RevertValue = "0" };
        RegistryChange auOptions = new() { Hive = "HKLM", KeyPath = policyKey, ValueName = "AUOptions", ValueKind = "DWord", ApplyValue = "2", RevertValue = "3" };
        if (action == "Verificar")
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(policyKey, writable: false);
            string serviceState = "";
            try { serviceState = (await RunPowerShellStaticAsync("(Get-Service wuauserv -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Status) -as [string]", cancellationToken)).Trim(); } catch { }
            var lines = new StringBuilder();
            lines.Append(ToolHeader("Estado de Windows Update", $"Clave de directiva: HKLM\\{policyKey}"));
            lines.AppendLine($"NoAutoUpdate: {ReadRegistryDisplay(key, "NoAutoUpdate", "0 (instalación automática habilitada)")}");
            lines.AppendLine($"AUOptions: {ReadRegistryDisplay(key, "AUOptions", "sin directiva (3 = descargar e instalar automáticamente)")}");
            lines.AppendLine($"Servicio wuauserv: {(serviceState.Length == 0 ? "no disponible" : serviceState)}");
            lines.AppendLine("→ AUOptions: 2=notificar, 3=descargar e instalar, 4=programar, 5=el administrador local elige.");
            lines.AppendLine("→ 'Pausar 4 semanas' y 'Modo Manual' respaldan los valores anteriores; 'Reanudar' los restaura.");
            return lines.ToString();
        }
        switch (action)
        {
            case "Pausar 4 semanas":
            {
                ExecutionResult result = await ApplyToolRegistryAsync(111, [noAutoUpdate], cancellationToken);
                if (!result.Success) return result.Message;
                string svc = "";
                try { svc = (await RunPowerShellStaticAsync("Stop-Service wuauserv -Force -ErrorAction SilentlyContinue; Set-Service wuauserv -StartupType Disabled -ErrorAction SilentlyContinue; (Get-Service wuauserv).Status", cancellationToken)).Trim(); } catch { }
                return ToolHeader("Windows Update pausado", "NoAutoUpdate=1 y servicio wuauserv deshabilitado.") + result.Message +
                       Environment.NewLine + $"Estado del servicio: {(svc.Length == 0 ? "no disponible" : svc)}" +
                       Environment.NewLine + "→ Usa 'Reanudar' para recuperar la directiva previa y dejar el servicio en Manual.";
            }
            case "Modo Manual":
            {
                if (await _backup.ReadOriginalAsync<DevicePowerSnapshot>(ToolRegistryBackupKey(111), cancellationToken) is not null)
                    return "Ya hay un cambio pendiente de Windows Update. Pulsa 'Reanudar' antes de aplicar 'Modo Manual'.";
                ExecutionResult result = await ApplyToolRegistryAsync(111,
                [
                    new RegistryChange { Hive = "HKLM", KeyPath = policyKey, ValueName = "NoAutoUpdate", ValueKind = "DWord", ApplyValue = "0", RevertValue = "0" },
                    auOptions
                ], cancellationToken);
                return result.Success
                    ? ToolHeader("Windows Update en modo manual", "NoAutoUpdate=0 y AUOptions=2 (notificar antes de descargar/instalar).") +
                      result.Message + Environment.NewLine + "→ 'Verificar' muestra los valores vigentes; 'Reanudar' restaura la configuración previa."
                    : result.Message;
            }
            case "Reanudar":
            {
                bool hadBackup = await _backup.ReadOriginalAsync<DevicePowerSnapshot>(ToolRegistryBackupKey(111), cancellationToken) is not null;
                if (hadBackup)
                {
                    ExecutionResult result = await RestoreToolRegistryAsync(111, [noAutoUpdate, auOptions], cancellationToken);
                    if (!result.Success) return result.Message;
                }
                string svc = "";
                try { svc = (await RunPowerShellStaticAsync("Set-Service wuauserv -StartupType Manual -ErrorAction SilentlyContinue; Start-Service wuauserv -ErrorAction SilentlyContinue; (Get-Service wuauserv).Status", cancellationToken)).Trim(); } catch { }
                return ToolHeader("Windows Update reanudado", hadBackup ? "Se restauraron las directivas previas y el servicio quedó en Manual." : "No había directivas respaldadas; el servicio se dejó en Manual.") +
                       $"Estado del servicio: {(svc.Length == 0 ? "no disponible" : svc)}";
            }
            default:
                return "Acción no reconocida.";
        }
    }

    private async Task<string> RunRepairToolAsync(string action, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "DISM RestoreHealth":
                return ToolHeader("Reparación de la imagen de Windows (DISM)", "Puede tardar varios minutos; no cierres la ventana.") +
                       await RunPowerShellStaticAsync("DISM /Online /Cleanup-Image /RestoreHealth", cancellationToken);
            case "SFC /scannow":
                return ToolHeader("Comprobación de archivos protegidos del sistema (SFC)") +
                       await RunPowerShellStaticAsync("sfc /scannow", cancellationToken);
            case "Reset Winsock":
                return ToolHeader("Reinicio del catálogo de sockets (Winsock)") +
                       await RunPowerShellAsync("netsh winsock reset", cancellationToken) +
                       Environment.NewLine + "→ Reinicia el equipo para completar el reinicio del catálogo.";
            case "Reset TCP/IP":
                return ToolHeader("Restablecimiento de la pila TCP/IP") +
                       await RunPowerShellAsync("netsh int ip reset", cancellationToken) +
                       Environment.NewLine + "→ Reinicia el equipo; la configuración manual de IP/DNS puede haberse perdido.";
            case "Runtimes VC++":
            {
                string script =
                    "$paths = @('HKLM:\\SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\X64'," +
                    "'HKLM:\\SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\X86'," +
                    "'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\X86'); " +
                    "foreach ($p in $paths) { if (Test-Path $p) { $v = Get-ItemProperty $p -ErrorAction SilentlyContinue; " +
                    "\"$p : versión $($v.Version), instalado=$($v.Installed)\" } else { \"$p : no instalado\" } }; " +
                    "'--- Paquetes VCLibs (Appx) ---'; " +
                    "Get-AppxPackage -Name 'Microsoft.VCLibs*' -ErrorAction SilentlyContinue | Select-Object Name, Version | Format-Table -AutoSize | Out-String";
                string output = await RunPowerShellStaticAsync(script, cancellationToken);
                return ToolHeader("Runtimes de Visual C++", "Solo lectura: revisa los redistribuibles Win32 y los paquetes VCLibs.") + output.Trim() +
                    Environment.NewLine + "→ Si falta 'Microsoft Visual C++ 2015-2022 Redistributable (x64)', instálalo desde https://aka.ms/vs/17/release/vc_redist.x64.exe";
            }
            case "DirectX":
            {
                string script =
                    "$d = Get-Item \"$env:windir\\System32\\d3d12.dll\" -ErrorAction SilentlyContinue; " +
                    "if ($d) { \"d3d12.dll: \" + $d.VersionInfo.FileVersion } else { 'd3d12.dll no encontrado' }; " +
                    "\"Versión DirectX registrada: \" + ((Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\DirectX' -ErrorAction SilentlyContinue).Version -as [string])";
                string output = await RunPowerShellStaticAsync(script, cancellationToken);
                return ToolHeader("Componentes DirectX", "Solo lectura: versión instalada y enlace oficial de reparación.") + output.Trim() +
                    Environment.NewLine + "→ Para reinstalar el runtime clásico (D3DX/XINPUT), usa el paquete oficial: https://www.microsoft.com/download/details.aspx?id=35";
            }
            default:
                return "Acción no reconocida.";
        }
    }

    private async Task<string> RunBufferToolAsync(string action, string input, string displayName, CancellationToken cancellationToken)
    {
        // -DisplayName de Set-NetAdapterAdvancedProperty NO acepta comodines: hay que pasarle
        // los objetos devueltos por Get-NetAdapterAdvancedProperty (que sí filtra con -like).
        string query =
            "$props = @(Get-NetAdapterAdvancedProperty -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*" + displayName + "*' }); " +
            "if ($props.Count -eq 0) { 'No se encontró la propiedad " + displayName + " en ningún adaptador.' } " +
            "else { $props | Select-Object Name, DisplayName, RegistryValue, ValidRegistryValues | Format-List | Out-String }";
        if (action == "Consultar")
        {
            string output = await RunPowerShellStaticAsync(query, cancellationToken);
            return ToolHeader("Búfer: " + displayName, "Solo lectura: valor actual y valores admitidos por el controlador.") + output.Trim();
        }
        if (action == "Restaurar valor previo")
        {
            int toolId = displayName.Contains("Receive", StringComparison.OrdinalIgnoreCase) ? 129 : 130;
            string backupKey = ToolBackupKey(toolId, "Buffer");
            BufferBackup? backup = await _backup.ReadOriginalAsync<BufferBackup>(backupKey, cancellationToken);
            if (backup is null) return "No hay un valor previo respaldado de este búfer; aplica un valor primero.";
            var report = new StringBuilder();
            report.Append(ToolHeader("Búfer restaurado: " + displayName, $"Valores respaldados: {backup.Values.Count}."));
            foreach (BufferValueBackup entry in backup.Values)
            {
                string script =
                    "$props = @(Get-NetAdapterAdvancedProperty -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq '" + EscapePsSingleQuotes(entry.RegistryName) + "' -and $_.InterfaceDescription -eq '" + EscapePsSingleQuotes(entry.InterfaceDescription) + "' }); " +
                    "if ($props.Count -eq 0) { 'No se encontró el adaptador; se omite.' } " +
                    "else { $props | Set-NetAdapterAdvancedProperty -RegistryValue ([uint32]" + EscapePsSingleQuotes(entry.Value) + ") -ErrorAction Stop; " +
                    "\"$($props[0].InterfaceDescription): \" + (@(Get-NetAdapterAdvancedProperty -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq '" + EscapePsSingleQuotes(entry.RegistryName) + "' -and $_.InterfaceDescription -eq '" + EscapePsSingleQuotes(entry.InterfaceDescription) + "' }) | Select-Object -First 1 -ExpandProperty RegistryValue) }";
                try { report.AppendLine(entry.InterfaceDescription + ": " + (await RunPowerShellAsync(script, cancellationToken)).Trim()); }
                catch (Exception ex) { report.AppendLine(entry.InterfaceDescription + ": no se pudo restaurar (" + ex.Message + ")."); }
            }
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            report.AppendLine("→ El respaldo se ha consumido; vuelve a aplicar un valor para crear uno nuevo.");
            return report.ToString();
        }
        uint? preset = action switch
        {
            "Preset 512" => 512u,
            "Preset 1024" => 1024u,
            "Preset 2048" => 2048u,
            _ => null
        };
        if (action == "Aplicar")
        {
            if (!uint.TryParse(input?.Trim(), out uint value) || value < 128 || value > 8192)
                return "Introduce un valor entre 128 y 8192 (comprueba los valores válidos con 'Consultar').";
            return await ApplyBufferValueAsync(displayName, value, (input ?? "").Trim(), cancellationToken);
        }
        if (preset.HasValue) return await ApplyBufferValueAsync(displayName, preset.Value, preset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        return "Acción no reconocida.";
    }

    private async Task<string> ApplyBufferValueAsync(string displayName, uint value, string rawValue, CancellationToken cancellationToken)
    {
        int toolId = displayName.Contains("Receive", StringComparison.OrdinalIgnoreCase) ? 129 : 130;
        string backupKey = ToolBackupKey(toolId, "Buffer");
        bool hasBackup = false;
        try { hasBackup = await _backup.ReadOriginalAsync<BufferBackup>(backupKey, cancellationToken) is not null; } catch { }
        if (!hasBackup)
        {
            string readScript =
                "$props = @(Get-NetAdapterAdvancedProperty -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*" + displayName + "*' }); " +
                "$props | ForEach-Object { \"$($_.InterfaceDescription)|$($_.Name)|$($_.RegistryValue)\" }";
            try
            {
                string lines = await RunPowerShellStaticAsync(readScript, cancellationToken);
                var values = new List<BufferValueBackup>();
                foreach (string line in lines.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length == 3) values.Add(new BufferValueBackup(parts[0], parts[1], parts[2]));
                }
                if (values.Count > 0) await _backup.SaveOriginalAsync(backupKey, new BufferBackup(displayName, values), cancellationToken);
            }
            catch { }
        }
        string script =
            "$props = @(Get-NetAdapterAdvancedProperty -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*" + displayName + "*' }); " +
            "if ($props.Count -eq 0) { throw 'No se encontró la propiedad " + displayName + " en ningún adaptador; usa Consultar.' }; " +
            "$antes = ($props | ForEach-Object { \"$($_.Name)=$($_.RegistryValue)\" }) -join '; '; " +
            "$props | Set-NetAdapterAdvancedProperty -RegistryValue " + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " -ErrorAction Stop; " +
            "$despues = (@(Get-NetAdapterAdvancedProperty -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*" + displayName + "*' }) | ForEach-Object { \"$($_.Name)=$($_.RegistryValue)\" }) -join '; '; " +
            "\"Adaptadores afectados: \" + $props.Count; \"Propiedad: " + displayName + "\"; \"Antes: $antes\"; \"Después: $despues\"";
        string output = await RunPowerShellAsync(script, cancellationToken);
        return ToolHeader("Búfer aplicado: " + displayName, $"Valor solicitado: {rawValue} · adaptadores afectados se indican abajo.") + output.Trim() +
               Environment.NewLine + "→ Para revertir automáticamente usa 'Restaurar valor previo' (respaldo guardado) o vuelve a aplicar el valor de 'Antes'.";
    }

    private async Task<string> RunBrowserToolAsync(string action, CancellationToken cancellationToken)
    {
        const string chromeKey = @"SOFTWARE\Policies\Google\Chrome";
        const string edgeKey = @"SOFTWARE\Policies\Microsoft\Edge";
        const string braveKey = @"SOFTWARE\Policies\BraveSoftware\Brave";
        if (action == "Verificar políticas")
        {
            var lines = new StringBuilder();
            lines.Append(ToolHeader("Políticas de navegadores", "Solo lectura: 0 = métrica desactivada, 1 = activada."));
            var catalog = new[]
            {
                ("Chrome", chromeKey, new[] { "MetricsReportingEnabled", "UrlKeyedAnonymizedDataCollectionEnabled", "BackgroundModeEnabled" }),
                ("Edge", edgeKey, new[] { "DiagnosticData", "StartupBoostEnabled" }),
                ("Brave", braveKey, new[] { "MetricsReportingEnabled" })
            };
            foreach ((string browser, string key, string[] values) in catalog)
            {
                using RegistryKey? reg = Registry.LocalMachine.OpenSubKey(key, writable: false);
                lines.AppendLine($"• {browser} — HKLM\\{key}");
                foreach (string value in values)
                    lines.AppendLine($"    {value}: {ReadRegistryDisplay(reg, value, "sin política")}");
            }
            lines.AppendLine("→ 'Aplicar políticas' fija todas estas métricas a 0 y guarda el estado previo; 'Restaurar políticas' lo devuelve.");
            return lines.ToString();
        }
        if (action == "Aplicar políticas" || action == "Restaurar políticas")
        {
            string apply = action == "Aplicar políticas" ? "0" : "1";
            var changes = new List<RegistryChange>
            {
                new() { Hive = "HKLM", KeyPath = chromeKey, ValueName = "MetricsReportingEnabled", ValueKind = "DWord", ApplyValue = apply, RevertValue = "1" },
                new() { Hive = "HKLM", KeyPath = chromeKey, ValueName = "UrlKeyedAnonymizedDataCollectionEnabled", ValueKind = "DWord", ApplyValue = apply, RevertValue = "1" },
                new() { Hive = "HKLM", KeyPath = chromeKey, ValueName = "BackgroundModeEnabled", ValueKind = "DWord", ApplyValue = apply, RevertValue = "1" },
                new() { Hive = "HKLM", KeyPath = edgeKey, ValueName = "DiagnosticData", ValueKind = "DWord", ApplyValue = apply, RevertValue = "1" },
                new() { Hive = "HKLM", KeyPath = edgeKey, ValueName = "StartupBoostEnabled", ValueKind = "DWord", ApplyValue = apply, RevertValue = "1" },
                new() { Hive = "HKLM", KeyPath = braveKey, ValueName = "MetricsReportingEnabled", ValueKind = "DWord", ApplyValue = apply, RevertValue = "1" }
            };
            ExecutionResult result = action == "Aplicar políticas"
                ? await ApplyToolRegistryAsync(144, changes, cancellationToken)
                : await RestoreToolRegistryAsync(144, changes, cancellationToken);
            return result.Success
                ? ToolHeader(action == "Aplicar políticas" ? "Políticas de privacidad aplicadas" : "Políticas restauradas",
                    $"Valores escritos: {changes.Count} en Chrome, Edge y Brave.") + result.Message +
                  Environment.NewLine + "→ Reinicia los navegadores y comprueba el estado con 'Verificar políticas'."
                : result.Message;
        }
        if (action.StartsWith("Instalar", StringComparison.Ordinal))
        {
            string browser = action switch
            {
                "Instalar Brave" => "Brave.Brave",
                "Instalar Firefox" => "Mozilla.Firefox",
                "Instalar Vivaldi" => "Vivaldi.Vivaldi",
                _ => ""
            };
            if (browser.Length == 0) return "Acción no reconocida.";
            string output = await RunPowerShellAsync($"winget install --id {browser} --silent --accept-source-agreements --accept-package-agreements", cancellationToken);
            return ToolHeader("Instalación solicitada", $"Paquete winget: {browser}") + output.Trim() +
                   Environment.NewLine + "→ Si winget no está instalado, actualiza Microsoft Store o instala App Installer.";
        }
        return "Acción no reconocida.";
    }

    private async Task<string> RunGhostDeviceToolAsync(string action, CancellationToken cancellationToken)
    {
        if (action == "Escanear")
        {
            string output = await RunPowerShellStaticAsync(
                "$present = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Select-Object -ExpandProperty InstanceId; " +
                "$ghosts = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $present -notcontains $_.InstanceId -and $_.InstanceId -and $_.InstanceId -notlike 'SWD*' }); " +
                "\"Dispositivos no presentes: \" + $ghosts.Count; " +
                "if ($ghosts.Count -gt 0) { $ghosts | Select-Object Status, Class, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String }",
                cancellationToken);
            return ToolHeader("Dispositivos fantasma (no presentes)", "Solo lectura: entradas huérfanas del Administrador de dispositivos.") +
                   (output.Trim().Length == 0 ? "No hay dispositivos fantasma." : output.Trim()) +
                   Environment.NewLine + "→ 'Limpiar' elimina estas entradas; 'Buscar cambios' fuerza un nuevo escaneo PnP.";
        }
        if (action == "Limpiar")
        {
            string output = await RunPowerShellStaticAsync(
                "$present = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Select-Object -ExpandProperty InstanceId; " +
                "$ghosts = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $present -notcontains $_.InstanceId -and $_.InstanceId -and $_.InstanceId -notlike 'SWD*' }); " +
                "$removed = 0; $failed = 0; foreach ($g in $ghosts) { pnputil /remove-device \"$($g.InstanceId)\" 2>$null; if ($LASTEXITCODE -eq 0) { $removed++ } else { $failed++ } }; " +
                "\"Encontrados: \" + $ghosts.Count + \"; eliminados: \" + $removed + \"; con error: \" + $failed",
                cancellationToken);
            return ToolHeader("Limpieza de dispositivos fantasma") + output.Trim() +
                   Environment.NewLine + "→ Vuelve a 'Escanear' para confirmar; un dispositivo puede reaparecer si su controlador sigue instalado.";
        }
        if (action == "Buscar cambios")
            return ToolHeader("Reescaneo Plug and Play") + await RunPowerShellStaticAsync("pnputil /scan-devices", cancellationToken);
        if (action == "Exportar lista")
        {
            string text = await RunGhostDeviceToolAsync("Escanear", cancellationToken);
            string export = await WriteApexReportAsync("DispositivosFantasma", text, cancellationToken);
            return ToolHeader("Lista de dispositivos fantasma exportada") + export +
                   Environment.NewLine + "→ El archivo incluye el número de entradas y sus InstanceId; úsalo antes de pulsar 'Limpiar'.";
        }
        return "Acción no reconocida.";
    }

    private async Task<string> RunStoreToolAsync(string action, string input, CancellationToken cancellationToken)
    {
        if (action == "Buscar")
        {
            string query = (input ?? "").Trim().Replace("\"", "").Replace("'", "");
            if (query.Length == 0) return "Escribe el nombre de la app a buscar en el campo de texto.";
            string output = await RunPowerShellStaticAsync($"winget search \"{query}\" --source msstore --accept-source-agreements", cancellationToken);
            return ToolHeader("Resultados en Microsoft Store", $"Búsqueda: {query}") + output.Trim() +
                   Environment.NewLine + "→ Copia el ID (9N...) del resultado y úsalo en 'Instalar por ID'.";
        }
        if (action == "Instalar por ID")
        {
            string id = (input ?? "").Trim().Replace("\"", "").Replace("'", "");
            if (id.Length == 0) return "Escribe el ID de producto de la Store (9XXXX...) y vuelve a pulsar.";
            string aviso = Regex.IsMatch(id, @"^9[A-Z0-9]{11}$", RegexOptions.IgnoreCase)
                ? "" : " El ID no tiene el formato habitual de la Store (9 + 11 caracteres); winget intentará instalarlo de todos modos.";
            string output = await RunPowerShellAsync($"winget install --id \"{id}\" --source msstore --accept-source-agreements --accept-package-agreements", cancellationToken);
            return ToolHeader("Instalación desde Microsoft Store", $"ID de producto: {id}." + aviso) + output.Trim() +
                   Environment.NewLine + "→ Si winget no está disponible, instala App Installer desde la Store.";
        }
        string upgradeScript = "winget upgrade --source msstore --accept-source-agreements --include-unknown";
        if (action == "Listar actualizaciones")
        {
            string output = await RunPowerShellStaticAsync(upgradeScript, cancellationToken);
            return ToolHeader("Actualizaciones pendientes de Microsoft Store", "Solo lectura: aplicaciones con versión más reciente disponible.") +
                   (output.Trim().Length == 0 ? "No hay actualizaciones pendientes o winget no devolvió datos." : output.Trim()) +
                   Environment.NewLine + "→ 'Actualizar apps' aplica todas las actualizaciones listadas con winget.";
        }
        if (action == "Actualizar apps")
        {
            string before = "";
            try { before = (await RunPowerShellStaticAsync(upgradeScript, cancellationToken)).Trim(); } catch { }
            string output = await RunPowerShellAsync("winget upgrade --all --source msstore --accept-source-agreements --accept-package-agreements --include-unknown", cancellationToken);
            string after = "";
            try { after = (await RunPowerShellStaticAsync(upgradeScript, cancellationToken)).Trim(); } catch { }
            return ToolHeader("Actualización de apps de Microsoft Store", "Se actualizan todas las apps listadas por winget para el origen msstore.") +
                   "Antes:\n" + (before.Length == 0 ? "sin lectura" : before) +
                   "\nDespués:\n" + (after.Length == 0 ? "sin lectura (puede requerir reiniciar la sesión)" : after) +
                   "\nDetalle:\n" + output.Trim() +
                   Environment.NewLine + "→ No hay reversión automática de versiones: para volver atrás reinstala la versión anterior desde la Store; crea un punto de restauración antes de actualizar.";
        }
        return "Acción no reconocida.";
    }

    private async Task<string> RunTrustedInstallerToolAsync(string action, string input, CancellationToken cancellationToken)
    {
        if (action == "Verificar")
        {
            string script =
                "$s = Get-Service TrustedInstaller -ErrorAction SilentlyContinue; " +
                "if ($s) { \"Servicio TrustedInstaller: $($s.Status), arranque $($s.StartType)\" } else { 'Servicio TrustedInstaller no encontrado' }; " +
                "\"Procesos TrustedInstaller.exe: \" + @(Get-Process TrustedInstaller -ErrorAction SilentlyContinue).Count; " +
                "\"Sesión con privilegios de administrador: \" + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)";
            string output = await RunPowerShellStaticAsync(script, cancellationToken);
            return ToolHeader("Consola TrustedInstaller", "Solo lectura: comprueba los requisitos antes de ejecutar comandos.") + output.Trim() +
                   Environment.NewLine + "→ 'Ejecutar comando' usa el token de TrustedInstaller; revisa dos veces la ruta antes de continuar.";
        }
        if (action != "Ejecutar comando") return "Acción no reconocida.";
        if (string.IsNullOrWhiteSpace(input)) return "Escribe un comando de consola (por ejemplo: ren \"C:\\ruta\\carpeta\" \"carpeta_nueva\") y pulsa Ejecutar.";
        string result = await TrustedInstallerRunner.RunAsync(input.Trim(), cancellationToken);
        return ToolHeader("Comando ejecutado con TrustedInstaller", $"Comando: {input.Trim()}") + result;
    }

    private async Task<string> ListPeripheralsByClassAsync(string classes, string title, string subtitle, CancellationToken cancellationToken)
    {
        string script =
            "$devices = @(Get-PnpDevice -Class " + classes + " -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VID_' }); " +
            "\"Dispositivos detectados: \" + $devices.Count; " +
            "if ($devices.Count -gt 0) { $devices | Select-Object Class, Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String }";
        string output = await RunPowerShellStaticAsync(script, cancellationToken);
        return ToolHeader(title, subtitle) +
               (output.Trim().Length == 0 ? "No se detectaron dispositivos con identificador VID." : output.Trim()) +
               Environment.NewLine + "→ La ruta de instancia muestra VID (fabricante) y PID (producto) de cada dispositivo.";
    }

    private async Task<string> RunPeripheralProfilesToolAsync(string action, CancellationToken cancellationToken)
    {
        if (action == "Solo ratones")
            return await ListPeripheralsByClassAsync("Mouse", "Inventario de ratones", "Solo lectura: dispositivos de clase Mouse presentes con VID.", cancellationToken);
        if (action == "Solo teclados")
            return await ListPeripheralsByClassAsync("Keyboard", "Inventario de teclados", "Solo lectura: dispositivos de clase Keyboard presentes con VID.", cancellationToken);
        if (action == "Escanear periféricos")
        {
            string output = await RunPowerShellStaticAsync(
                "$devices = @(Get-PnpDevice -Class HIDClass,USB,MEDIA,Mouse,Keyboard -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VID_' }); " +
                "\"Periféricos detectados: \" + $devices.Count; " +
                "if ($devices.Count -gt 0) { $devices | Select-Object Class, Status, FriendlyName, InstanceId | Format-Table -AutoSize | Out-String }",
                cancellationToken);
            return ToolHeader("Inventario de periféricos", "Solo lectura: identifica fabricante (VID) y producto (PID) por ruta de instancia.") +
                   (output.Trim().Length == 0 ? "No se detectaron periféricos con identificador VID." : output.Trim()) +
                   Environment.NewLine + "→ 'Exportar inventario' guarda este informe como texto en Mis documentos.";
        }
        if (action == "Exportar inventario")
        {
            string text = await RunPeripheralProfilesToolAsync("Escanear periféricos", cancellationToken);
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string path = Path.Combine(documents, "ApexKernel", "InventarioPerifericos-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, cancellationToken);
            return ToolHeader("Inventario exportado") + "Archivo: " + path +
                   Environment.NewLine + "→ Abre el archivo para compartir el VID/PID de cada periférico al solicitar soporte.";
        }
        return "Acción no reconocida.";
    }

    private async Task<string> RunSafeModeToolAsync(string action, CancellationToken cancellationToken)
    {
        if (action == "Verificar")
        {
            string output = await RunBcdEditAsync([], cancellationToken);
            var lines = new StringBuilder();
            lines.Append(ToolHeader("Estado del arranque", "Solo lectura: muestra el cargador de Windows y el indicador safeboot."));
            foreach (string line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("identifier", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("device", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("path", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("safeboot", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("bootmenupolicy", StringComparison.OrdinalIgnoreCase))
                    lines.AppendLine("  " + trimmed);
            }
            if (!output.Contains("safeboot", StringComparison.OrdinalIgnoreCase))
                lines.AppendLine("  safeboot: no configurado (el próximo arranque será normal).");
            lines.AppendLine("→ 'Modo Seguro' y 'Modo Seguro con Red' afectan solo al próximo arranque; 'Arranque normal' lo revierte.");
            return lines.ToString();
        }
        switch (action)
        {
            case "Modo Seguro":
                await RunBcdEditAsync(["/set", "{current}", "safeboot", "minimal"], cancellationToken);
                return ToolHeader("Modo seguro configurado") + "El próximo arranque será en Modo Seguro (mínimo)." +
                       Environment.NewLine + "→ Usa 'Verificar' para confirmarlo y 'Arranque normal' para revertirlo.";
            case "Modo Seguro con Red":
                await RunBcdEditAsync(["/set", "{current}", "safeboot", "network"], cancellationToken);
                return ToolHeader("Modo seguro con red configurado") + "El próximo arranque será en Modo Seguro con Red." +
                       Environment.NewLine + "→ Usa 'Arranque normal' para revertirlo.";
            case "Arranque normal":
                await RunBcdEditAsync(["/deletevalue", "{current}", "safeboot"], cancellationToken);
                return ToolHeader("Arranque normal restaurado") + "Se eliminó el indicador safeboot; el próximo arranque será normal." +
                       Environment.NewLine + "→ Usa 'Verificar' para confirmarlo.";
            case "Reiniciar ahora":
                return ToolHeader("Reinicio inmediato", "Se cerrarán las aplicaciones abiertas sin guardar.") +
                       await RunPowerShellStaticAsync("shutdown /r /t 0", cancellationToken);
            default:
                return "Acción no reconocida.";
        }
    }

    private async Task<string> RunInstallerFixToolAsync(string action, CancellationToken cancellationToken)
    {
        const string aclKey = @"Windows\Tools\177\TempAcl";
        if (action == "Verificar")
        {
            string acl = await RunPowerShellStaticAsync("icacls \"$env:windir\\Temp\"", cancellationToken);
            return ToolHeader("Permisos del temporal del sistema", "Solo lectura: ACL actual de %windir%\\Temp.") + acl.Trim() +
                   Environment.NewLine + "→ 'Reparar' concede control total heredable al grupo Administradores (y guarda la ACL previa).";
        }
        if (action == "Probar permisos")
        {
            string script =
                "$p = Join-Path $env:windir 'Temp'; " +
                "\"Ruta: $p\"; \"Existe: \" + (Test-Path $p); " +
                "try { \"ACL (SDDL): \" + (Get-Acl -Path $p -ErrorAction Stop).Sddl } catch { \"ACL (SDDL): no disponible - \" + $_.Exception.Message }; " +
                "$test = Join-Path $p ('ApexKernel-Test-' + [guid]::NewGuid().ToString('N') + '.tmp'); " +
                "try { [IO.File]::WriteAllText($test, 'ok'); Remove-Item -LiteralPath $test -Force -ErrorAction SilentlyContinue; " +
                "'Prueba de escritura: CORRECTA (archivo temporal creado y eliminado)' } " +
                "catch { 'Prueba de escritura: FALLIDA - ' + $_.Exception.Message }";
            string probe = await RunPowerShellStaticAsync(script, cancellationToken);
            return ToolHeader("Prueba de permisos de %windir%\\Temp", "Solo lectura funcional: crea y borra un archivo de prueba de unas pocas palabras.") +
                   probe.Trim() + Environment.NewLine +
                   "→ Si la escritura falla, usa 'Reparar'; si vuelve a fallar, comprueba antivirus o directivas de grupo.";
        }
        if (action == "Restaurar ACL previa")
        {
            if (!IsAdministrator()) return "Restaurar la ACL requiere ejecutar la aplicación como administrador.";
            string? sddl = await _backup.ReadOriginalAsync<string>(aclKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(sddl)) return "No hay una ACL previa respaldada; ejecuta 'Reparar' primero para crear el respaldo.";
            string script =
                "$p = Join-Path $env:windir 'Temp'; " +
                "$acl = New-Object System.Security.AccessControl.DirectorySecurity; " +
                "$acl.SetSecurityDescriptorSddlForm('" + EscapePsSingleQuotes(sddl) + "'); " +
                "Set-Acl -Path $p -AclObject $acl -ErrorAction Stop; " +
                "\"ACL actual: \" + (Get-Acl -Path $p).Sddl";
            string restoreOutput = await RunPowerShellAsync(script, cancellationToken);
            await _backup.RemoveOriginalAsync(aclKey, cancellationToken);
            return ToolHeader("ACL previa restaurada", "Se reaplicó la SDDL guardada antes de la reparación.") + restoreOutput.Trim() +
                   Environment.NewLine + "→ La restauración afecta a la carpeta raíz; los cambios heredados en subcarpetas pueden requerir icacls /reset /T manual.";
        }
        if (action != "Reparar") return "Acción no reconocida.";
        string before = "", after = "";
        try
        {
            string sddl = (await RunPowerShellStaticAsync("(Get-Acl -Path \"$env:windir\\Temp\").Sddl", cancellationToken)).Trim();
            if (sddl.Length > 0 && await _backup.ReadOriginalAsync<string>(aclKey, cancellationToken) is null)
                await _backup.SaveOriginalAsync(aclKey, sddl, cancellationToken);
        }
        catch { }
        try { before = (await RunPowerShellStaticAsync("icacls \"$env:windir\\Temp\"", cancellationToken)).Trim(); } catch { }
        string output = await RunPowerShellStaticAsync("icacls \"$env:windir\\Temp\" /grant \"*S-1-5-32-544:(OI)(CI)F\" /T", cancellationToken);
        try { after = (await RunPowerShellStaticAsync("icacls \"$env:windir\\Temp\"", cancellationToken)).Trim(); } catch { }
        return ToolHeader("Permisos del temporal reparados", "Grupo Administradores (S-1-5-32-544) con control total heredable.") +
               "Antes:\n" + before + "\nDespués:\n" + after + "\nDetalle: " + output.Trim() +
               Environment.NewLine + "→ Para revertir: 'Restaurar ACL previa' reaplica la SDDL guardada (la ACL de la carpeta raíz).";
    }

    private static async Task<string> ListDefenderExclusionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            string output = await RunPowerShellStaticAsync(
                "$ex = @((Get-MpPreference -ErrorAction Stop).ExclusionPath); if ($ex.Count -eq 0) { '  (ninguna)' } else { $ex | ForEach-Object { \"  $_\" } }",
                cancellationToken);
            return output.TrimEnd();
        }
        catch (Exception ex) { return "  No se pudieron leer las exclusiones: " + ex.Message; }
    }

    private async Task<string> RunDefenderToolAsync(string action, string input, CancellationToken cancellationToken)
    {
        const string smartScreenPolicy = @"SOFTWARE\Policies\Microsoft\Windows\System";
        const string edgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";
        var smartScreenChanges = new List<RegistryChange>
        {
            new() { Hive = "HKLM", KeyPath = smartScreenPolicy, ValueName = "EnableSmartScreen", ValueKind = "DWord", ApplyValue = "0", RevertValue = "1" },
            new() { Hive = "HKLM", KeyPath = edgePolicy, ValueName = "SmartScreenEnabled", ValueKind = "DWord", ApplyValue = "0", RevertValue = "1" },
            new() { Hive = "HKLM", KeyPath = smartScreenPolicy, ValueName = "ShellSmartScreenLevel", ValueKind = "String", ApplyValue = "Off", RevertValue = "Warn", DeleteOnApply = true }
        };
        switch (action)
        {
            case "Añadir exclusión":
            {
                string path = (input ?? "").Trim().Replace("\"", "");
                if (path.Length == 0) return "Escribe la ruta de la carpeta de juegos (por ejemplo D:\\SteamLibrary) y vuelve a pulsar.";
                string before = await ListDefenderExclusionsAsync(cancellationToken);
                await RunPowerShellAsync($"Add-MpPreference -ExclusionPath '{EscapePsSingleQuotes(path)}' -ErrorAction Stop", cancellationToken);
                string after = await ListDefenderExclusionsAsync(cancellationToken);
                return ToolHeader("Exclusión añadida", $"Ruta: {path}") +
                       "Antes:\n" + before + "\nDespués:\n" + after +
                       Environment.NewLine + "→ 'Quitar exclusión' elimina esta misma ruta.";
            }
            case "Quitar exclusión":
            {
                string path = (input ?? "").Trim().Replace("\"", "");
                if (path.Length == 0) return "Escribe la ruta exacta de la exclusión a quitar (déjala ver con 'Listar exclusiones').";
                string before = await ListDefenderExclusionsAsync(cancellationToken);
                if (!before.Contains(path, StringComparison.OrdinalIgnoreCase))
                    return "La ruta no aparece entre las exclusiones actuales:" + Environment.NewLine + before;
                await RunPowerShellAsync($"Remove-MpPreference -ExclusionPath '{EscapePsSingleQuotes(path)}' -ErrorAction Stop", cancellationToken);
                string after = await ListDefenderExclusionsAsync(cancellationToken);
                return ToolHeader("Exclusión eliminada", $"Ruta: {path}") +
                       "Antes:\n" + before + "\nDespués:\n" + after;
            }
            case "Listar exclusiones":
                return ToolHeader("Exclusiones de Microsoft Defender", "Solo lectura.") +
                       await ListDefenderExclusionsAsync(cancellationToken) +
                       Environment.NewLine + "→ Las rutas se eliminan con 'Quitar exclusión' indicando la ruta exacta.";
            case "Verificar SmartScreen":
            {
                using RegistryKey? policy = Registry.LocalMachine.OpenSubKey(smartScreenPolicy, writable: false);
                using RegistryKey? edge = Registry.LocalMachine.OpenSubKey(edgePolicy, writable: false);
                return ToolHeader("Estado de SmartScreen", $"Directivas: HKLM\\{smartScreenPolicy} y HKLM\\{edgePolicy}") +
                       $"EnableSmartScreen (Explorador): {ReadRegistryDisplay(policy, "EnableSmartScreen", "0 (desactivado) / 1 (activado por directiva)")}\n" +
                       $"ShellSmartScreenLevel: {ReadRegistryDisplay(policy, "ShellSmartScreenLevel", "sin nivel forzado")}\n" +
                       $"SmartScreenEnabled (Edge): {ReadRegistryDisplay(edge, "SmartScreenEnabled", "sin directiva")}\n" +
                       "→ 'Desactivar SmartScreen' escribe EnableSmartScreen=0 y SmartScreenEnabled=0; 'Restaurar SmartScreen' recupera los valores previos.";
            }
            case "Desactivar SmartScreen":
            {
                ExecutionResult result = await ApplyToolRegistryAsync(182, smartScreenChanges, cancellationToken);
                return result.Success
                    ? ToolHeader("SmartScreen desactivado", "EnableSmartScreen=0 (Explorador) y SmartScreenEnabled=0 (Edge).") + result.Message +
                      Environment.NewLine + "→ Reinicia el Explorador/Edge para aplicar el cambio; 'Verificar SmartScreen' muestra el estado."
                    : result.Message;
            }
            case "Restaurar SmartScreen":
            {
                ExecutionResult result = await RestoreToolRegistryAsync(182, smartScreenChanges, cancellationToken);
                return result.Success
                    ? ToolHeader("SmartScreen restaurado") + result.Message +
                      Environment.NewLine + "→ Comprueba el resultado con 'Verificar SmartScreen'."
                    : result.Message;
            }
            default:
                return "Acción no reconocida.";
        }
    }

    private static string RunQuantumCalculatorTool(string action, string input)
    {
        if (action != "Calcular") return "Acción no reconocida.";
        string[] parts = (input ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out int fps) || !int.TryParse(parts[1], out int hz) || fps < 30 || fps > 1000 || hz < 60 || hz > 1000)
            return "Introduce 'fps objetivo, Hz monitor' (por ejemplo: 240,240). El primer valor es fps (30-1000) y el segundo Hz (60-1000).";
        double frameTime = 1000.0 / fps;
        double refreshTime = 1000.0 / hz;
        int timerResolution = 5000; // 0.5 ms en unidades de 100 ns
        int prioritySeparation = hz >= fps * 2 ? 0x28 : 0x26;
        int lazyModeTimeout = Math.Max(2000, (int)(frameTime * 8));
        var lines = new StringBuilder();
        lines.Append(ToolHeader("Calculadora de sincronización fps/Hz", "Solo lectura: no aplica ningún ajuste al sistema."));
        lines.AppendLine($"Objetivo: {fps} fps → {frameTime:0.00} ms por fotograma.");
        lines.AppendLine($"Monitor: {hz} Hz → {refreshTime:0.00} ms por refresco.");
        lines.AppendLine($"Margen por fotograma: {refreshTime - frameTime:0.00} ms {(refreshTime >= frameTime ? "(el monitor puede mostrar cada fotograma)" : "(el monitor no puede seguir este ritmo; baja los fps o usa G-Sync/FreeSync)")}.");
        lines.AppendLine($"Fotogramas por refresco: {(frameTime > 0 ? refreshTime / frameTime : 0):0.00}.");
        lines.AppendLine();
        lines.AppendLine("Valores orientativos para afinar el sistema:");
        lines.AppendLine($"  Resolución del temporizador: 0.5 ms (NtSetTimerResolution={timerResolution} en unidades de 100 ns).");
        lines.AppendLine($"  Win32PrioritySeparation: {prioritySeparation} (0x{prioritySeparation:X}) — {(prioritySeparation == 0x28 ? "quantum corto fijo con primer plano reforzado" : "quantum corto variable")}.");
        lines.AppendLine($"  LazyModeTimeout de MMCSS: {lazyModeTimeout} ms.");
        lines.AppendLine("→ Estos son cálculos de referencia; mide con LatencyMon o el contador de fps antes y después de aplicar cambios reales.");
        return lines.ToString();
    }

    private async Task<string> RunGameInputFixToolAsync(string action, CancellationToken cancellationToken)
    {
        if (action == "Verificar")
        {
            var report = new StringBuilder();
            report.Append(ToolHeader("Servicios GameInput", "Solo lectura: revisa los servicios antes de corregir."));
            using (RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false))
            {
                if (root is null) return "No se pudo leer la clave de servicios de Windows.";
                foreach (string name in root.GetSubKeyNames()
                             .Where(n => n.StartsWith("GameInput", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    ServiceStateSnapshot state = await ReadServiceStateAsync(name, cancellationToken);
                    bool primary = string.Equals(name, "GameInputSvc", StringComparison.OrdinalIgnoreCase);
                    report.AppendLine($"• {name}: inicio={state.StartType}, estado={state.CurrentState}{(primary ? " (servicio primario; no se modifica)" : "")}");
                }
            }
            report.AppendLine("→ 'Corregir' deshabilita los duplicados y conserva GameInputSvc; los estados previos quedan en el Centro de respaldos (#207).");
            return report.ToString();
        }
        if (action == "Listar todos los servicios")
        {
            var report = new StringBuilder();
            report.Append(ToolHeader("Todos los servicios GameInput", "Solo lectura: nombre, tipo de arranque, estado y si existe respaldo."));
            int count = 0;
            using (RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false))
            {
                if (root is null) return "No se pudo leer la clave de servicios de Windows.";
                foreach (string name in root.GetSubKeyNames()
                             .Where(n => n.StartsWith("GameInput", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ServiceStateSnapshot state = await ReadServiceStateAsync(name, cancellationToken);
                    using RegistryKey? key = root.OpenSubKey(name, writable: false);
                    string display = key?.GetValue("DisplayName") as string ?? name;
                    bool hasBackup = await _backup.ReadOriginalAsync<ServiceStateSnapshot>(@"Windows\Services\GameInputFix\" + name, cancellationToken) is not null;
                    report.AppendLine($"• {name} — {display}");
                    report.AppendLine($"    Inicio: {state.StartType} · Estado: {state.CurrentState} · Respaldo: {(hasBackup ? "sí" : "no")}");
                    count++;
                }
            }
            report.AppendLine($"Total de servicios: {count}.");
            report.AppendLine("→ 'Restaurar servicios' recupera los estados guardados por 'Corregir'.");
            return report.ToString();
        }
        if (action == "Restaurar servicios")
        {
            if (!IsAdministrator()) return "Restaurar servicios requiere ejecutar la aplicación como administrador.";
            var report = new StringBuilder();
            report.Append(ToolHeader("Restauración de servicios GameInput", "Se reaplican los estados guardados por 'Corregir'."));
            int restored = 0, missing = 0;
            using (RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false))
            {
                if (root is null) return "No se pudo leer la clave de servicios de Windows.";
                foreach (string name in root.GetSubKeyNames().Where(n => n.StartsWith("GameInput", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string key = @"Windows\Services\GameInputFix\" + name;
                    ServiceStateSnapshot? original = await _backup.ReadOriginalAsync<ServiceStateSnapshot>(key, cancellationToken);
                    if (original is null) { missing++; continue; }
                    try
                    {
                        await RestoreServiceStateAsync(name, original, cancellationToken);
                        await _backup.RemoveOriginalAsync(key, cancellationToken);
                        report.AppendLine($"{name}: restaurado a inicio={original.StartType}, estado={original.CurrentState}.");
                        restored++;
                    }
                    catch (Exception ex) { report.AppendLine($"{name}: no se pudo restaurar ({ex.Message})."); }
                }
            }
            report.AppendLine($"Restaurados: {restored} · sin respaldo: {missing}.");
            report.AppendLine("→ GameInputSvc no se modifica; comprueba el resultado con 'Listar todos los servicios'.");
            return report.ToString();
        }
        if (action != "Corregir") return "Acción no reconocida.";
        var duplicates = new List<string>();
        using (RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services", writable: false))
        {
            if (root is not null)
            {
                foreach (string name in root.GetSubKeyNames())
                {
                    if (name.StartsWith("GameInput", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "GameInputSvc", StringComparison.Ordinal))
                        duplicates.Add(name);
                }
            }
        }
        if (duplicates.Count == 0) return "No se encontraron servicios GameInput duplicados; el servicio primario GameInputSvc está correcto.";
        var lines = new StringBuilder();
        foreach (string duplicate in duplicates)
        {
            try
            {
                ServiceStateSnapshot state = await ReadServiceStateAsync(duplicate, cancellationToken);
                string key = @"Windows\Services\GameInputFix\" + duplicate;
                if (await _backup.ReadOriginalAsync<ServiceStateSnapshot>(key, cancellationToken) is null)
                    await _backup.SaveOriginalAsync(key, state, cancellationToken);
                ChangeServiceStartType(duplicate, ServiceDisabled);
                ServiceStatusProcess status = QueryServiceStatusByName(duplicate);
                if (status.CurrentState == ServiceRunning) await SetServiceRunningAsync(duplicate, false, cancellationToken);
                lines.AppendLine($"{duplicate}: deshabilitado (respaldo guardado).");
            }
            catch (Exception ex)
            {
                lines.AppendLine($"{duplicate}: no se pudo deshabilitar ({ex.Message}).");
            }
        }
        lines.AppendLine("El servicio primario GameInputSvc queda intacto; para revertir, restaura el respaldo desde el Centro de respaldos (#207).");
        return lines.ToString();
    }

    private Task<string> RunRebarToolAsync(string action, string input, CancellationToken cancellationToken)
    {
        const uint RebarFeature = 0x10D5DD6Eu;
        const uint RebarOptions = 0x10D5DD6Du;
        const uint RebarSize1 = 0x10D5DD6Cu;
        const uint RebarSize2 = 0x10D5DD6Bu;
        const uint RebarMax = 0x10D5DD6Au;
        uint[] settingIds = [RebarFeature, RebarOptions, RebarSize1, RebarSize2, RebarMax];
        string? exe = string.IsNullOrWhiteSpace(input) ? null : NormalizeExeName(input);
        string? gameProfile = exe is null ? null : "ApexKernel ReBAR · " + Path.GetFileNameWithoutExtension(exe);
        if (action == "Verificar juego")
        {
            if (exe is null || gameProfile is null)
                return Task.FromResult("Escribe el .exe del juego en el campo de entrada (por ejemplo Cyberpunk2077.exe) para verificar su perfil.");
            (string error, uint value) = NvApiDrs.ReadDrsSetting(gameProfile, RebarFeature);
            var report = new StringBuilder();
            report.Append(ToolHeader("Verificación de ReBAR por juego", $"Ejecutable: {exe} · Perfil DRS: {gameProfile}"));
            report.AppendLine(error.Length > 0 ? "No se pudo leer el perfil: " + error :
                $"ReBAR forzado: {(value == 1 ? "SÍ (0x10D5DD6E = 1)" : "NO (valor leído: " + value + ")")}.");
            report.AppendLine("→ Si el perfil no existe, 'Activar' con el mismo .exe lo crea; 'Quitar del perfil del juego' elimina solo sus anulaciones.");
            return Task.FromResult(report.ToString());
        }
        if (action == "Quitar del perfil del juego")
        {
            if (exe is null || gameProfile is null)
                return Task.FromResult("Escribe el mismo .exe usado en 'Activar' para quitar sus anulaciones de ReBAR.");
            var report = new StringBuilder();
            report.Append(ToolHeader("ReBAR eliminado del perfil del juego", $"Ejecutable: {exe} · Perfil DRS: {gameProfile} · Ajustes eliminados: {settingIds.Length}."));
            foreach (uint settingId in settingIds) report.AppendLine($"0x{settingId:X8}: " + NvApiDrs.DeleteDrsSetting(gameProfile, settingId));
            report.AppendLine("→ El perfil base no se ha tocado. Para revertir, vuelve a 'Activar' con el mismo .exe.");
            return Task.FromResult(report.ToString());
        }
        if (action == "Verificar")
        {
            (string error, uint value) = NvApiDrs.ReadDrsSetting(gameProfile, RebarFeature);
            var lines = new StringBuilder();
            lines.Append(ToolHeader("Estado de ReBAR en NVIDIA DRS",
                gameProfile is null ? "Perfil base." : $"Perfil del juego: {gameProfile}."));
            if (error.Length > 0) lines.AppendLine("No se pudo leer el estado: " + error);
            else lines.AppendLine($"ReBAR forzado: {(value == 1 ? "SÍ (0x10D5DD6E = 1)" : "NO (valor leído: " + value + ")")}.");
            lines.AppendLine("→ 'Activar' crea un perfil por juego: escribe el mismo .exe en la entrada para verificarlo.");
            lines.AppendLine("→ Sin entrada se consulta el perfil base; con entrada se consulta el perfil creado para ese juego.");
            return Task.FromResult(lines.ToString());
        }
        if (action == "Activar")
        {
            if (exe is null || gameProfile is null) return Task.FromResult("Escribe el nombre del ejecutable del juego (por ejemplo Cyberpunk2077.exe) y vuelve a pulsar.");
            string result = NvApiDrs.ApplyDrsValues(gameProfile, exe,
                [(RebarFeature, 1u), (RebarOptions, 0x040001u), (RebarSize1, 0x0FFC0000u), (RebarSize2, 0x0FFC0000u), (RebarMax, 1u)]);
            return Task.FromResult(ToolHeader("ReBAR forzado para un juego", $"Ejecutable: {exe} · Perfil DRS: {gameProfile}") + result +
                Environment.NewLine + "→ Comprueba el perfil con 'Verificar' escribiendo el mismo .exe; reinicia el juego para aplicar.");
        }
        if (action == "Quitar")
        {
            var lines = new StringBuilder();
            lines.Append(ToolHeader("Quitar anulaciones de ReBAR", "Se limpia el perfil base y, si indicas un .exe, también el perfil de ese juego."));
            foreach (uint settingId in settingIds) lines.AppendLine("Base: " + NvApiDrs.DeleteDrsSetting(null, settingId));
            if (gameProfile is not null)
            {
                lines.AppendLine($"Perfil del juego ({gameProfile}):");
                foreach (uint settingId in settingIds) lines.AppendLine("  " + NvApiDrs.DeleteDrsSetting(gameProfile, settingId));
            }
            else lines.AppendLine("→ Escribe el mismo .exe usado en 'Activar' y pulsa 'Quitar' de nuevo para limpiar también su perfil.");
            lines.AppendLine("→ Si el juego sigue con ReBAR, elimina el perfil 'ApexKernel ReBAR · <juego>' desde el Panel de NVIDIA.");
            return Task.FromResult(lines.ToString().Trim());
        }
        return Task.FromResult("Acción no reconocida.");
    }

    private static string DisplayTargetBackupKey(string deviceName) => @"Windows\Tools\204\Target\" + deviceName;

    private async Task<string> SetDisplayRefreshTargetAsync(uint targetHz, CancellationToken cancellationToken)
    {
        IReadOnlyList<DisplayRefreshOption> options = await GetDisplayRefreshOptionsAsync(cancellationToken);
        if (options.Count == 0) return "No se detectaron monitores compatibles.";
        var lines = new StringBuilder();
        lines.Append(ToolHeader($"Sincronización de refresco a {targetHz} Hz",
            "Se aplica una frecuencia concreta con vista previa de Windows y respaldo del valor original; se conserva resolución y orientación estándar."));
        foreach (DisplayRefreshOption option in options)
        {
            string key = DisplayTargetBackupKey(option.DeviceName);
            if (await _backup.ReadOriginalAsync<DisplayRefreshSnapshot>(key, cancellationToken) is not null)
            {
                lines.AppendLine($"{option.DeviceName}: ya hay un cambio respaldado; usa 'Restaurar frecuencia' antes de aplicar otro.");
                continue;
            }
            if (option.MaximumHz < targetHz)
            {
                lines.AppendLine($"{option.DeviceName}: no ofrece {targetHz} Hz (máximo detectado: {option.MaximumHz} Hz); sin cambios.");
                continue;
            }
            if (option.CurrentHz == targetHz)
            {
                lines.AppendLine($"{option.DeviceName}: ya está a {targetHz} Hz; sin cambios.");
                continue;
            }
            var snapshot = new DisplayRefreshSnapshot(option.DeviceName, option.Width, option.Height, option.BitsPerPixel,
                0, option.CurrentHz, targetHz);
            await _backup.SaveOriginalAsync(key, snapshot, cancellationToken);
            DisplayRefreshResult preview = DisplayRefreshController.ApplyPreview(snapshot);
            if (!preview.Success)
            {
                await _backup.RemoveOriginalAsync(key, cancellationToken);
                lines.AppendLine($"{option.DeviceName}: {preview.Message}");
                continue;
            }
            DisplayRefreshResult confirm = DisplayRefreshController.Confirm(snapshot);
            if (!confirm.Success)
            {
                DisplayRefreshResult revert = DisplayRefreshController.Restore(snapshot, onlyIfTargetStillCurrent: true);
                await _backup.RemoveOriginalAsync(key, cancellationToken);
                lines.AppendLine($"{option.DeviceName}: {confirm.Message} {revert.Message}");
                continue;
            }
            lines.AppendLine($"{option.DeviceName}: {option.CurrentHz} → {targetHz} Hz a {option.Width}×{option.Height} · {confirm.Message}");
        }
        lines.AppendLine("→ Para volver a la frecuencia anterior usa 'Restaurar frecuencia'; si Windows no confirma la vista previa, revierte solo.");
        return lines.ToString();
    }

    private async Task<string> RestoreDisplayFrequenciesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DisplayRefreshOption> options = await GetDisplayRefreshOptionsAsync(cancellationToken);
        var lines = new StringBuilder();
        lines.Append(ToolHeader("Restauración de frecuencia de refresco", "Se recuperan los valores originales respaldados por 'Sincronizar' o por los presets."));
        int restored = 0;
        foreach (DisplayRefreshOption option in options)
        {
            if (option.HasBackup)
            {
                ExecutionResult result = await RestoreDisplayRefreshAsync(option.DeviceName, false, cancellationToken);
                lines.AppendLine($"{option.DeviceName}: {result.Message}");
                if (result.Success) restored++;
                continue;
            }
            string key = DisplayTargetBackupKey(option.DeviceName);
            DisplayRefreshSnapshot? snapshot = await _backup.ReadOriginalAsync<DisplayRefreshSnapshot>(key, cancellationToken);
            if (snapshot is null)
            {
                lines.AppendLine($"{option.DeviceName}: sin frecuencia respaldada (actual {option.CurrentHz} Hz); sin cambios.");
                continue;
            }
            DisplayRefreshResult result2 = DisplayRefreshController.Restore(snapshot, onlyIfTargetStillCurrent: false);
            lines.AppendLine($"{option.DeviceName}: {snapshot.TargetHz} → {snapshot.OriginalHz} Hz · {result2.Message}");
            if (result2.Success) { await _backup.RemoveOriginalAsync(key, cancellationToken); restored++; }
        }
        lines.AppendLine($"Monitores restaurados: {restored} de {options.Count}.");
        lines.AppendLine("→ Comprueba el resultado con 'Escanear'.");
        return lines.ToString();
    }

    private async Task<string> RunMonitorSyncToolAsync(string action, CancellationToken cancellationToken)
    {
        if (action == "Sincronizar a 60") return await SetDisplayRefreshTargetAsync(60, cancellationToken);
        if (action == "Sincronizar a 120") return await SetDisplayRefreshTargetAsync(120, cancellationToken);
        if (action == "Restaurar frecuencia") return await RestoreDisplayFrequenciesAsync(cancellationToken);
        if (action == "Escanear")
        {
            IReadOnlyList<DisplayRefreshOption> options = await GetDisplayRefreshOptionsAsync(cancellationToken);
            var lines = new StringBuilder();
            lines.Append(ToolHeader("Monitores y frecuencias disponibles", "Solo lectura: frecuencia activa y máxima compatible."));
            if (options.Count == 0) lines.AppendLine("No se detectaron monitores.");
            foreach (DisplayRefreshOption option in options)
            {
                lines.AppendLine($"• {option.DeviceName}");
                lines.AppendLine($"    Actual: {option.CurrentHz} Hz · Máximo detectado: {option.MaximumHz} Hz");
            }
            if (options.Count > 0)
            {
                int max = (int)options.Max(o => o.MaximumHz);
                lines.AppendLine($"→ Frecuencia objetivo para 'Sincronizar': {max} Hz (la mayor detectada entre todos los monitores).");
            }
            return lines.ToString();
        }
        if (action == "Sincronizar")
        {
            IReadOnlyList<DisplayRefreshOption> options = await GetDisplayRefreshOptionsAsync(cancellationToken);
            if (options.Count == 0) return "No se detectaron monitores compatibles.";
            int target = (int)options.Max(option => option.MaximumHz);
            var lines = new StringBuilder();
            lines.Append(ToolHeader("Sincronización de frecuencia de refresco", $"Objetivo: el máximo soportado por cada monitor (el mayor detectado es {target} Hz). Se usa la vista previa de Windows con opción de revertir."));
            foreach (DisplayRefreshOption option in options)
            {
                try
                {
                    ExecutionResult preview = await BeginDisplayRefreshPreviewAsync(option.DeviceName, cancellationToken);
                    if (!preview.Success) { lines.AppendLine($"{option.DeviceName}: {preview.Message}"); continue; }
                    ExecutionResult confirm = await ConfirmDisplayRefreshPreviewAsync(option.DeviceName, cancellationToken);
                    lines.AppendLine($"{option.DeviceName}: {confirm.Message}");
                }
                catch (Exception ex)
                {
                    lines.AppendLine($"{option.DeviceName}: no se pudo aplicar ({ex.Message}).");
                }
            }
            lines.AppendLine($"Sincronización terminada. Máximo global detectado: {target} Hz; cada monitor se ha ajustado a su propio máximo.");
            lines.AppendLine("→ Si la imagen parpadea o no se ve, Windows revertirá la vista previa; vuelve a 'Escanear' para comprobarlo.");
            return lines.ToString();
        }
        return "Acción no reconocida.";
    }

    private async Task<string> RunHardwareShieldToolAsync(string action, CancellationToken cancellationToken)
    {
        HardwareSnapshot snapshot = await new HardwareAndAnticheatScanner().ScanAsync(cancellationToken);
        if (action == "Verificación rápida")
        {
            string detectedAnticheats = (snapshot.HasVanguard ? "Vanguard " : "") + (snapshot.HasFaceit ? "FACEIT " : "") +
                                        (snapshot.HasEasyAntiCheat ? "EAC " : "") + (snapshot.HasBattlEye ? "BattlEye" : "");
            var checks = new StringBuilder();
            checks.Append(ToolHeader("Verificación rápida del escudo de hardware", "Solo lectura: comprobaciones con valores concretos del equipo."));
            checks.AppendLine($"{(snapshot.MemoryGb >= 16 ? "[OK]" : "[AVISO]")} Memoria: {snapshot.MemoryGb:0.#} GB {(snapshot.MemoryGb >= 16 ? "(mínimo recomendado 16 GB cubierto)" : "(por debajo de 16 GB recomendados)")}.");
            checks.AppendLine($"{(snapshot.SystemDriveType.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "[OK]" : "[AVISO]")} Unidad del sistema: {snapshot.SystemDriveType}.");
            checks.AppendLine($"{(snapshot.GraphicsAdapters.Count > 0 ? "[OK]" : "[AVISO]")} GPU detectadas: {snapshot.GraphicsAdapters.Count} {(snapshot.GraphicsAdapters.Count > 0 ? "(" + string.Join(", ", snapshot.GraphicsAdapters) + ")" : "")}.");
            checks.AppendLine($"{(detectedAnticheats.Trim().Length == 0 ? "[OK]" : "[AVISO]")} Anticheat de kernel: {(detectedAnticheats.Trim().Length == 0 ? "ninguno detectado por nombre de servicio" : detectedAnticheats.Trim())}.");
            checks.AppendLine($"{(snapshot.SupportsHardwareGpuScheduling == true ? "[OK]" : "[AVISO]")} Programación de GPU por hardware: {(snapshot.SupportsHardwareGpuScheduling switch { true => "compatible", false => "no compatible", null => "desconocida" })} · activada: {(snapshot.IsHardwareGpuSchedulingEnabled switch { true => "sí", false => "no", null => "desconocido" })}.");
            checks.AppendLine($"[INFO] CPU: {snapshot.CpuName} · {snapshot.PhysicalCores} núcleos físicos / {snapshot.LogicalProcessors} lógicos · {snapshot.WindowsVersion}.");
            checks.AppendLine($"[INFO] Árbol USB/HID: {snapshot.UsbTopology.Count} entrada(s); batería: {(snapshot.HasBattery ? "sí" : "no")}.");
            checks.AppendLine("→ [AVISO] no implica un fallo: indica ajustes a revisar antes de aplicar tweaks agresivos de kernel.");
            return checks.ToString();
        }
        if (action is not ("Escanear" or "Exportar informe")) return "Acción no reconocida.";
        var lines = new StringBuilder();
        lines.Append(ToolHeader("Escudo de hardware", "Solo lectura: identifica limitaciones y anticheats de kernel."));
        lines.AppendLine("CPU: " + snapshot.CpuName);
        lines.AppendLine($"Núcleos: {snapshot.PhysicalCores} físicos / {snapshot.LogicalProcessors} lógicos · RAM: {snapshot.MemoryGb:0.#} GB · Unidad del sistema: {snapshot.SystemDriveType}");
        lines.AppendLine("GPU: " + (snapshot.GraphicsAdapters.Count == 0 ? "no detectada" : string.Join(", ", snapshot.GraphicsAdapters)));
        lines.AppendLine("Sistema: " + snapshot.WindowsVersion);
        string anticheats = (snapshot.HasVanguard ? "Vanguard " : "") + (snapshot.HasFaceit ? "FACEIT " : "") + (snapshot.HasEasyAntiCheat ? "EAC " : "") + (snapshot.HasBattlEye ? "BattlEye" : "");
        lines.AppendLine("Anticheat por servicio: " + (anticheats.Trim().Length == 0 ? "ninguno detectado" : anticheats.Trim()));
        lines.AppendLine(snapshot.HasVanguard
            ? "Escudo: Vanguard detectado; evita tweaks agresivos de kernel (debug, firma de drivers) mientras juegues Valorant."
            : "Escudo: sin anticheats de kernel detectados por nombre de servicio; los ajustes de la app son compatibles.");
        lines.AppendLine("→ Árbol USB/HID: " + snapshot.UsbTopology.Count + " entrada(s); la herramienta #15 lo detalla.");
        if (action == "Exportar informe")
        {
            string export = await WriteApexReportAsync("EscudoHardware", lines.ToString(), cancellationToken);
            return ToolHeader("Informe del escudo de hardware exportado", $"GPU: {snapshot.GraphicsAdapters.Count} · USB: {snapshot.UsbTopology.Count} entradas.") + export +
                   Environment.NewLine + "→ 'Verificación rápida' muestra solo las comprobaciones con estado [OK]/[AVISO].";
        }
        return lines.ToString();
    }
}

/// <summary>
/// Ejecuta comandos de consola con el token del servicio TrustedInstaller (usado por #139/#140 y la herramienta #151).
/// </summary>
public static class TrustedInstallerRunner
{
    private const uint ServiceStart = 0x0010;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunningState = 4;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAssignPrimary = 0x0001;
    private const int SecurityImpersonation = 2;
    // CreateProcessWithTokenW exige un token PRIMARIO (TokenPrimary=1); un token de
    // impersonación (2) hace que la creación de proceso falle siempre.
    private const int TokenPrimary = 1;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, uint count, IntPtr arguments);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", EntryPoint = "DuplicateTokenEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreatePipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr readHandle, out IntPtr writeHandle, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetHandleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint Width;
        public uint Height;
        public uint CountCharsX;
        public uint CountCharsY;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public IntPtr Reserved3;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessWithTokenW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string application, string commandLine,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public static async Task<string> RunAsync(string commandLine, CancellationToken cancellationToken = default)
    {
        if (!IsElevated()) return "Ejecutar como TrustedInstaller requiere una sesión de administrador.";
        string serviceError = EnsureTrustedInstallerRunning();
        if (serviceError.Length > 0) return serviceError;
        Process? trustedInstaller = Process.GetProcessesByName("TrustedInstaller").FirstOrDefault();
        if (trustedInstaller is null) return "El proceso TrustedInstaller.exe no está en ejecución; reintenta en unos segundos.";
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)trustedInstaller.Id);
        if (process == IntPtr.Zero) return "No se pudo abrir el proceso TrustedInstaller.exe (PID " + trustedInstaller.Id + ", error " + Marshal.GetLastWin32Error() + ").";
        try
        {
            if (!OpenProcessToken(process, TokenDuplicate | TokenQuery, out IntPtr token))
                return "No se pudo abrir el token de TrustedInstaller (error " + Marshal.GetLastWin32Error() + ").";
            try
            {
                if (!DuplicateTokenEx(token, TokenAssignPrimary | TokenDuplicate | TokenQuery, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr duplicate))
                    return "No se pudo duplicar el token de TrustedInstaller (error " + Marshal.GetLastWin32Error() + ").";
                try
                {
                    var pipeSecurity = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
                    if (!CreatePipe(out IntPtr readOutput, out IntPtr writeOutput, ref pipeSecurity, 0))
                        return "No se pudo crear la tubería de salida (error " + Marshal.GetLastWin32Error() + ").";
                    try
                    {
                        SetHandleInformation(readOutput, HandleFlagInherit, 0);
                        string fullCommandLine = "/c " + commandLine;
                        var startup = new StartupInfo
                        {
                            Size = (uint)Marshal.SizeOf<StartupInfo>(),
                            Flags = StartfUseStdHandles,
                            StdInput = IntPtr.Zero,
                            StdOutput = writeOutput,
                            StdError = writeOutput
                        };
                        if (!CreateProcessWithTokenW(duplicate, 0, Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                            fullCommandLine, CreateNoWindow, IntPtr.Zero, null, ref startup, out ProcessInformation info))
                            return $"No se pudo crear el proceso con el token TrustedInstaller (error {Marshal.GetLastWin32Error()}).";
                        CloseHandle(writeOutput);
                        writeOutput = IntPtr.Zero;
                        CloseHandle(info.Thread);
                        string output;
                        using (var reader = new StreamReader(new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(readOutput, ownsHandle: false), FileAccess.Read, bufferSize: 4096, isAsync: false), Encoding.GetEncoding(0)))
                        {
                            output = await reader.ReadToEndAsync(cancellationToken);
                        }
                        CloseHandle(info.Process);
                        return output.Trim().Length == 0 ? "Comando ejecutado con el token TrustedInstaller (sin salida)." : output.Trim();
                    }
                    finally
                    {
                        CloseHandle(readOutput);
                        if (writeOutput != IntPtr.Zero) CloseHandle(writeOutput);
                    }
                }
                finally { CloseHandle(duplicate); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    private static string EnsureTrustedInstallerRunning()
    {
        IntPtr manager = OpenSCManagerW(null, null, 0x0001 | ServiceStart);
        if (manager == IntPtr.Zero) return "No se pudo abrir el administrador de servicios.";
        try
        {
            IntPtr service = OpenServiceW(manager, "TrustedInstaller", ServiceStart | ServiceQueryStatus);
            if (service == IntPtr.Zero) return "No se pudo abrir el servicio TrustedInstaller.";
            try
            {
                QueryServiceStatus(service, out ServiceStatus status);
                if (status.CurrentState != ServiceRunningState && !StartServiceW(service, 0, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is not (1053 or 1056)) return $"No se pudo iniciar TrustedInstaller (error {error}).";
                }
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(250);
                    QueryServiceStatus(service, out status);
                    if (status.CurrentState == ServiceRunningState) return "";
                }
                return "TrustedInstaller no terminó de arrancar; reintenta en unos segundos.";
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
