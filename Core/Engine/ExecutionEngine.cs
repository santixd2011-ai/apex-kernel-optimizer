using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Security.Principal;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ApexKernelOptimizer.Core.Models;

namespace ApexKernelOptimizer.Core.Engine;

public sealed record ExecutionResult(bool Success, string Message);
public sealed record RegistryValueSnapshot(bool Exists, string Kind, string? SerializedData);
public sealed record ServiceStateSnapshot(uint StartType, uint CurrentState);
public sealed record PowerSettingSnapshot(Guid Scheme, uint AcValue, uint DcValue);
public sealed record PowerIndexSnapshot(Guid Scheme, Guid Subgroup, Guid Setting, uint AcValue, uint DcValue);
public sealed record CpuPowerSnapshot(Guid Scheme, uint MinimumAc, uint MinimumDc, uint MaximumAc, uint MaximumDc);
public sealed record DiskSleepSnapshot(Guid Scheme, uint DiskAc, uint DiskDc, uint HybridAc, uint HybridDc);
public sealed record DevicePowerValueSnapshot(string Name, RegistryValueSnapshot Original);
public sealed record DevicePowerKeySnapshot(string RegistryPath, List<DevicePowerValueSnapshot> Values);
public sealed record DevicePowerSnapshot(List<DevicePowerKeySnapshot> Keys);
public sealed record BcdOptionSnapshot(bool Exists, string? Value);
public sealed record GpuPreferenceEntry(string ExePath, string Value, bool HasBackup);
public sealed record GameCompatibilityEntry(string ExePath, string Value, bool HasBackup);
public sealed record ShaderCacheFileEntry(string OriginalPath, string BackupPath, long Length);
public sealed record ShaderCacheSnapshot(string BackupRoot, List<ShaderCacheFileEntry> Files);
public sealed record ShaderCacheStatus(int BackupFileCount, long BackupBytes, string? BackupRoot);
public sealed record GamePriorityStatus(int OptimizationId, string ValueName, string DesiredValue, string? CurrentValue, bool HasBackup);

public sealed partial class ExecutionEngine
{
    private readonly StateBackupManager _backup;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _shaderCacheGate = new(1, 1);
    private readonly SemaphoreSlim _displayGate = new(1, 1);
    private readonly SemaphoreSlim _gamePriorityGate = new(1, 1);

    public ExecutionEngine(StateBackupManager? backup = null) => _backup = backup ?? new StateBackupManager();

    public async Task<IReadOnlyList<GamePriorityStatus>> GetGamePriorityStatusAsync(string exePath, CancellationToken cancellationToken = default)
    {
        if (!TryGetIfEOExecutableName(exePath, out string exeName, out string error)) throw new ArgumentException(error, nameof(exePath));
        var results = new List<GamePriorityStatus>(3);
        foreach ((int id, string valueName, string desired) in GamePriorityOptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string keyPath = GamePriorityKeyPath(exeName);
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
            string? current = key?.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase) == true
                ? SerializeCurrentValue(key!.GetValue(valueName), key.GetValueKind(valueName)) : null;
            bool backup = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(GamePriorityBackupKey(exeName, valueName), cancellationToken) is not null;
            results.Add(new(id, valueName, desired, current, backup));
        }
        return results;
    }

    public async Task<ExecutionResult> SetGamePriorityAsync(string exePath, int optimizationId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!TryGetIfEOExecutableName(exePath, out string exeName, out string error)) return new(false, error);
        (int _, string valueName, string desired) option = GamePriorityOptions.FirstOrDefault(entry => entry.Id == optimizationId);
        if (option.valueName is null) return new(false, "El ajuste de prioridad seleccionado no es compatible.");
        if (!IsAdministrator()) return new(false, "Las prioridades persistentes IFEO requieren ejecutar ApexKernel como administrador.");
        string keyPath = GamePriorityKeyPath(exeName);
        string backupKey = GamePriorityBackupKey(exeName, option.valueName);
        var change = new RegistryChange { Hive = "HKLM", KeyPath = keyPath, ValueName = option.valueName, ValueKind = "DWord", ApplyValue = option.desired, RevertValue = "0" };
        await _gamePriorityGate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                RegistryValueSnapshot? existingBackup = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, cancellationToken);
                if (existingBackup is not null) return new(false, "Ya existe un respaldo pendiente para este juego y prioridad; restáuralo primero.");
                RegistryValueSnapshot original = ReadSnapshot(change);
                await _backup.SaveOriginalAsync(backupKey, original, cancellationToken);
                try
                {
                    using RegistryKey target = Registry.LocalMachine.CreateSubKey(keyPath, writable: true) ?? throw new UnauthorizedAccessException($"No se pudo abrir HKLM\\{keyPath}.");
                    target.SetValue(option.valueName, ParseDWordValue(option.desired), RegistryValueKind.DWord);
                    if (SerializeCurrentValue(target.GetValue(option.valueName), target.GetValueKind(option.valueName)) != option.desired)
                        throw new InvalidOperationException("Windows no confirmó el valor IFEO solicitado.");
                }
                catch
                {
                    try { RestoreValue(change, original); await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None); }
                    catch { /* conserva el respaldo para recuperarlo desde el índice */ }
                    throw;
                }
                return new(true, $"{option.valueName}={option.desired} se aplicó a todos los procesos llamados {exeName}; el estado anterior quedó respaldado.");
            }

            RegistryValueSnapshot? saved = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, cancellationToken);
            if (saved is null) return new(false, "No existe respaldo de ApexKernel para este valor; no se modificó el registro.");
            using (RegistryKey? currentKey = Registry.LocalMachine.OpenSubKey(keyPath, writable: false))
            {
                if (currentKey?.GetValueNames().Contains(option.valueName, StringComparer.OrdinalIgnoreCase) != true ||
                    SerializeCurrentValue(currentKey.GetValue(option.valueName), currentKey.GetValueKind(option.valueName)) != option.desired)
                    return new(false, "El valor cambió después de aplicarlo o ya no existe. Se conserva el respaldo y no se sobrescribió el cambio externo.");
            }
            RestoreValue(change, saved);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, $"Se restauró el estado original de {option.valueName} para {exeName}.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException or JsonException or System.Security.SecurityException)
        {
            return new(false, $"No se pudo cambiar la prioridad del juego: {ex.Message}");
        }
        finally { _gamePriorityGate.Release(); }
    }

    private static readonly (int Id, string ValueName, string DesiredValue)[] GamePriorityOptions =
    [ (18, "CpuPriorityClass", "3"), (19, "IoPriority", "3"), (20, "PagePriority", "5") ];

    private static bool TryGetIfEOExecutableName(string exePath, out string exeName, out string error)
    {
        exeName = "";
        error = "";
        if (string.IsNullOrWhiteSpace(exePath)) { error = "Selecciona un archivo ejecutable .exe existente."; return false; }
        try
        {
            string fullPath = Path.GetFullPath(exePath.Trim());
            if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            { error = "Selecciona un archivo .exe existente."; return false; }
            exeName = Path.GetFileName(fullPath);
            return !string.IsNullOrWhiteSpace(exeName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { error = $"La ruta del ejecutable no es válida: {ex.Message}"; return false; }
    }

    private static string GamePriorityKeyPath(string exeName) => $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{exeName}\PerfOptions";
    private static string GamePriorityBackupKey(string exeName, string valueName) => $@"Windows\IFEO\{exeName}\PerfOptions\{valueName}";

    public Task<string> GetBackupIndexSummaryAsync(CancellationToken cancellationToken = default) => _backup.GetIndexSummaryAsync(cancellationToken);

    public async Task<IReadOnlyList<GpuPreferenceEntry>> GetGpuPreferenceEntriesAsync(CancellationToken cancellationToken = default)
    {
        const string keyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is null) return [];
        var entries = new List<GpuPreferenceEntry>();
        foreach (string exePath in key.GetValueNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.GetValue(exePath) is not string value || !value.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(token => token.TrimStart().StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))) continue;
            bool backup = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(GpuPreferenceBackupKey(exePath), cancellationToken) is not null;
            entries.Add(new(exePath, value, backup));
        }
        return entries.OrderBy(entry => entry.ExePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ExecutionResult> SetGpuPreferenceAsync(string exePath, bool highPerformance, CancellationToken cancellationToken = default)
    {
        const string keyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        if (string.IsNullOrWhiteSpace(exePath)) return new(false, "Selecciona un archivo ejecutable .exe.");
        string fullPath;
        try { fullPath = Path.GetFullPath(exePath.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new(false, $"La ruta del ejecutable no es válida: {ex.Message}"); }
        if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return new(false, "Selecciona un archivo .exe existente.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var change = new RegistryChange { Hive = "HKCU", KeyPath = keyPath, ValueName = fullPath, ValueKind = "String", ApplyValue = "", RevertValue = "" };
            string backupKey = GpuPreferenceBackupKey(fullPath);
            if (highPerformance)
            {
                using RegistryKey? existingKey = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
                bool exists = existingKey?.GetValueNames().Contains(fullPath, StringComparer.OrdinalIgnoreCase) == true;
                RegistryValueKind kind = exists ? existingKey!.GetValueKind(fullPath) : RegistryValueKind.String;
                if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                    return new(false, "El valor de este ejecutable no es texto compatible; no se modificó.");
                RegistryValueSnapshot? oldBackup = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, cancellationToken);
                if (oldBackup is null) await _backup.SaveOriginalAsync(backupKey, ReadSnapshot(change), cancellationToken);
                try
                {
                    string current = exists ? (existingKey!.GetValue(fullPath, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "") : "";
                    string updated = string.Join(';', current.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(token => !token.TrimStart().StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase)).Append("GpuPreference=2")) + ";";
                    using RegistryKey target = Registry.CurrentUser.CreateSubKey(keyPath, writable: true) ?? throw new UnauthorizedAccessException("No se pudo abrir la preferencia de GPU del usuario.");
                    target.SetValue(fullPath, updated, kind);
                }
                catch
                {
                    try
                    {
                        RegistryValueSnapshot? original = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, CancellationToken.None);
                        if (original is not null) RestoreValue(change, original);
                        await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    }
                    catch { }
                    throw;
                }
                return new(true, $"Se asignó preferencia de alto rendimiento para {Path.GetFileName(fullPath)}. La GPU elegida depende de Windows y de los adaptadores disponibles.");
            }

            RegistryValueSnapshot? snapshot = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, cancellationToken);
            if (snapshot is null) return new(false, "ApexKernel no tiene un respaldo para este ejecutable; no se modificó el valor.");
            RestoreValue(change, snapshot);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, $"Se restauró el valor original de {Path.GetFileName(fullPath)}.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException or JsonException or System.Security.SecurityException)
        {
            return new(false, $"No se pudo cambiar la preferencia de GPU: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static string GpuPreferenceBackupKey(string exePath) => @"Windows\GPU\UserGpuPreferences\" + exePath;

    public async Task<IReadOnlyList<GameCompatibilityEntry>> GetGameCompatibilityEntriesAsync(CancellationToken cancellationToken = default)
    {
        const string keyPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (key is null) return [];
        var entries = new List<GameCompatibilityEntry>();
        foreach (string exePath in key.GetValueNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.GetValue(exePath) is not string value || !HasGameCompatibilityFlags(value)) continue;
            bool backup = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(GameCompatibilityBackupKey(exePath), cancellationToken) is not null;
            entries.Add(new(exePath, value, backup));
        }
        return entries.OrderBy(entry => entry.ExePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ExecutionResult> SetGameCompatibilityAsync(string exePath, bool enabled, CancellationToken cancellationToken = default)
    {
        const string keyPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        if (string.IsNullOrWhiteSpace(exePath)) return new(false, "Selecciona el ejecutable real del juego (.exe), no un acceso directo.");
        string fullPath;
        try { fullPath = Path.GetFullPath(exePath.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new(false, $"La ruta no es válida: {ex.Message}"); }
        if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return new(false, "Selecciona un archivo .exe existente.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var change = new RegistryChange { Hive = "HKCU", KeyPath = keyPath, ValueName = fullPath, ValueKind = "String", ApplyValue = "", RevertValue = "" };
            string backupKey = GameCompatibilityBackupKey(fullPath);
            if (enabled)
            {
                using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
                if (existing?.GetValueNames().Contains(fullPath, StringComparer.OrdinalIgnoreCase) == true && existing.GetValueKind(fullPath) is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                    return new(false, "El valor de compatibilidad existente no es texto; no se modificó.");
                string current = existing?.GetValue(fullPath, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                RegistryValueSnapshot? original = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, cancellationToken);
                if (original is null) await _backup.SaveOriginalAsync(backupKey, ReadSnapshot(change), cancellationToken);
                string[] retained = current.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Where(token => token != "~" && !token.Equals("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase) && !token.Equals("HIGHDPIAWARE", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                string updated = string.Join(' ', new[] { "~" }.Concat(retained).Append("DISABLEDXMAXIMIZEDWINDOWEDMODE").Append("HIGHDPIAWARE"));
                try
                {
                    using RegistryKey target = Registry.CurrentUser.CreateSubKey(keyPath, writable: true) ?? throw new UnauthorizedAccessException("No se pudo abrir la clave AppCompatFlags\\Layers.");
                    target.SetValue(fullPath, updated, RegistryValueKind.String);
                }
                catch
                {
                    try
                    {
                        RegistryValueSnapshot? saved = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, CancellationToken.None);
                        if (saved is not null) RestoreValue(change, saved);
                        await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    }
                    catch { }
                    throw;
                }
                return new(true, $"Se aplicaron las compatibilidades FSO y DPI a {Path.GetFileName(fullPath)}. Comprueba escala e interacción en ese juego; se conserva su configuración previa para revertir.");
            }

            RegistryValueSnapshot? snapshot = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, cancellationToken);
            if (snapshot is null) return new(false, "ApexKernel no tiene un respaldo para ese ejecutable; no se modificó el valor.");
            RestoreValue(change, snapshot);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, $"Se restauró el valor AppCompat original de {Path.GetFileName(fullPath)}.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException or JsonException or System.Security.SecurityException)
        {
            return new(false, $"No se pudo cambiar la compatibilidad del juego: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static bool HasGameCompatibilityFlags(string value) =>
        value.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase) || value.Contains("HIGHDPIAWARE", StringComparison.OrdinalIgnoreCase);

    private static string GameCompatibilityBackupKey(string exePath) => @"Windows\AppCompat\GameLayers\" + exePath;

    public async Task<IReadOnlyList<DisplayRefreshOption>> GetDisplayRefreshOptionsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DisplayRefreshOption> options = DisplayRefreshController.Enumerate();
        var updated = new List<DisplayRefreshOption>(options.Count);
        foreach (DisplayRefreshOption option in options)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasBackup = await _backup.ReadOriginalAsync<DisplayRefreshSnapshot>(DisplayRefreshBackupKey(option.DeviceName), cancellationToken) is not null;
            updated.Add(option with { HasBackup = hasBackup });
        }
        return updated;
    }

    public async Task<ExecutionResult> BeginDisplayRefreshPreviewAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        await _displayGate.WaitAsync(cancellationToken);
        try
        {
            string backupKey = DisplayRefreshBackupKey(deviceName);
            if (await _backup.ReadOriginalAsync<DisplayRefreshSnapshot>(backupKey, cancellationToken) is not null)
                return new(false, "Este monitor ya tiene un valor de refresco respaldado; restáuralo antes de aplicar otro.");
            DisplayRefreshResult plan = DisplayRefreshController.PlanMaximumRefresh(deviceName);
            if (!plan.Success || plan.Snapshot is null) return new(false, plan.Message);
            await _backup.SaveOriginalAsync(backupKey, plan.Snapshot, cancellationToken);
            DisplayRefreshResult preview = DisplayRefreshController.ApplyPreview(plan.Snapshot);
            if (!preview.Success) await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
            return new(preview.Success, preview.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return new(false, $"No se pudo preparar el modo de refresco: {ex.Message}");
        }
        finally { _displayGate.Release(); }
    }

    public async Task<ExecutionResult> ConfirmDisplayRefreshPreviewAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        await _displayGate.WaitAsync(cancellationToken);
        try
        {
            DisplayRefreshSnapshot? snapshot = await _backup.ReadOriginalAsync<DisplayRefreshSnapshot>(DisplayRefreshBackupKey(deviceName), cancellationToken);
            if (snapshot is null) return new(false, "No existe una vista previa respaldada para este monitor.");
            DisplayRefreshResult result = DisplayRefreshController.Confirm(snapshot);
            if (!result.Success)
            {
                DisplayRefreshResult revert = DisplayRefreshController.Restore(snapshot, onlyIfTargetStillCurrent: true);
                if (revert.Success) await _backup.RemoveOriginalAsync(DisplayRefreshBackupKey(deviceName), cancellationToken);
                return new(false, result.Message + (revert.Success ? " Se restauró el modo original." : " " + revert.Message));
            }
            return new(true, result.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return new(false, $"No se pudo guardar el modo de pantalla: {ex.Message}");
        }
        finally { _displayGate.Release(); }
    }

    public async Task<ExecutionResult> RestoreDisplayRefreshAsync(string deviceName, bool onlyIfTargetStillCurrent = false, CancellationToken cancellationToken = default)
    {
        await _displayGate.WaitAsync(cancellationToken);
        try
        {
            string backupKey = DisplayRefreshBackupKey(deviceName);
            DisplayRefreshSnapshot? snapshot = await _backup.ReadOriginalAsync<DisplayRefreshSnapshot>(backupKey, cancellationToken);
            if (snapshot is null) return new(false, "No existe un valor original respaldado para este monitor.");
            DisplayRefreshResult result = DisplayRefreshController.Restore(snapshot, onlyIfTargetStillCurrent);
            if (result.Success) await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(result.Success, result.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return new(false, $"No se pudo restaurar el modo original: {ex.Message}");
        }
        finally { _displayGate.Release(); }
    }

    private static string DisplayRefreshBackupKey(string deviceName) => @"Windows\Display\Refresh\" + deviceName;

    public async Task<ShaderCacheStatus> GetShaderCacheStatusAsync(CancellationToken cancellationToken = default)
    {
        ShaderCacheSnapshot? snapshot = await _backup.ReadOriginalAsync<ShaderCacheSnapshot>(@"Windows\GPU\ShaderCache", cancellationToken);
        if (snapshot is null) return new(0, 0, null);
        int count = snapshot.Files.Count(file => File.Exists(file.BackupPath));
        long bytes = snapshot.Files.Where(file => File.Exists(file.BackupPath)).Sum(file => { try { return new FileInfo(file.BackupPath).Length; } catch { return 0L; } });
        return new(count, bytes, snapshot.BackupRoot);
    }

    public async Task<ExecutionResult> QuarantineShaderCachesAsync(CancellationToken cancellationToken = default)
    {
        await _shaderCacheGate.WaitAsync(cancellationToken);
        try { return await QuarantineShaderCachesCoreAsync(cancellationToken); }
        finally { _shaderCacheGate.Release(); }
    }

    private async Task<ExecutionResult> QuarantineShaderCachesCoreAsync(CancellationToken cancellationToken)
    {
        const string stateKey = @"Windows\GPU\ShaderCache";
        if (await _backup.ReadOriginalAsync<ShaderCacheSnapshot>(stateKey, cancellationToken) is not null)
            return new(false, "Ya hay una copia de caché pendiente de restaurar. Restáurala antes de iniciar otra limpieza.");
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) return new(false, "Windows no devolvió la carpeta LocalAppData.");
        string backupRoot = Path.Combine(localAppData, "ApexKernel", "ShaderCacheBackup", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        string[] cacheRoots = [Path.Combine(localAppData, "D3DSCache"), Path.Combine(localAppData, "NVIDIA", "DXCache"), Path.Combine(localAppData, "NVIDIA", "GLCache")];
        var files = new List<ShaderCacheFileEntry>();
        foreach (string root in cacheRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            try { if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, $"No se pudo comprobar {root}: {ex.Message}. No se movió ningún archivo."); }
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string file in Directory.EnumerateFiles(root, "*", options))
                {
                    string fullPath = Path.GetFullPath(file);
                    string normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                    if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = Path.GetRelativePath(localAppData, fullPath);
                    if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                    string backupPath = Path.Combine(backupRoot, relative);
                    long length;
                    try { length = new FileInfo(fullPath).Length; } catch { length = 0; }
                    files.Add(new(fullPath, backupPath, length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(false, $"No se pudo inspeccionar {root}: {ex.Message}. No se ha movido ningún archivo.");
            }
        }
        if (files.Count == 0) return new(false, "No se encontraron archivos en D3DSCache, NVIDIA\\DXCache o NVIDIA\\GLCache.");
        var snapshot = new ShaderCacheSnapshot(backupRoot, files);
        await _backup.SaveOriginalAsync(stateKey, snapshot, cancellationToken);
        int moved = 0, skipped = 0;
        foreach (ShaderCacheFileEntry file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(file.OriginalPath)) { skipped++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(file.BackupPath)!);
                File.Move(file.OriginalPath, file.BackupPath);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; }
        }
        if (moved == 0)
        {
            await _backup.RemoveOriginalAsync(stateKey, CancellationToken.None);
            try { if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, recursive: true); } catch { }
            return new(false, $"No se pudo mover ningún archivo; {skipped} quedaron intactos (posiblemente estaban en uso o protegidos).");
        }
        return new(true, $"Se movieron {moved} archivos de caché al respaldo recuperable. {skipped} archivos quedaron en su sitio (en uso o inaccesibles). Los shaders se volverán a generar al ejecutar las aplicaciones.");
    }

    public async Task<ExecutionResult> RestoreShaderCachesAsync(CancellationToken cancellationToken = default)
    {
        await _shaderCacheGate.WaitAsync(cancellationToken);
        try { return await RestoreShaderCachesCoreAsync(cancellationToken); }
        finally { _shaderCacheGate.Release(); }
    }

    private async Task<ExecutionResult> RestoreShaderCachesCoreAsync(CancellationToken cancellationToken)
    {
        const string stateKey = @"Windows\GPU\ShaderCache";
        ShaderCacheSnapshot? snapshot = await _backup.ReadOriginalAsync<ShaderCacheSnapshot>(stateKey, cancellationToken);
        if (snapshot is null) return new(false, "No hay una copia de caché de shaders de ApexKernel para restaurar.");
        string expectedBackupRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexKernel", "ShaderCacheBackup")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(snapshot.BackupRoot).StartsWith(expectedBackupRoot, StringComparison.OrdinalIgnoreCase)) return new(false, "La ubicación del respaldo no coincide con la carpeta segura de ApexKernel; no se movieron archivos.");
        string localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) + Path.DirectorySeparatorChar;
        var roots = new[] { "D3DSCache", Path.Combine("NVIDIA", "DXCache"), Path.Combine("NVIDIA", "GLCache") }
            .Select(folder => Path.GetFullPath(Path.Combine(localAppData, folder)) + Path.DirectorySeparatorChar).ToArray();
        int restored = 0, conflicts = 0, missing = 0;
        foreach (ShaderCacheFileEntry file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string original = Path.GetFullPath(file.OriginalPath);
            string backup = Path.GetFullPath(file.BackupPath);
            if (!roots.Any(root => original.StartsWith(root, StringComparison.OrdinalIgnoreCase)) || !backup.StartsWith(expectedBackupRoot, StringComparison.OrdinalIgnoreCase))
                return new(false, "El respaldo contiene una ruta fuera de las carpetas de caché permitidas; no se restauró el resto.");
            if (!File.Exists(backup))
            {
                if (File.Exists(original)) continue;
                missing++;
                continue;
            }
            if (File.Exists(original)) { conflicts++; continue; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.Move(backup, original);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { conflicts++; }
        }
        int remaining = snapshot.Files.Count(file => File.Exists(file.BackupPath));
        if (remaining == 0)
        {
            await _backup.RemoveOriginalAsync(stateKey, cancellationToken);
            try { if (Directory.Exists(snapshot.BackupRoot)) Directory.Delete(snapshot.BackupRoot, recursive: true); } catch { }
            return new(true, $"Restaurados {restored} archivos. {conflicts} ya existían en la caché y se conservaron; {missing} ya no están en el respaldo.");
        }
        return new(false, $"Se restauraron {restored} archivos; quedan {remaining} en el respaldo porque hay conflictos o archivos bloqueados. Cierra las aplicaciones y vuelve a intentarlo.");
    }

    public async Task<bool> IsAppliedAsync(OptimizationItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool? extended = await TryAppliedExtendedAsync(item, cancellationToken);
        if (extended is not null) return extended.Value;
        if (item.Id == 193 && IsAllowed(item)) return !await ReadMemoryCompressionAsync(cancellationToken);
        if (item.Id == 13 && IsAllowed(item))
        {
            PowerSettingSnapshot power = ReadUsbSelectiveSuspend();
            return power.AcValue == 0 && power.DcValue == 0;
        }
        if (item.Id == 47 && IsAllowed(item))
        {
            PowerIndexSnapshot power = ReadPcieAspm();
            return power.AcValue == 0 && power.DcValue == 0;
        }
        if (item.Id == 44 && IsAllowed(item))
        {
            CpuPowerSnapshot power = ReadCpuPowerPlan();
            return power.MinimumAc == 100 && power.MaximumAc == 100;
        }
        if (item.Id == 48 && IsAllowed(item))
        {
            DiskSleepSnapshot power = ReadDiskSleepPlan();
            return power.DiskAc == 0 && power.DiskDc == 0 && power.HybridAc == 0 && power.HybridDc == 0;
        }
        if (item.Id == 158 && IsAllowed(item))
        {
            PowerIndexSnapshot power = ReadStandbyIdleTimeout();
            return power.AcValue == 0 && power.DcValue == 0;
        }
        if (item.Id == 118 && IsAllowed(item) && await ReadPageCombiningAsync(cancellationToken)) return false;
        if (item.Id == 107 && IsAllowed(item))
        {
            ServiceStateSnapshot service = await ReadWindowsSearchStateAsync(cancellationToken);
            return service.StartType == ServiceDisabled && service.CurrentState == ServiceStopped;
        }
        if (item.Id == 163 && IsAllowed(item)) return await IsBiometricsDisabledAsync(item, cancellationToken);
        if (item.Id == 17 && IsAllowed(item)) return IsPrioritySeparationActive(item);
        if (item.Id == 14 && IsAllowed(item)) return await IsUsbHidPowerManagementDisabledAsync(cancellationToken);
        if (item.Id == 32 && IsAllowed(item)) return string.Equals(await ReadDynamicTickOptionAsync(cancellationToken), "yes", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 33 && IsAllowed(item))
            return await _backup.ReadOriginalAsync<BcdOptionSnapshot>(@"Windows\BCD\useplatformclock", cancellationToken) is not null &&
                await ReadBcdBooleanOptionAsync("useplatformclock", cancellationToken) is null;
        if (item.Id is 125 or 126 && IsAllowed(item))
        {
            string option = item.Id == 125 ? "tscsyncpolicy" : "useplatformtick";
            string desired = item.Id == 125 ? "Enhanced" : "no";
            return await _backup.ReadOriginalAsync<BcdOptionSnapshot>(BcdBackupKey(option), cancellationToken) is not null &&
                string.Equals(await ReadBcdOptionAsync(option, cancellationToken), desired, StringComparison.OrdinalIgnoreCase);
        }
        if (!IsAllowed(item) || item.RegistryChanges.Count == 0) return false;
        foreach (RegistryChange change in item.RegistryChanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RegistryKey? key = OpenKey(change, writable: false);
            if (change.DeleteOnApply)
            {
                if (key is not null && key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
                continue;
            }
            if (key is null || !key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
            string expected = item.RegistryChanges.Count == 1 ? (item.SelectedOptionValue ?? change.ApplyValue) : change.ApplyValue;
            RegistryValueKind actualKind = key.GetValueKind(change.ValueName);
            if (!ValueMatches(actualKind, key.GetValue(change.ValueName), expected)) return false;
        }
        return true;
    }

    /// <summary>Devuelve el índice de la opción que coincide con el estado real del sistema, o -1.</summary>
    public int DetectOptionIndex(OptimizationItem item)
    {
        if (!item.HasOptions || item.RegistryChanges.Count == 0) return -1;
        for (int i = 0; i < item.Options.Count && i < item.OptionValues.Count; i++)
        {
            bool allMatch = true;
            foreach (RegistryChange change in item.RegistryChanges)
            {
                try
                {
                    using RegistryKey? key = OpenKey(change, writable: false);
                    object? current = key is null ? null : key.GetValue(change.ValueName);
                    RegistryValueKind kind = key is null ? RegistryValueKind.None : key.GetValueKind(change.ValueName);
                    if (!ValueMatches(kind, current, item.OptionValues[i])) { allMatch = false; break; }
                }
                catch { allMatch = false; break; }
            }
            if (allMatch) return i;
        }
        return -1;
    }

    /// <summary>Texto de la opción actualmente activa en el sistema (o el valor sin normalizar).</summary>
    public string DetectAppliedOption(OptimizationItem item)
    {
        int index = DetectOptionIndex(item);
        if (index >= 0 && index < item.Options.Count) return item.Options[index];
        return item.RegistryChanges.Count == 0
            ? (item.IsEnabled ? "Aplicado" : "Sin aplicar")
            : "Personalizado: " + ReadCurrentValue(item);
    }

    /// <summary>Lee el valor real del primer cambio de Registro del ajuste (para la vista Pro).</summary>
    public string ReadCurrentValue(OptimizationItem item)
    {
        if (item.RegistryChanges.Count == 0) return "—";
        RegistryChange change = item.RegistryChanges[0];
        try
        {
            using RegistryKey? key = OpenKey(change, writable: false);
            if (key is null) return "(clave no accesible)";
            if (!key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase)) return "(sin valor)";
            object? value = key.GetValue(change.ValueName);
            if (value is null) return "(nulo)";
            RegistryValueKind kind = key.GetValueKind(change.ValueName);
            return SerializeCurrentValue(value, kind);
        }
        catch (UnauthorizedAccessException) { return "(sin permisos)"; }
        catch (Exception) { return "(no legible)"; }
    }

    public async Task<bool> HasBackupAsync(OptimizationItem item, CancellationToken cancellationToken = default)
    {
        bool? extended = await TryBackupExtendedAsync(item, cancellationToken);
        if (extended is not null) return extended.Value;
        if (item.Id == 193)
            return await _backup.ReadOriginalAsync<bool?>(@"Windows\MMAgent\MemoryCompression", cancellationToken) is not null;
        if (item.Id == 13)
            return await _backup.ReadOriginalAsync<PowerSettingSnapshot>(@"Windows\Power\UsbSelectiveSuspend", cancellationToken) is not null;
        if (item.Id == 47)
            return await _backup.ReadOriginalAsync<PowerIndexSnapshot>(@"Windows\Power\PcieAspm", cancellationToken) is not null;
        if (item.Id == 44)
            return await _backup.ReadOriginalAsync<CpuPowerSnapshot>(@"Windows\Power\CpuPowerPlan", cancellationToken) is not null;
        if (item.Id == 48)
            return await _backup.ReadOriginalAsync<DiskSleepSnapshot>(@"Windows\Power\DiskSleep", cancellationToken) is not null;
        if (item.Id == 158)
            return await _backup.ReadOriginalAsync<PowerIndexSnapshot>(@"Windows\Power\StandbyIdleTimeout", cancellationToken) is not null;
        if (item.Id == 107)
            return await _backup.ReadOriginalAsync<ServiceStateSnapshot>(@"Windows\Services\WSearch", cancellationToken) is not null;
        if (item.Id == 163)
        {
            RegistryChange change = item.RegistryChanges[0];
            return await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(change), cancellationToken) is not null &&
                await _backup.ReadOriginalAsync<ServiceStateSnapshot>(@"Windows\Services\WbioSrvc", cancellationToken) is not null;
        }
        if (item.Id == 17)
        {
            RegistryChange priorityChange = item.RegistryChanges[0];
            return await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(priorityChange), cancellationToken) is not null;
        }
        if (item.Id == 14)
            return await _backup.ReadOriginalAsync<DevicePowerSnapshot>(@"Windows\Devices\UsbHidPower", cancellationToken) is not null;
        if (item.Id == 32)
            return await _backup.ReadOriginalAsync<BcdOptionSnapshot>(@"Windows\BCD", cancellationToken) is not null;
        if (item.Id == 33)
            return await _backup.ReadOriginalAsync<BcdOptionSnapshot>(@"Windows\BCD\useplatformclock", cancellationToken) is not null;
        if (item.Id is 125 or 126)
            return await _backup.ReadOriginalAsync<BcdOptionSnapshot>(BcdBackupKey(item.Id == 125 ? "tscsyncpolicy" : "useplatformtick"), cancellationToken) is not null;
        if (item.Id == 118)
        {
            if (item.RegistryChanges.Count != 1) return false;
            RegistryChange change = item.RegistryChanges[0];
            return await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(change), cancellationToken) is not null &&
                await _backup.ReadOriginalAsync<bool?>(@"Windows\MMAgent\PageCombining", cancellationToken) is not null;
        }
        if (item.RegistryChanges.Count == 0) return false;
        foreach (RegistryChange change in item.RegistryChanges)
            if (await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(change), cancellationToken) is null)
                return false;
        return true;
    }

    // ===== Defecto: valores oficiales de Windows documentados en el catálogo =====

    /// <summary>Indica si el ajuste tiene un valor oficial de Windows para el botón Defecto.</summary>
    public Task<bool> HasOfficialDefaultAsync(OptimizationItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!item.IsImplemented || item.IsTool) return Task.FromResult(false);
        // Con Registro siempre hay un valor oficial: si RevertValue está vacío,
        // el estado predeterminado de Windows es que el valor no exista (se elimina).
        return Task.FromResult(item.RegistryChanges.Count > 0 || item.IsImplemented);
    }

    /// <summary>Texto del valor oficial que se aplicaría con Defecto.</summary>
    public Task<string> ReadOfficialDefaultTextAsync(OptimizationItem item, CancellationToken cancellationToken = default)
    {
        if (item.RegistryChanges.Count == 0) return Task.FromResult("—");
        string text = string.Join(" · ", item.RegistryChanges.Select(change =>
            $"{change.ValueName} = {(string.IsNullOrWhiteSpace(change.RevertValue) ? "(ausente)" : change.RevertValue)}"));
        return Task.FromResult(text);
    }

    /// <summary>Vuelve al valor oficial de Windows (catalogado), no al valor que tuviera este equipo.</summary>
    public async Task<ExecutionResult> SetDefaultAsync(OptimizationItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            if (item.RegistryChanges.Count > 0)
            {
                foreach (RegistryChange change in item.RegistryChanges)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string key = MakeBackupKey(change);
                    if (!string.IsNullOrWhiteSpace(change.RevertValue))
                    {
                        WriteValue(change, change.RevertValue);
                    }
                    else
                    {
                        // Sin valor documentado: Windows lo crea/usa por defecto eliminando el valor.
                        using RegistryKey? existing = OpenKey(change, writable: true);
                        existing?.DeleteValue(change.ValueName, throwOnMissingValue: false);
                    }
                    if (await _backup.HasBackupAsync(key, cancellationToken))
                        await _backup.RemoveOriginalAsync(key, cancellationToken);
                }
                return new(true, "Se aplicó el valor oficial de Windows documentado para este ajuste.");
            }

            // Ajustes sin Registro: si no hay respaldo, ya están en el estado predeterminado.
            if (!await HasBackupAsync(item, cancellationToken))
                return new(true, "El ajuste ya está en su estado predeterminado; no se aplicó ningún cambio previo.");

            return await SetEnabledAsync(item, false, true, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or ArgumentException)
        {
            return new(false, $"No se pudo aplicar el valor oficial: {ex.Message}");
        }
    }

    public async Task<ExecutionResult> SetEnabledAsync(OptimizationItem item, bool enabled, bool hardwareApplicable = true, CancellationToken cancellationToken = default)
    {
        if (!IsAllowed(item)) return new(false, "La acción no está habilitada en esta versión.");
        ExecutionResult? extended = await TrySetExtendedAsync(item, enabled, hardwareApplicable, cancellationToken);
        if (extended is not null) return extended;
        if (item.Id == 193) return await SetMemoryCompressionAsync(enabled, hardwareApplicable, cancellationToken);
        if (item.Id == 13) return await SetUsbSelectiveSuspendAsync(enabled, cancellationToken);
        if (item.Id == 47) return await SetPcieAspmAsync(enabled, cancellationToken);
        if (item.Id == 44) return await SetCpuPowerPlanAsync(enabled, cancellationToken);
        if (item.Id == 48) return await SetDiskSleepPlanAsync(enabled, cancellationToken);
        if (item.Id == 158) return await SetStandbyIdleTimeoutAsync(enabled, cancellationToken);
        if (item.Id == 107) return await SetWindowsSearchAsync(enabled, cancellationToken);
        if (item.Id == 163) return await SetBiometricsAsync(item, enabled, cancellationToken);
        if (item.Id == 17) return await SetPrioritySeparationAsync(item, enabled, 36, cancellationToken);
        if (item.Id == 14) return await SetUsbHidPowerManagementAsync(enabled, cancellationToken);
        if (item.Id == 32) return await SetDynamicTickAsync(enabled, cancellationToken);
        if (item.Id == 33) return await SetPlatformClockAsync(enabled, cancellationToken);
        if (item.Id is 125 or 126) return await SetBcdProfileOptionAsync(item.Id, enabled, cancellationToken);
        if (enabled && !hardwareApplicable) return new(false, "El hardware detectado no cumple los requisitos de este ajuste.");
        if (item.RegistryChanges.Any(change => change.Hive is "HKLM" or "HKCR") && !IsAdministrator())
            return new(false, "Este ajuste requiere privilegios de administrador. Cierra la app y ábrela con clic derecho → Ejecutar como administrador.");
        if (item.Id == 118) return await SetPageCombiningAsync(item, enabled, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (RegistryChange change in item.RegistryChanges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string backupKey = MakeBackupKey(change);
                if (enabled)
                {
                    RegistryValueSnapshot snapshot = ReadSnapshot(change);
                    await _backup.SaveOriginalAsync(backupKey, snapshot, cancellationToken);
                }
            }
            if (enabled)
            {
                foreach (RegistryChange change in item.RegistryChanges)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? overrideValue = item.RegistryChanges.Count == 1 ? item.SelectedOptionValue : null;
                    ApplyRegistryChange(change, overrideValue);
                }
            }
            else
            {
                var snapshots = new Dictionary<string, RegistryValueSnapshot>(StringComparer.OrdinalIgnoreCase);
                foreach (RegistryChange change in item.RegistryChanges)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RegistryValueSnapshot? snapshot = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(change), cancellationToken);
                    if (snapshot is null)
                        return new(false, "No existe un respaldo previo de este ajuste; no se modificó el Registro para evitar sobrescribir una configuración desconocida.");
                    snapshots.Add(MakeBackupKey(change), snapshot);
                }
                foreach (RegistryChange change in item.RegistryChanges)
                    RestoreValue(change, snapshots[MakeBackupKey(change)]);
                foreach (RegistryChange change in item.RegistryChanges)
                    await _backup.RemoveOriginalAsync(MakeBackupKey(change), cancellationToken);
            }
            string message = item.Id == 168
                ? enabled
                    ? "Se guardó y aplicó el valor binario para ocultar el sufijo en nuevos accesos directos. Cierra sesión y vuelve a entrar para que Explorer lo use."
                    : "Se restauró el valor binario original de Windows. Cierra sesión y vuelve a entrar para actualizar Explorer."
                : item.Id == 167
                    ? enabled
                        ? "Se respaldaron y quitaron los marcadores de acceso directo. Reinicia el Explorador de Windows para actualizar los iconos; las opciones del Shell que dependen de IsShortcut pueden cambiar."
                        : "Se restauraron exactamente los marcadores IsShortcut respaldados. Reinicia el Explorador de Windows para actualizar los iconos."
                : enabled ? "Ajuste aplicado. Se guardó el estado previo." : "Se restauró el estado original guardado.";
            return new(true, message);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException or JsonException)
        {
            if (enabled)
            {
                foreach (RegistryChange change in item.RegistryChanges)
                {
                    try
                    {
                        RegistryValueSnapshot? snapshot = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(change), CancellationToken.None);
                        if (snapshot is not null) RestoreValue(change, snapshot);
                        await _backup.RemoveOriginalAsync(MakeBackupKey(change), CancellationToken.None);
                    }
                    catch { }
                }
            }
            return new(false, $"No se pudo completar el ajuste: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    public async Task<ExecutionResult> SetPrioritySeparationAsync(OptimizationItem item, bool enabled, int selectedValue, CancellationToken cancellationToken = default)
    {
        if (item.Id != 17 || !IsAllowed(item) || selectedValue is not (36 or 38 or 40))
            return new(false, "El perfil de Win32PrioritySeparation no es válido.");
        if (!IsAdministrator()) return new(false, "Este ajuste requiere privilegios de administrador. Cierra la app y ábrela con clic derecho → Ejecutar como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RegistryChange change = item.RegistryChanges[0];
            string backupKey = MakeBackupKey(change);
            if (enabled)
            {
                await _backup.SaveOriginalAsync(backupKey, ReadSnapshot(change), cancellationToken);
                ApplyRegistryChange(change, selectedValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return new(true, $"Win32PrioritySeparation={selectedValue} aplicado y valor original respaldado. El resultado depende de la carga y versión de Windows.");
            }
            RegistryValueSnapshot? snapshot = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, cancellationToken);
            if (snapshot is null) return new(false, "No existe un respaldo previo; no se modificó el Registro.");
            RestoreValue(change, snapshot);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauró el valor original de Win32PrioritySeparation.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException or JsonException)
        {
            return new(false, $"No se pudo completar Win32PrioritySeparation: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static bool IsPrioritySeparationActive(OptimizationItem item)
    {
        RegistryChange change = item.RegistryChanges[0];
        using RegistryKey? key = OpenKey(change, writable: false);
        if (key is null || !key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
        return int.TryParse(Convert.ToString(key.GetValue(change.ValueName), System.Globalization.CultureInfo.InvariantCulture), out int value) && value is 36 or 38 or 40;
    }

    private async Task<ExecutionResult> SetUsbHidPowerManagementAsync(bool enabled, CancellationToken cancellationToken)
    {
        const string backupKey = @"Windows\Devices\UsbHidPower";
        if (!IsAdministrator()) return new(false, "Este ajuste requiere privilegios de administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                DevicePowerSnapshot snapshot = CaptureUsbHidPowerValues();
                int valueCount = snapshot.Keys.Sum(key => key.Values.Count);
                if (valueCount == 0) return new(false, "No se encontraron valores DWORD compatibles en Device Parameters de dispositivos USB/HID; no se cambió Windows.");
                await _backup.SaveOriginalAsync(backupKey, snapshot, cancellationToken);
                try
                {
                    SetUsbHidPowerValues(snapshot, disable: true);
                }
                catch
                {
                    try
                    {
                        RestoreUsbHidPowerValues(snapshot);
                        await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    }
                    catch { /* el respaldo se conserva para restauración manual desde la app */ }
                    throw;
                }
                return new(true, $"Se desactivaron parámetros de ahorro energético en {snapshot.Keys.Count} instancias USB/HID ({valueCount} valores respaldados). Los dispositivos nuevos no quedan incluidos hasta reaplicar el ajuste.");
            }

            DevicePowerSnapshot? original = await _backup.ReadOriginalAsync<DevicePowerSnapshot>(backupKey, cancellationToken);
            if (original is null) return new(false, "No existe respaldo de este ajuste; no se modificó ningún dispositivo.");
            RestoreUsbHidPowerValues(original);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, $"Se restauraron exactamente {original.Keys.Sum(key => key.Values.Count)} valores en {original.Keys.Count} instancias USB/HID.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException or JsonException or System.Security.SecurityException)
        {
            return new(false, $"No se pudo completar el ajuste USB/HID: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> IsUsbHidPowerManagementDisabledAsync(CancellationToken cancellationToken)
    {
        DevicePowerSnapshot? snapshot = await _backup.ReadOriginalAsync<DevicePowerSnapshot>(@"Windows\Devices\UsbHidPower", cancellationToken);
        DevicePowerSnapshot check = snapshot ?? CaptureUsbHidPowerValues();
        if (check.Keys.Count == 0) return false;
        foreach (DevicePowerKeySnapshot keySnapshot in check.Keys)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keySnapshot.RegistryPath, writable: false);
                if (key is null) return false;
                foreach (DevicePowerValueSnapshot value in keySnapshot.Values)
                {
                    if (!key.GetValueNames().Contains(value.Name, StringComparer.OrdinalIgnoreCase) || key.GetValueKind(value.Name) != RegistryValueKind.DWord || Convert.ToInt32(key.GetValue(value.Name), System.Globalization.CultureInfo.InvariantCulture) != 0)
                        return false;
                }
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }
        return true;
    }

    private async Task<ExecutionResult> SetDynamicTickAsync(bool enabled, CancellationToken cancellationToken)
    {
        const string backupKey = @"Windows\BCD";
        if (!IsAdministrator()) return new(false, "Modificar el almacén de arranque requiere ejecutar la aplicación como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                string? current = await ReadDynamicTickOptionAsync(cancellationToken);
                var snapshot = new BcdOptionSnapshot(current is not null, current);
                await _backup.SaveOriginalAsync(backupKey, snapshot, cancellationToken);
                try { await RunBcdEditAsync(["/set", "{current}", "disabledynamictick", "yes"], cancellationToken); }
                catch
                {
                    try
                    {
                        await RestoreDynamicTickOptionAsync(snapshot, CancellationToken.None);
                        await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    }
                    catch { /* keep the exact backup if rollback cannot be confirmed */ }
                    throw;
                }
                return new(true, "Se configuró disabledynamictick=yes y se respaldó el estado anterior. Microsoft clasifica esta opción como depuración; requiere reiniciar y no garantiza mejoras de rendimiento.");
            }

            BcdOptionSnapshot? original = await _backup.ReadOriginalAsync<BcdOptionSnapshot>(backupKey, cancellationToken);
            if (original is null) return new(false, "No hay respaldo de la opción de arranque; no se modificó el almacén BCD.");
            await RestoreDynamicTickOptionAsync(original, cancellationToken);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauró el valor original de disabledynamictick en el almacén BCD. Reinicia Windows para que el estado restaurado surta efecto.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return new(false, $"No se pudo modificar la opción de arranque: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetPlatformClockAsync(bool enabled, CancellationToken cancellationToken)
    {
        const string option = "useplatformclock";
        const string backupKey = @"Windows\BCD\useplatformclock";
        if (!IsAdministrator()) return new(false, "Modificar el almacén de arranque requiere ejecutar la aplicación como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? current = await ReadBcdBooleanOptionAsync(option, cancellationToken);
            if (enabled)
            {
                BcdOptionSnapshot? existingBackup = await _backup.ReadOriginalAsync<BcdOptionSnapshot>(backupKey, cancellationToken);
                if (existingBackup is not null) return new(false, "Este ajuste ya tiene un respaldo pendiente; restaura primero el valor anterior.");
                var snapshot = new BcdOptionSnapshot(current is not null, current);
                await _backup.SaveOriginalAsync(backupKey, snapshot, cancellationToken);
                try
                {
                    if (current is not null) await RunBcdEditAsync(["/deletevalue", "{current}", option], cancellationToken);
                }
                catch
                {
                    try { await RestoreBcdBooleanOptionAsync(option, snapshot, CancellationToken.None); await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None); }
                    catch { /* conserva el respaldo para una restauración posterior */ }
                    throw;
                }
                return new(true, current is null
                    ? "useplatformclock ya estaba ausente; se guardó el estado original. No se solicitó otro reloj de plataforma. Requiere reiniciar."
                    : "Se eliminó useplatformclock de la entrada BCD actual. Se conservó el estado original para restaurarlo. Requiere reiniciar; Windows decidirá el temporizador apropiado para el hardware.");
            }

            BcdOptionSnapshot? original = await _backup.ReadOriginalAsync<BcdOptionSnapshot>(backupKey, cancellationToken);
            if (original is null) return new(false, "No hay respaldo original de useplatformclock; no se modificó el almacén BCD.");
            await RestoreBcdBooleanOptionAsync(option, original, cancellationToken);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauró el estado original de useplatformclock. Reinicia Windows para que el estado surta efecto.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException)
        {
            return new(false, $"No se pudo modificar useplatformclock: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static async Task RestoreBcdBooleanOptionAsync(string option, BcdOptionSnapshot snapshot, CancellationToken cancellationToken)
    {
        string? current = await ReadBcdBooleanOptionAsync(option, cancellationToken);
        if (snapshot.Exists)
        {
            string original = snapshot.Value ?? throw new InvalidOperationException("El respaldo BCD está incompleto.");
            if (!string.Equals(current, original, StringComparison.OrdinalIgnoreCase))
                await RunBcdEditAsync(["/set", "{current}", option, original], cancellationToken);
        }
        else if (current is not null)
            await RunBcdEditAsync(["/deletevalue", "{current}", option], cancellationToken);
    }

    private static async Task<string?> ReadBcdBooleanOptionAsync(string option, CancellationToken cancellationToken)
    {
        string output = await RunBcdEditAsync(["/enum", "{current}"], cancellationToken);
        foreach (string line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(line,
                @"^\s*" + System.Text.RegularExpressions.Regex.Escape(option) + @"\s+(\S+)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string value = match.Groups[1].Value;
            if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("sí", StringComparison.OrdinalIgnoreCase) || value.Equals("si", StringComparison.OrdinalIgnoreCase) || value.Equals("oui", StringComparison.OrdinalIgnoreCase) || value.Equals("ja", StringComparison.OrdinalIgnoreCase)) return "yes";
            if (value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("non", StringComparison.OrdinalIgnoreCase) || value.Equals("nein", StringComparison.OrdinalIgnoreCase)) return "no";
            throw new InvalidOperationException($"No se pudo interpretar {option} en el BCD (valor: {value}). No se modificó.");
        }
        return null;
    }

    private static string BcdBackupKey(string option) => @"Windows\BCD\" + option;

    private async Task<ExecutionResult> SetBcdProfileOptionAsync(int itemId, bool enabled, CancellationToken cancellationToken)
    {
        string option = itemId == 125 ? "tscsyncpolicy" : "useplatformtick";
        string desired = itemId == 125 ? "Enhanced" : "no";
        string backupKey = BcdBackupKey(option);
        if (!IsAdministrator()) return new(false, "Modificar el almacén de arranque requiere ejecutar la aplicación como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? current = await ReadBcdOptionAsync(option, cancellationToken);
            if (enabled)
            {
                if (await _backup.ReadOriginalAsync<BcdOptionSnapshot>(backupKey, cancellationToken) is not null)
                    return new(false, "Este ajuste ya tiene un respaldo pendiente; restáuralo antes de aplicarlo otra vez.");
                var snapshot = new BcdOptionSnapshot(current is not null, current);
                await _backup.SaveOriginalAsync(backupKey, snapshot, cancellationToken);
                try
                {
                    if (!string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
                        await RunBcdEditAsync(["/set", "{current}", option, desired], cancellationToken);
                }
                catch
                {
                    try { await RestoreBcdOptionAsync(option, snapshot, CancellationToken.None); await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None); }
                    catch { /* conserva el respaldo para restauración manual */ }
                    throw;
                }
                return new(true, current is null || !string.Equals(current, desired, StringComparison.OrdinalIgnoreCase)
                    ? $"Se configuró {option}={desired} en la entrada BCD actual; se respaldó el estado previo. Requiere reiniciar y puede requerir suspender BitLocker o Secure Boot. No garantiza mejoras de rendimiento."
                    : $"{option} ya tenía el valor {desired}; se guardó el estado para poder gestionarlo desde ApexKernel.");
            }

            BcdOptionSnapshot? original = await _backup.ReadOriginalAsync<BcdOptionSnapshot>(backupKey, cancellationToken);
            if (original is null) return new(false, $"No hay respaldo original de {option}; no se modificó el almacén BCD.");
            await RestoreBcdOptionAsync(option, original, cancellationToken);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, $"Se restauró el estado original de {option}. Reinicia Windows para que el estado surta efecto.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException)
        {
            return new(false, $"No se pudo cambiar {option}: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static async Task RestoreBcdOptionAsync(string option, BcdOptionSnapshot snapshot, CancellationToken cancellationToken)
    {
        string? current = await ReadBcdOptionAsync(option, cancellationToken);
        if (snapshot.Exists)
        {
            string original = snapshot.Value ?? throw new InvalidOperationException("El respaldo BCD está incompleto.");
            if (!string.Equals(current, original, StringComparison.OrdinalIgnoreCase))
                await RunBcdEditAsync(["/set", "{current}", option, original], cancellationToken);
        }
        else if (current is not null)
            await RunBcdEditAsync(["/deletevalue", "{current}", option], cancellationToken);
    }

    private static async Task<string?> ReadBcdOptionAsync(string option, CancellationToken cancellationToken)
    {
        string output = await RunBcdEditAsync(["/enum", "{current}"], cancellationToken);
        foreach (string line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(line,
                @"^\s*" + System.Text.RegularExpressions.Regex.Escape(option) + @"\s+(\S+)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    private static async Task RestoreDynamicTickOptionAsync(BcdOptionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Exists)
            await RunBcdEditAsync(["/set", "{current}", "disabledynamictick", snapshot.Value ?? throw new InvalidOperationException("El respaldo BCD está incompleto.")], cancellationToken);
        else if (await ReadDynamicTickOptionAsync(cancellationToken) is not null)
            await RunBcdEditAsync(["/deletevalue", "{current}", "disabledynamictick"], cancellationToken);
    }

    private static async Task<string?> ReadDynamicTickOptionAsync(CancellationToken cancellationToken)
    {
        string output = await RunBcdEditAsync(["/enum", "{current}"], cancellationToken);
        foreach (string line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*disabledynamictick\s+(\S+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string value = match.Groups[1].Value;
            if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("sí", StringComparison.OrdinalIgnoreCase) || value.Equals("si", StringComparison.OrdinalIgnoreCase) || value.Equals("oui", StringComparison.OrdinalIgnoreCase) || value.Equals("ja", StringComparison.OrdinalIgnoreCase)) return "yes";
            if (value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("non", StringComparison.OrdinalIgnoreCase) || value.Equals("nein", StringComparison.OrdinalIgnoreCase)) return "no";
            throw new InvalidOperationException($"No se pudo interpretar disabledynamictick en el BCD (valor: {value}). No se modificó.");
        }
        return null;
    }

    private static async Task<string> RunBcdEditAsync(string[] arguments, CancellationToken cancellationToken)
    {
        string bcdedit = Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
        var start = new ProcessStartInfo(bcdedit) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("No se pudo iniciar BCDEdit.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await stdout;
        string error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"BCDEdit terminó con código {process.ExitCode}." : error.Trim());
        return output;
    }

    private static DevicePowerSnapshot CaptureUsbHidPowerValues()
    {
        string[] names = ["EnhancedPowerManagementEnabled", "AllowIdleIrpInD3", "SelectiveSuspendEnabled"];
        var entries = new List<DevicePowerKeySnapshot>();
        foreach (string rootPath in new[] { @"SYSTEM\CurrentControlSet\Enum\USB", @"SYSTEM\CurrentControlSet\Enum\HID" })
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(rootPath, writable: false);
            if (root is null) continue;
            Walk(root, rootPath, depth: 0);
        }
        return new(entries);

        void Walk(RegistryKey parent, string parentPath, int depth)
        {
            if (depth > 6) return;
            try
            {
                using RegistryKey? parameters = parent.OpenSubKey("Device Parameters", writable: false);
                if (parameters is not null)
                {
                    var values = new List<DevicePowerValueSnapshot>();
                    foreach (string name in names)
                    {
                        try
                        {
                            if (!parameters.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                            if (parameters.GetValueKind(name) != RegistryValueKind.DWord) continue;
                            values.Add(new(name, ReadSnapshot(new RegistryChange
                            {
                                Hive = "HKLM", KeyPath = parentPath + @"\Device Parameters", ValueName = name,
                                ValueKind = "DWord", ApplyValue = "0", RevertValue = "0"
                            })));
                        }
                        catch { /* clave de dispositivo protegida: se omite */ }
                    }
                    if (values.Count > 0) entries.Add(new(parentPath + @"\Device Parameters", values));
                }
            }
            catch { /* Device Parameters no accesible */ }

            try
            {
                foreach (string childName in parent.GetSubKeyNames())
                {
                    using RegistryKey? child = parent.OpenSubKey(childName, writable: false);
                    if (child is not null) Walk(child, parentPath + "\\" + childName, depth + 1);
                }
            }
            catch { /* se omiten subclaves sin permiso */ }
        }
    }

    private static void SetUsbHidPowerValues(DevicePowerSnapshot snapshot, bool disable)
    {
        foreach (DevicePowerKeySnapshot entry in snapshot.Keys)
        {
            using RegistryKey key = Registry.LocalMachine.OpenSubKey(entry.RegistryPath, writable: true)
                ?? throw new InvalidOperationException($"Ya no está disponible la clave HKLM\\{entry.RegistryPath}.");
            foreach (DevicePowerValueSnapshot value in entry.Values)
            {
                if (disable) key.SetValue(value.Name, 0, RegistryValueKind.DWord);
                else RestoreValue(new RegistryChange
                {
                    Hive = "HKLM", KeyPath = entry.RegistryPath, ValueName = value.Name,
                    ValueKind = value.Original.Kind, ApplyValue = "0", RevertValue = "0"
                }, value.Original);
            }
        }
    }

    private static void RestoreUsbHidPowerValues(DevicePowerSnapshot snapshot)
    {
        foreach (DevicePowerKeySnapshot entry in snapshot.Keys)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(entry.RegistryPath, writable: true);
            if (key is null) throw new InvalidOperationException($"No se pudo abrir HKLM\\{entry.RegistryPath} para restaurar el respaldo.");
            foreach (DevicePowerValueSnapshot value in entry.Values)
                RestoreValue(new RegistryChange
                {
                    Hive = "HKLM", KeyPath = entry.RegistryPath, ValueName = value.Name,
                    ValueKind = value.Original.Kind, ApplyValue = "0", RevertValue = "0"
                }, value.Original);
        }
    }

    private static bool IsAllowed(OptimizationItem item)
    {
        if (IsAllowedExtended(item)) return true;
        if (item.Id == 193)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("Disable-MMAgent -MemoryCompression", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 118)
            return MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ("DisablePageCombining", "1", "0", "DWord"));
        if (item.Id == 107)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("WSearch", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 13)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("48e6b7a6-50f5-4782-a5d4-53bb8f07e226", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 47)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("ee12f906-d277-404b-b6da-e5fa1a576df5", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 44)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("893dee8e-2bef-41e0-89c6-b55d0929964c", StringComparison.OrdinalIgnoreCase) && item.TechnicalSummary.Contains("bc5038f7-23e0-4960-96da-33abaf5935ec", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 48)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("6738e2c4-e8a5-4a42-b16a-e040e769756e", StringComparison.OrdinalIgnoreCase) && item.TechnicalSummary.Contains("94ac6d29-73ce-41a6-809f-6363ba21b47e", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 158)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("29f6c1db-86da-48c5-9fdb-f2b67b1f44da", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 17)
            return item.RegistryChanges.Count == 1 && item.RegistryChanges[0] is { Hive: "HKLM", KeyPath: @"SYSTEM\CurrentControlSet\Control\PriorityControl", ValueName: "Win32PrioritySeparation", ValueKind: "DWord" } &&
                item.TechnicalSummary.Contains("Win32PrioritySeparation", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 14)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(@"Enum\USB", StringComparison.OrdinalIgnoreCase) && item.TechnicalSummary.Contains("HID", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 32)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("bcdedit", StringComparison.OrdinalIgnoreCase) && item.TechnicalSummary.Contains("disabledynamictick", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 33)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("bcdedit", StringComparison.OrdinalIgnoreCase) && item.TechnicalSummary.Contains("useplatformclock", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 125)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("bcdedit", StringComparison.OrdinalIgnoreCase) && item.TechnicalSummary.Contains("tscsyncpolicy", StringComparison.OrdinalIgnoreCase);
        if (item.Id == 126)
            return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("bcdedit", StringComparison.OrdinalIgnoreCase) && item.TechnicalSummary.Contains("useplatformtick", StringComparison.OrdinalIgnoreCase);
        if (item.RegistryChanges.Count == 0) return false;
        return item.Id switch
        {
            1 => Match(item, @"Control Panel\Mouse", ("MouseSpeed", "0", "1", "String"), ("MouseThreshold1", "0", "6", "String"), ("MouseThreshold2", "0", "10", "String")),
            3 => Match(item, @"Control Panel\Mouse", ("MouseSensitivity", "10", "10", "String")),
            4 => Match(item, @"Control Panel\Mouse", ("MouseTrails", "0", "0", "String"), ("SnapToDefaultButton", "0", "0", "String")),
            5 => Match(item, @"Control Panel\Mouse", ("DoubleClickSpeed", "200", "500", "String")),
            6 => Match(item, @"Control Panel\Accessibility\MouseKeys", ("Flags", "62", "126", "String"), ("MaximumSpeed", "10", "80", "String"), ("TimeToMaximumSpeed", "3000", "3000", "String")),
            7 => MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", ("MouseDataQueueSize", "30", "100", "DWord")),
            8 => MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", ("KeyboardDataQueueSize", "30", "100", "DWord")),
            9 => Match(item, @"Control Panel\Accessibility\Keyboard Response", ("AutoRepeatDelay", "150", "1000", "String"), ("AutoRepeatRate", "25", "500", "String"), ("DelayBeforeAcceptance", "0", "1000", "String"), ("BounceTime", "0", "0", "String"), ("Flags", "59", "126", "String")),
            46 => MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", ("PowerThrottlingOff", "1", "0", "DWord")),
            76 => MatchManyHive(item,
                ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", "0", "0", "DWord"),
                ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOPercentageMaxBackgroundBandwidth", "5", "0", "DWord")),
            89 => MatchHive(item, "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", ("LetAppsRunInBackground", "2", "0", "DWord")),
            97 => MatchHive(item, "HKLM", @"SOFTWARE\Policies\Microsoft\Dsh", ("AllowNewsAndInterests", "0", "1", "DWord")),
            104 => MatchManyHive(item,
                ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", "1", "0", "DWord"),
                ("HKLM", @"SYSTEM\CurrentControlSet\Control\CrashControl", "DisplayParameters", "1", "0", "DWord")),
            163 => MatchHive(item, "HKLM", @"SOFTWARE\Policies\Microsoft\Biometrics", ("Enabled", "0", "1", "DWord")),
            161 => MatchHive(item, "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Appx", ("AllowAutomaticAppArchiving", "0", "65535", "DWord")),
            156 => MatchHive(item, "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", ("EnableMmx", "0", "1", "DWord")),
            160 => Match(item, @"Software\Policies\Microsoft\Windows\CloudContent", ("DisableWindowsSpotlightFeatures", "1", "0", "DWord")),
            169 => MatchHive(item, "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Personalization", ("NoLockScreen", "1", "0", "DWord")),
            167 => MatchDeletedValues(item,
                ("HKCR", @"lnkfile", "IsShortcut"),
                ("HKCR", @"piffile", "IsShortcut")),
            115 => MatchHive(item, "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ("SystemResponsiveness", "10", "20", "DWord")),
            49 => MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", ("HiberbootEnabled", "0", "1", "DWord")),
            90 => MatchMany(item,
                ( @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", "0", "1", "DWord"),
                (@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1", "String")),
            101 => Match(item, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", ("", "", "", "String")),
            103 => Match(item, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", ("HideFileExt", "0", "1", "DWord"), ("Hidden", "1", "2", "DWord")),
            168 => Match(item, @"Software\Microsoft\Windows\CurrentVersion\Explorer", ("link", "AAAAAA==", "HgAAAA==", "Binary")),
            55 => MatchHive(item, "HKLM", @"SOFTWARE\Microsoft\Windows\Dwm", ("OverlayTestMode", "5", "0", "DWord")),
            52 => MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ("HwSchMode", "2", "0", "DWord")),
            34 => MatchManyHive(item,
                ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", "8", "8", "DWord"),
                ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", "6", "2", "DWord"),
                ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "High", "Medium", "String"),
                ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "SFIO Priority", "High", "Normal", "String")),
            106 => MatchHive(item, "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", ("MaintenanceDisabled", "1", "0", "DWord")),
            188 => MatchHive(item, "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", ("Disabled", "1", "0", "DWord")),
            180 => MatchHive(item, "HKLM", @"SOFTWARE\Microsoft\FTH", ("Enabled", "0", "1", "DWord")),
            190 => MatchManyHive(item,
                ("HKLM", @"SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "2000", "5000", "String"),
                ("HKCU", @"Control Panel\Desktop", "AutoEndTasks", "1", "0", "String")),
            10 => Match(item, @"Control Panel\Keyboard", ("KeyboardDelay", "0", "1", "String"), ("KeyboardSpeed", "31", "31", "String")),
            11 => Match(item, @"Control Panel\Accessibility\StickyKeys", ("Flags", "506", "510", "String")),
            12 => Match(item, @"Control Panel\Accessibility\ToggleKeys", ("Flags", "58", "58", "String")),
            53 => Match(item, @"Software\Microsoft\DirectX\UserGpuPreferences", ("DirectXUserGlobalSettings", "SwapEffectUpgradeEnable=1;", "", "String")),
            83 => Match(item, @"Software\Microsoft\Multimedia\Audio", ("UserDuckingPreference", "3", "10", "DWord")),
            87 => Match(item, @"Software\Microsoft\GameBar", ("AllowAutoGameMode", "1", "1", "DWord"), ("AutoGameModeEnabled", "1", "1", "DWord")),
            92 => Match(item, @"Control Panel\Desktop", ("MenuShowDelay", "0", "400", "String")),
            95 => Match(item, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ("NoInstrumentation", "1", "0", "DWord")),
            96 => Match(item, @"Software\Policies\Microsoft\Windows\Windows Search", ("DisableSearchBoxSuggestions", "1", "0", "DWord")),
            100 => Match(item, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", ("TaskbarEndTask", "1", "0", "DWord")),
            102 => Match(item, @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell", ("FolderType", "NotSpecified", "NotSpecified", "String")),
            172 => Match(item, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", ("ShowRecent", "0", "1", "DWord"), ("ShowFrequent", "0", "1", "DWord")),
            189 => Match(item, @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", ("RestartApps", "0", "1", "DWord")),
            211 => MatchHive(item, "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ("NetworkThrottlingIndex", "4294967295", "10", "DWord")),
            212 => MatchHive(item, "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ("SystemResponsiveness", "0", "20", "DWord")),
            213 => MatchManyHive(item,
                ("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", "1", "0", "DWord"),
                ("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", "0", "1", "DWord")),
            214 => MatchManyHive(item,
                ("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", "0", "1", "DWord"),
                ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch", "1", "0", "DWord"),
                ("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", "1", "2", "DWord")),
            215 => MatchManyHive(item,
                ("HKCU", @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", "1", "0", "DWord"),
                ("HKCU", @"System\GameConfigStore", "GameDVR_EFSEFeatureFlags", "0", "0", "DWord"),
                ("HKCU", @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", "2", "2", "DWord"),
                ("HKCU", @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", "1", "0", "DWord"),
                ("HKCU", @"System\GameConfigStore", "GameDVR_Enabled", "0", "1", "DWord"),
                ("HKLM", @"SOFTWARE\Microsoft\WindowsRuntime\ActivatableClassId\Windows.Gaming.GameBar.PresenceServer.Internal.PresenceWriter", "ActivationType", "0", "1", "DWord"),
                ("HKLM", @"SOFTWARE\Microsoft\PolicyManager\default\ApplicationManagement\AllowGameDVR", "value", "0", "1", "DWord")),
            216 => Match(item, @"SOFTWARE\Microsoft\Windows\DWM",
                ("EnableAeroPeek", "0", "1", "DWord"),
                ("AlwaysHibernateThumbnails", "0", "1", "DWord")),
            217 => Match(item, @"Software\Microsoft\Windows\CurrentVersion\Explorer\NamingTemplates",
                ("ShortcutNameTemplate", "%s.lnk", "", "String")),
            218 => item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("e9a42b02-d5df-448d-aa00-03f14749eb61", StringComparison.OrdinalIgnoreCase),
            219 => item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase),
            220 or 221 or 222 => item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("36687f9e-e3a5-4dbf-b1dc-15eb381c6863", StringComparison.OrdinalIgnoreCase),
            223 => item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("DwmEnableMMCSS", StringComparison.OrdinalIgnoreCase),
            224 => item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("AdvancedEEE", StringComparison.OrdinalIgnoreCase),
            225 => item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("WinSAT", StringComparison.OrdinalIgnoreCase),
            226 => Match(item, @"Software\Microsoft\Windows\CurrentVersion\Explorer", ("EnableAutoTray", "0", "1", "DWord")),
            227 => MatchHive(item, "HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ("NetworkThrottlingIndex", "10", "10", "DWord")),            _ => false
        };
    }

    private static bool Match(OptimizationItem item, string path, params (string Name, string Apply, string Revert, string Kind)[] expected) =>
        item.RegistryChanges.Count == expected.Length && expected.All(pair => item.RegistryChanges.Any(change =>
            change.Hive == "HKCU" &&
            string.Equals(change.KeyPath, path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ValueName, pair.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ValueKind, pair.Kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ApplyValue, pair.Apply, StringComparison.Ordinal) &&
            string.Equals(change.RevertValue, pair.Revert, StringComparison.Ordinal)));

    private static string SerializeCurrentValue(object? value, RegistryValueKind kind) => value switch
    {
        byte[] bytes when kind == RegistryValueKind.Binary => Convert.ToBase64String(bytes),
        string[] strings when kind == RegistryValueKind.MultiString => string.Join('|', strings),
        _ => value?.ToString() ?? string.Empty
    };

    /// <summary>
    /// Compara el valor real del registro con el valor elegido en el desplegable.
    /// Los DWORD se comparan numéricamente para que 4294967295 y 0xFFFFFFFF (−1)
    /// se reconozcan como el mismo estado.
    /// </summary>
    private static bool ValueMatches(RegistryValueKind kind, object? current, string expected)
    {
        if (expected == "__DELETE__") return current is null;
        try
        {
            return kind switch
            {
                RegistryValueKind.DWord => current is int i && ParseDWordValue(expected) == i,
                RegistryValueKind.QWord => current is long l && ParseQWordValue(expected) == l,
                RegistryValueKind.Binary or RegistryValueKind.None =>
                    current is byte[] bytes && string.Equals(Convert.ToBase64String(bytes), expected, StringComparison.OrdinalIgnoreCase),
                RegistryValueKind.MultiString =>
                    current is string[] strings && string.Equals(string.Join('|', strings), expected, StringComparison.Ordinal),
                _ => string.Equals(current?.ToString() ?? string.Empty, expected, StringComparison.Ordinal)
            };
        }
        catch (FormatException) { return false; }
        catch (InvalidCastException) { return false; }
    }

    private static bool MatchMany(OptimizationItem item, params (string Path, string Name, string Apply, string Revert, string Kind)[] expected) =>
        item.RegistryChanges.Count == expected.Length && expected.All(pair => item.RegistryChanges.Any(change =>
            change.Hive == "HKCU" &&
            string.Equals(change.KeyPath, pair.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ValueName, pair.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ValueKind, pair.Kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ApplyValue, pair.Apply, StringComparison.Ordinal) &&
            string.Equals(change.RevertValue, pair.Revert, StringComparison.Ordinal)));

    private static bool MatchHive(OptimizationItem item, string hive, string path, params (string Name, string Apply, string Revert, string Kind)[] expected) =>
        item.RegistryChanges.Count == expected.Length && expected.All(pair => item.RegistryChanges.Any(change =>
            string.Equals(change.Hive, hive, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.KeyPath, path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ValueName, pair.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ValueKind, pair.Kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ApplyValue, pair.Apply, StringComparison.Ordinal) &&
            string.Equals(change.RevertValue, pair.Revert, StringComparison.Ordinal)));

    private static bool MatchManyHive(OptimizationItem item, params (string Hive, string Path, string Name, string Apply, string Revert, string Kind)[] expected) =>
        item.RegistryChanges.Count == expected.Length && expected.All(pair => item.RegistryChanges.Any(change =>
            string.Equals(change.Hive, pair.Hive, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.KeyPath, pair.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ValueName, pair.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ValueKind, pair.Kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ApplyValue, pair.Apply, StringComparison.Ordinal) &&
            string.Equals(change.RevertValue, pair.Revert, StringComparison.Ordinal)));

    private static bool MatchDeletedValues(OptimizationItem item, params (string Hive, string Path, string Name)[] expected) =>
        item.RegistryChanges.Count == expected.Length && expected.All(pair => item.RegistryChanges.Any(change =>
            string.Equals(change.Hive, pair.Hive, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.KeyPath, pair.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ValueName, pair.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(change.ValueKind, "String", StringComparison.OrdinalIgnoreCase) &&
            change.DeleteOnApply));

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async Task<ExecutionResult> SetUsbSelectiveSuspendAsync(bool enabled, CancellationToken cancellationToken)
    {
        const string backupKey = @"Windows\Power\UsbSelectiveSuspend";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                PowerSettingSnapshot original = ReadUsbSelectiveSuspend();
                await _backup.SaveOriginalAsync(backupKey, original, cancellationToken);
                try { WriteUsbSelectiveSuspend(original.Scheme, 0, 0, refreshIfActive: true); }
                catch
                {
                    try { WriteUsbSelectiveSuspend(original.Scheme, original.AcValue, original.DcValue, refreshIfActive: true); } catch { }
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "Suspensión selectiva USB desactivada en CA y batería; se guardaron los índices previos del plan activo.");
            }

            PowerSettingSnapshot? originalState = await _backup.ReadOriginalAsync<PowerSettingSnapshot>(backupKey, cancellationToken);
            if (originalState is null) return new(false, "No existe respaldo del plan anterior; no se cambiará su configuración.");
            PowerSettingSnapshot active = ReadUsbSelectiveSuspend();
            bool restoreActivePlan = active.Scheme == originalState.Scheme;
            WriteUsbSelectiveSuspend(originalState.Scheme, originalState.AcValue, originalState.DcValue, restoreActivePlan);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, restoreActivePlan
                ? "Se restauraron los valores CA/batería originales del plan activo."
                : "Se restauraron los valores del plan usado al aplicar el ajuste; se respetó el plan que Windows tiene activo ahora.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar la suspensión selectiva USB: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetPcieAspmAsync(bool enabled, CancellationToken cancellationToken)
    {
        const string backupKey = @"Windows\Power\PcieAspm";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                PowerIndexSnapshot original = ReadPcieAspm();
                await _backup.SaveOriginalAsync(backupKey, original, cancellationToken);
                try { WritePowerSetting(original, 0, 0, refreshIfActive: true); }
                catch
                {
                    try { WritePowerSetting(original, original.AcValue, original.DcValue, refreshIfActive: true); } catch { }
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "PCI Express Link State Power Management desactivado en CA y batería; respaldo exacto guardado.");
            }

            PowerIndexSnapshot? originalState = await _backup.ReadOriginalAsync<PowerIndexSnapshot>(backupKey, cancellationToken);
            if (originalState is null) return new(false, "No existe respaldo de PCIe ASPM; no se cambiará la configuración del plan.");
            PowerIndexSnapshot active = ReadPcieAspm();
            bool restoreActivePlan = active.Scheme == originalState.Scheme;
            WritePowerSetting(originalState, originalState.AcValue, originalState.DcValue, restoreActivePlan);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, restoreActivePlan
                ? "Se restauraron los valores PCIe ASPM originales del plan activo."
                : "Se restauraron los valores del plan usado al aplicar el ajuste; se respetó el plan activo actual.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar PCIe ASPM: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static PowerSettingSnapshot ReadUsbSelectiveSuspend()
    {
        Guid scheme = GetActivePowerScheme();
        (uint acValue, uint dcValue) = ReadPowerSetting(scheme, UsbPowerSubgroup, UsbSelectiveSuspendSetting);
        return new(scheme, acValue, dcValue);
    }

    private static void WriteUsbSelectiveSuspend(Guid scheme, uint acValue, uint dcValue, bool refreshIfActive)
    {
        WritePowerSetting(new PowerIndexSnapshot(scheme, UsbPowerSubgroup, UsbSelectiveSuspendSetting, acValue, dcValue), acValue, dcValue, refreshIfActive);
    }

    private static PowerIndexSnapshot ReadPcieAspm()
    {
        Guid scheme = GetActivePowerScheme();
        (uint acValue, uint dcValue) = ReadPowerSetting(scheme, PcieExpressSubgroup, PcieAspmSetting);
        return new(scheme, PcieExpressSubgroup, PcieAspmSetting, acValue, dcValue);
    }

    private async Task<ExecutionResult> SetCpuPowerPlanAsync(bool enabled, CancellationToken cancellationToken)
    {
        const string backupKey = @"Windows\Power\CpuPowerPlan";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                CpuPowerSnapshot original = ReadCpuPowerPlan();
                await _backup.SaveOriginalAsync(backupKey, original, cancellationToken);
                try
                {
                    WriteCpuPowerValue(original.Scheme, CpuMinimumSetting, 100, original.MinimumDc);
                    WriteCpuPowerValue(original.Scheme, CpuMaximumSetting, 100, original.MaximumDc);
                    RefreshPowerPlan(original.Scheme);
                }
                catch
                {
                    try
                    {
                        WriteCpuPowerValue(original.Scheme, CpuMinimumSetting, original.MinimumAc, original.MinimumDc);
                        WriteCpuPowerValue(original.Scheme, CpuMaximumSetting, original.MaximumAc, original.MaximumDc);
                        RefreshPowerPlan(original.Scheme);
                    }
                    catch { }
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "El plan activo fija el estado mínimo y máximo de CPU en 100% con corriente alterna; batería sin cambios.");
            }

            CpuPowerSnapshot? originalState = await _backup.ReadOriginalAsync<CpuPowerSnapshot>(backupKey, cancellationToken);
            if (originalState is null) return new(false, "No existe respaldo del plan de energía anterior; no se cambiará su configuración.");
            bool restoreActivePlan = GetActivePowerScheme() == originalState.Scheme;
            WriteCpuPowerValue(originalState.Scheme, CpuMinimumSetting, originalState.MinimumAc, originalState.MinimumDc);
            WriteCpuPowerValue(originalState.Scheme, CpuMaximumSetting, originalState.MaximumAc, originalState.MaximumDc);
            if (restoreActivePlan) RefreshPowerPlan(originalState.Scheme);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, restoreActivePlan
                ? "Se restauraron los valores originales del plan activo."
                : "Se restauraron los valores del plan usado al aplicar el ajuste; se respetó el plan activo actual.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar el escalado de CPU: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static CpuPowerSnapshot ReadCpuPowerPlan()
    {
        Guid scheme = GetActivePowerScheme();
        (uint minAc, uint minDc) = ReadPowerSetting(scheme, ProcessorPowerSubgroup, CpuMinimumSetting);
        (uint maxAc, uint maxDc) = ReadPowerSetting(scheme, ProcessorPowerSubgroup, CpuMaximumSetting);
        return new(scheme, minAc, minDc, maxAc, maxDc);
    }

    private static void WriteCpuPowerValue(Guid scheme, Guid setting, uint acValue, uint dcValue)
    {
        Guid subgroup = ProcessorPowerSubgroup;
        uint status = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, acValue);
        ThrowPowerError(status, "No se pudo cambiar el índice CA del procesador.");
        status = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, dcValue);
        ThrowPowerError(status, "No se pudo cambiar el índice de batería del procesador.");
    }

    private static void RefreshPowerPlan(Guid scheme)
    {
        uint status = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        ThrowPowerError(status, "No se pudo actualizar el plan de energía activo.");
    }

    private async Task<ExecutionResult> SetDiskSleepPlanAsync(bool enabled, CancellationToken cancellationToken)
    {
        const string backupKey = @"Windows\Power\DiskSleep";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                DiskSleepSnapshot original = ReadDiskSleepPlan();
                await _backup.SaveOriginalAsync(backupKey, original, cancellationToken);
                try
                {
                    WritePowerSetting(new PowerIndexSnapshot(original.Scheme, DiskPowerSubgroup, DiskIdleSetting, original.DiskAc, original.DiskDc), 0, 0, false);
                    WritePowerSetting(new PowerIndexSnapshot(original.Scheme, SleepPowerSubgroup, HybridSleepSetting, original.HybridAc, original.HybridDc), 0, 0, false);
                    RefreshPowerPlan(original.Scheme);
                }
                catch
                {
                    try
                    {
                        WritePowerSetting(new PowerIndexSnapshot(original.Scheme, DiskPowerSubgroup, DiskIdleSetting, original.DiskAc, original.DiskDc), original.DiskAc, original.DiskDc, false);
                        WritePowerSetting(new PowerIndexSnapshot(original.Scheme, SleepPowerSubgroup, HybridSleepSetting, original.HybridAc, original.HybridDc), original.HybridAc, original.HybridDc, false);
                        RefreshPowerPlan(original.Scheme);
                    }
                    catch { }
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "Disco inactivo e hibernación híbrida desactivados en CA y batería; respaldo exacto guardado.");
            }

            DiskSleepSnapshot? originalState = await _backup.ReadOriginalAsync<DiskSleepSnapshot>(backupKey, cancellationToken);
            if (originalState is null) return new(false, "No existe respaldo del plan anterior; no se cambiará el apagado de disco ni la suspensión híbrida.");
            bool restoreActivePlan = GetActivePowerScheme() == originalState.Scheme;
            WritePowerSetting(new PowerIndexSnapshot(originalState.Scheme, DiskPowerSubgroup, DiskIdleSetting, originalState.DiskAc, originalState.DiskDc), originalState.DiskAc, originalState.DiskDc, false);
            WritePowerSetting(new PowerIndexSnapshot(originalState.Scheme, SleepPowerSubgroup, HybridSleepSetting, originalState.HybridAc, originalState.HybridDc), originalState.HybridAc, originalState.HybridDc, false);
            if (restoreActivePlan) RefreshPowerPlan(originalState.Scheme);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, restoreActivePlan
                ? "Se restauraron los valores originales de apagado de disco y suspensión híbrida."
                : "Se restauraron los valores del plan usado al aplicar el ajuste; se respetó el plan activo actual.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar el apagado de disco: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static DiskSleepSnapshot ReadDiskSleepPlan()
    {
        Guid scheme = GetActivePowerScheme();
        (uint diskAc, uint diskDc) = ReadPowerSetting(scheme, DiskPowerSubgroup, DiskIdleSetting);
        (uint hybridAc, uint hybridDc) = ReadPowerSetting(scheme, SleepPowerSubgroup, HybridSleepSetting);
        return new(scheme, diskAc, diskDc, hybridAc, hybridDc);
    }

    private async Task<ExecutionResult> SetStandbyIdleTimeoutAsync(bool enabled, CancellationToken cancellationToken)
    {
        const string backupKey = @"Windows\Power\StandbyIdleTimeout";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                PowerIndexSnapshot original = ReadStandbyIdleTimeout();
                await _backup.SaveOriginalAsync(backupKey, original, cancellationToken);
                try { WritePowerSetting(original, 0, 0, true); }
                catch
                {
                    try { WritePowerSetting(original, original.AcValue, original.DcValue, true); } catch { }
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "Suspensión automática por inactividad desactivada en CA y batería para el plan activo; suspensión manual intacta.");
            }

            PowerIndexSnapshot? originalState = await _backup.ReadOriginalAsync<PowerIndexSnapshot>(backupKey, cancellationToken);
            if (originalState is null) return new(false, "No existe respaldo del tiempo de suspensión anterior; no se cambiará el plan.");
            bool restoreActivePlan = GetActivePowerScheme() == originalState.Scheme;
            WritePowerSetting(originalState, originalState.AcValue, originalState.DcValue, restoreActivePlan);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, restoreActivePlan
                ? "Se restauró el tiempo de suspensión original del plan activo."
                : "Se restauró el tiempo del plan usado al aplicar el ajuste; se respetó el plan activo actual.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar el tiempo de suspensión: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static PowerIndexSnapshot ReadStandbyIdleTimeout()
    {
        Guid scheme = GetActivePowerScheme();
        (uint ac, uint dc) = ReadPowerSetting(scheme, SleepPowerSubgroup, StandbyIdleTimeoutSetting);
        return new(scheme, SleepPowerSubgroup, StandbyIdleTimeoutSetting, ac, dc);
    }

    private static Guid GetActivePowerScheme()
    {
        uint status = PowerGetActiveScheme(IntPtr.Zero, out IntPtr schemePointer);
        ThrowPowerError(status, "No se pudo consultar el plan de energía activo.");
        try { return Marshal.PtrToStructure<Guid>(schemePointer); }
        finally { LocalFree(schemePointer); }
    }

    private static (uint Ac, uint Dc) ReadPowerSetting(Guid scheme, Guid subgroup, Guid setting)
    {
        uint status = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint acValue);
        ThrowPowerError(status, "No se pudo leer el índice CA de la configuración del plan.");
        status = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out uint dcValue);
        ThrowPowerError(status, "No se pudo leer el índice de batería de la configuración del plan.");
        return (acValue, dcValue);
    }

    private static void WritePowerSetting(PowerIndexSnapshot original, uint acValue, uint dcValue, bool refreshIfActive)
    {
        Guid scheme = original.Scheme;
        Guid subgroup = original.Subgroup;
        Guid setting = original.Setting;
        uint status = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, acValue);
        ThrowPowerError(status, "No se pudo guardar el índice CA de la configuración del plan.");
        status = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, dcValue);
        ThrowPowerError(status, "No se pudo guardar el índice de batería de la configuración del plan.");
        if (refreshIfActive)
        {
            status = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            ThrowPowerError(status, "No se pudo activar de nuevo el plan modificado.");
        }
    }

    private static void ThrowPowerError(uint error, string message)
    {
        if (error != 0) throw new Win32Exception((int)error, message);
    }

    private static readonly Guid UsbPowerSubgroup = Guid.Parse("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid UsbSelectiveSuspendSetting = Guid.Parse("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    private static readonly Guid PcieExpressSubgroup = Guid.Parse("501a4d13-42af-4429-9fd1-a8218c268e20");
    private static readonly Guid PcieAspmSetting = Guid.Parse("ee12f906-d277-404b-b6da-e5fa1a576df5");
    private static readonly Guid ProcessorPowerSubgroup = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid CpuMinimumSetting = Guid.Parse("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid CpuMaximumSetting = Guid.Parse("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid DiskPowerSubgroup = Guid.Parse("0012ee47-9041-4b5d-9b77-535fba8b1442");
    private static readonly Guid DiskIdleSetting = Guid.Parse("6738e2c4-e8a5-4a42-b16a-e040e769756e");
    private static readonly Guid SleepPowerSubgroup = Guid.Parse("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid HybridSleepSetting = Guid.Parse("94ac6d29-73ce-41a6-809f-6363ba21b47e");
    private static readonly Guid StandbyIdleTimeoutSetting = Guid.Parse("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    private async Task<ExecutionResult> SetWindowsSearchAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Este ajuste requiere privilegios de administrador. Cierra la app y ábrela con clic derecho → Ejecutar como administrador.");
        const string backupKey = @"Windows\Services\WSearch";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                ServiceStateSnapshot original = await ReadWindowsSearchStateAsync(cancellationToken);
                if (original.CurrentState is not (ServiceStopped or ServiceRunning))
                    return new(false, "Windows Search está en transición; espera a que termine y vuelve a intentarlo.");
                await _backup.SaveOriginalAsync(backupKey, original, cancellationToken);
                try
                {
                    ChangeWindowsSearchStartType(ServiceDisabled);
                    if (original.CurrentState == ServiceRunning) await SetWindowsSearchRunningAsync(false, cancellationToken);
                }
                catch
                {
                    await RestoreWindowsSearchStateAsync(original, CancellationToken.None);
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "Windows Search detenido y deshabilitado; se guardaron su inicio y estado previos.");
            }

            ServiceStateSnapshot? originalState = await _backup.ReadOriginalAsync<ServiceStateSnapshot>(backupKey, cancellationToken);
            if (originalState is null) return new(false, "No existe respaldo anterior de Windows Search; no se cambiará el servicio.");
            await RestoreWindowsSearchStateAsync(originalState, cancellationToken);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauraron el tipo de inicio y el estado original de Windows Search.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar Windows Search: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetBiometricsAsync(OptimizationItem item, bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Este ajuste requiere privilegios de administrador. Cierra la app y ábrela con clic derecho → Ejecutar como administrador.");
        RegistryChange change = item.RegistryChanges[0];
        string registryBackupKey = MakeBackupKey(change);
        const string serviceBackupKey = @"Windows\Services\WbioSrvc";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                RegistryValueSnapshot originalRegistry = ReadSnapshot(change);
                ServiceStateSnapshot originalService = await ReadServiceStateAsync("WbioSrvc", cancellationToken);
                if (originalService.CurrentState is not (ServiceStopped or ServiceRunning))
                    return new(false, "Windows Biometric Service está en transición; espera a que termine y vuelve a intentarlo.");
                await _backup.SaveOriginalAsync(registryBackupKey, originalRegistry, cancellationToken);
                await _backup.SaveOriginalAsync(serviceBackupKey, originalService, cancellationToken);
                try
                {
                    WriteValue(change, change.ApplyValue);
                    ChangeServiceStartType("WbioSrvc", ServiceDisabled);
                    if (originalService.CurrentState == ServiceRunning)
                        await SetServiceRunningAsync("WbioSrvc", false, cancellationToken);
                }
                catch
                {
                    try { await RestoreServiceStateAsync("WbioSrvc", originalService, CancellationToken.None); } catch { }
                    try { RestoreValue(change, originalRegistry); } catch { }
                    await _backup.RemoveOriginalAsync(registryBackupKey, CancellationToken.None);
                    await _backup.RemoveOriginalAsync(serviceBackupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "Se desactivaron los gestos biométricos y Windows Biometric Service. El PIN o la contraseña siguen disponibles; se guardaron ambos estados originales.");
            }

            RegistryValueSnapshot? registryState = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(registryBackupKey, cancellationToken);
            ServiceStateSnapshot? serviceState = await _backup.ReadOriginalAsync<ServiceStateSnapshot>(serviceBackupKey, cancellationToken);
            if (registryState is null || serviceState is null)
                return new(false, "Falta uno de los respaldos de biometría; no se cambiará nada para evitar una reversión parcial.");
            try
            {
                RestoreValue(change, registryState);
                await RestoreServiceStateAsync("WbioSrvc", serviceState, cancellationToken);
            }
            catch
            {
                try { WriteValue(change, change.ApplyValue); } catch { }
                try
                {
                    ChangeServiceStartType("WbioSrvc", ServiceDisabled);
                    await SetServiceRunningAsync("WbioSrvc", false, CancellationToken.None);
                }
                catch { }
                throw;
            }
            await _backup.RemoveOriginalAsync(registryBackupKey, cancellationToken);
            await _backup.RemoveOriginalAsync(serviceBackupKey, cancellationToken);
            return new(true, "Se restauraron el valor de la política y el tipo/estado de inicio originales de Windows Biometric Service.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException or Win32Exception or TimeoutException)
        {
            return new(false, $"No se pudo cambiar la biometría: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> IsBiometricsDisabledAsync(OptimizationItem item, CancellationToken cancellationToken)
    {
        RegistryChange change = item.RegistryChanges[0];
        using RegistryKey? key = OpenKey(change, writable: false);
        bool policyDisabled = key is not null && key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase) &&
            ValueMatches(key.GetValueKind(change.ValueName), key.GetValue(change.ValueName), change.ApplyValue);
        if (!policyDisabled) return false;
        ServiceStateSnapshot service = await ReadServiceStateAsync("WbioSrvc", cancellationToken);
        return service.StartType == ServiceDisabled && service.CurrentState == ServiceStopped;
    }

    private static Task<ServiceStateSnapshot> ReadServiceStateAsync(string serviceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw LastWin32("No se pudo consultar el Administrador de servicios.");
        try
        {
            IntPtr service = OpenServiceW(manager, serviceName, ServiceQueryConfig | ServiceQueryStatus);
            if (service == IntPtr.Zero) throw LastWin32($"No se pudo consultar el servicio {serviceName}.");
            try
            {
                IntPtr configBuffer = Marshal.AllocHGlobal(4096);
                try
                {
                    if (!QueryServiceConfigW(service, configBuffer, 4096, out _)) throw LastWin32($"No se pudo leer el tipo de inicio de {serviceName}.");
                    QueryServiceConfig config = Marshal.PtrToStructure<QueryServiceConfig>(configBuffer);
                    ServiceStatusProcess status = QueryWindowsSearchStatus(service);
                    return Task.FromResult(new ServiceStateSnapshot(config.StartType, status.CurrentState));
                }
                finally { Marshal.FreeHGlobal(configBuffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static async Task RestoreServiceStateAsync(string serviceName, ServiceStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.CurrentState is not (ServiceStopped or ServiceRunning))
            throw new InvalidOperationException($"El estado guardado de {serviceName} no se puede restaurar de forma segura.");
        ChangeServiceStartType(serviceName, snapshot.StartType);
        ServiceStatusProcess current = QueryServiceStatusByName(serviceName);
        if (snapshot.CurrentState == ServiceRunning && current.CurrentState != ServiceRunning)
            await SetServiceRunningAsync(serviceName, true, cancellationToken);
        else if (snapshot.CurrentState == ServiceStopped && current.CurrentState != ServiceStopped)
            await SetServiceRunningAsync(serviceName, false, cancellationToken);
    }

    private static async Task SetServiceRunningAsync(string serviceName, bool running, CancellationToken cancellationToken)
    {
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw LastWin32("No se pudo abrir el Administrador de servicios.");
        try
        {
            IntPtr service = OpenServiceW(manager, serviceName, ServiceQueryStatus | (running ? ServiceStart : ServiceStop));
            if (service == IntPtr.Zero) throw LastWin32($"No se pudo abrir {serviceName} para cambiar su estado.");
            try
            {
                ServiceStatusProcess status = QueryWindowsSearchStatus(service);
                uint expectedState = running ? ServiceRunning : ServiceStopped;
                if (status.CurrentState == expectedState) return;
                if (running)
                {
                    if (!StartServiceW(service, 0, IntPtr.Zero)) throw LastWin32($"No se pudo iniciar {serviceName}.");
                }
                else
                {
                    ServiceStatus basic = default;
                    if (!ControlService(service, ServiceControlStop, ref basic) && Marshal.GetLastWin32Error() != ErrorServiceNotActive)
                        throw LastWin32($"No se pudo detener {serviceName}.");
                }
                for (int attempt = 0; attempt < 120; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    status = QueryWindowsSearchStatus(service);
                    if (status.CurrentState == expectedState) return;
                    if (status.CurrentState is not (ServiceStopped or ServiceRunning))
                    {
                        await Task.Delay(250, cancellationToken);
                        continue;
                    }
                    throw new InvalidOperationException($"{serviceName} terminó en un estado inesperado.");
                }
                throw new TimeoutException($"{serviceName} no terminó de cambiar de estado en 30 segundos.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static void ChangeServiceStartType(string serviceName, uint startType)
    {
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw LastWin32("No se pudo abrir el Administrador de servicios.");
        try
        {
            IntPtr service = OpenServiceW(manager, serviceName, ServiceChangeConfig);
            if (service == IntPtr.Zero) throw LastWin32($"No se pudo abrir {serviceName} para cambiar el inicio.");
            try
            {
                if (!ChangeServiceConfigW(service, ServiceNoChange, startType, ServiceNoChange, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                    throw LastWin32($"No se pudo cambiar el tipo de inicio de {serviceName}.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static ServiceStatusProcess QueryServiceStatusByName(string serviceName)
    {
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw LastWin32("No se pudo consultar el Administrador de servicios.");
        try
        {
            IntPtr service = OpenServiceW(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) throw LastWin32($"No se pudo consultar {serviceName}.");
            try { return QueryWindowsSearchStatus(service); }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static Task<ServiceStateSnapshot> ReadWindowsSearchStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw LastWin32("No se pudo consultar el Administrador de servicios.");
        try
        {
            IntPtr service = OpenServiceW(manager, "WSearch", ServiceQueryConfig | ServiceQueryStatus);
            if (service == IntPtr.Zero) throw LastWin32("No se pudo consultar el servicio Windows Search (WSearch).");
            try
            {
                IntPtr configBuffer = Marshal.AllocHGlobal(4096);
                try
                {
                    if (!QueryServiceConfigW(service, configBuffer, 4096, out _)) throw LastWin32("No se pudo leer el tipo de inicio de WSearch.");
                    QueryServiceConfig config = Marshal.PtrToStructure<QueryServiceConfig>(configBuffer);
                    ServiceStatusProcess status = QueryWindowsSearchStatus(service);
                    return Task.FromResult(new ServiceStateSnapshot(config.StartType, status.CurrentState));
                }
                finally { Marshal.FreeHGlobal(configBuffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static async Task RestoreWindowsSearchStateAsync(ServiceStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.CurrentState is not (ServiceStopped or ServiceRunning))
            throw new InvalidOperationException("El estado guardado de WSearch no se puede restaurar de forma segura.");
        ChangeWindowsSearchStartType(snapshot.StartType);
        ServiceStatusProcess current = QueryWindowsSearchStatusByName();
        if (snapshot.CurrentState == ServiceRunning && current.CurrentState != ServiceRunning)
            await SetWindowsSearchRunningAsync(true, cancellationToken);
        else if (snapshot.CurrentState == ServiceStopped && current.CurrentState != ServiceStopped)
            await SetWindowsSearchRunningAsync(false, cancellationToken);
    }

    private static async Task SetWindowsSearchRunningAsync(bool running, CancellationToken cancellationToken)
    {
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw LastWin32("No se pudo abrir el Administrador de servicios.");
        try
        {
            IntPtr service = OpenServiceW(manager, "WSearch", ServiceQueryStatus | (running ? ServiceStart : ServiceStop));
            if (service == IntPtr.Zero) throw LastWin32("No se pudo abrir WSearch para cambiar su estado.");
            try
            {
                ServiceStatusProcess status = QueryWindowsSearchStatus(service);
                uint expectedState = running ? ServiceRunning : ServiceStopped;
                if (status.CurrentState == expectedState) return;
                if (running)
                {
                    if (!StartServiceW(service, 0, IntPtr.Zero)) throw LastWin32("No se pudo iniciar WSearch.");
                }
                else
                {
                    ServiceStatus basic = default;
                    if (!ControlService(service, ServiceControlStop, ref basic) && Marshal.GetLastWin32Error() != ErrorServiceNotActive)
                        throw LastWin32("No se pudo detener WSearch.");
                }
                for (int attempt = 0; attempt < 120; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    status = QueryWindowsSearchStatus(service);
                    if (status.CurrentState == expectedState) return;
                    await Task.Delay(250, cancellationToken);
                }
                throw new TimeoutException("WSearch no terminó de cambiar de estado en 30 segundos.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static void ChangeWindowsSearchStartType(uint startType)
    {
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw LastWin32("No se pudo abrir el Administrador de servicios.");
        try
        {
            IntPtr service = OpenServiceW(manager, "WSearch", ServiceChangeConfig);
            if (service == IntPtr.Zero) throw LastWin32("No se pudo abrir WSearch para cambiar el inicio.");
            try
            {
                if (!ChangeServiceConfigW(service, ServiceNoChange, startType, ServiceNoChange, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                    throw LastWin32("No se pudo cambiar el tipo de inicio de WSearch.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static ServiceStatusProcess QueryWindowsSearchStatusByName()
    {
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw LastWin32("No se pudo consultar el Administrador de servicios.");
        try
        {
            IntPtr service = OpenServiceW(manager, "WSearch", ServiceQueryStatus);
            if (service == IntPtr.Zero) throw LastWin32("No se pudo consultar WSearch.");
            try { return QueryWindowsSearchStatus(service); }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static ServiceStatusProcess QueryWindowsSearchStatus(IntPtr service)
    {
        int size = Marshal.SizeOf<ServiceStatusProcess>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, 0, buffer, (uint)size, out _)) throw LastWin32("No se pudo leer el estado de WSearch.");
            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static Win32Exception LastWin32(string message) => new(Marshal.GetLastWin32Error(), message);

    private async Task<ExecutionResult> SetPageCombiningAsync(OptimizationItem item, bool enabled, CancellationToken cancellationToken)
    {
        RegistryChange change = item.RegistryChanges[0];
        string registryBackupKey = MakeBackupKey(change);
        const string commandBackupKey = @"Windows\MMAgent\PageCombining";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                bool originalCommandState = await ReadPageCombiningAsync(cancellationToken);
                RegistryValueSnapshot originalRegistryState = ReadSnapshot(change);
                await _backup.SaveOriginalAsync(registryBackupKey, originalRegistryState, cancellationToken);
                await _backup.SaveOriginalAsync(commandBackupKey, originalCommandState, cancellationToken);
                try
                {
                    WriteValue(change, change.ApplyValue);
                    await SetPageCombiningStateAsync(false, cancellationToken);
                }
                catch
                {
                    RestoreValue(change, originalRegistryState);
                    await SetPageCombiningStateAsync(originalCommandState, CancellationToken.None);
                    await _backup.RemoveOriginalAsync(registryBackupKey, CancellationToken.None);
                    await _backup.RemoveOriginalAsync(commandBackupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "Page Combining desactivado; se respaldaron el Registro y el estado de Windows.");
            }

            RegistryValueSnapshot? registryState = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(registryBackupKey, cancellationToken);
            bool? commandState = await _backup.ReadOriginalAsync<bool?>(commandBackupKey, cancellationToken);
            if (registryState is null || commandState is null)
                return new(false, "Falta el respaldo completo; no se modificó el estado para evitar una reversión parcial.");
            await SetPageCombiningStateAsync(commandState.Value, cancellationToken);
            RestoreValue(change, registryState);
            await _backup.RemoveOriginalAsync(registryBackupKey, cancellationToken);
            await _backup.RemoveOriginalAsync(commandBackupKey, cancellationToken);
            return new(true, "Se restauraron los valores originales de Page Combining.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar Page Combining: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> ReadPageCombiningAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellAsync("$ErrorActionPreference='Stop'; (Get-MMAgent).PageCombining", cancellationToken);
        string state = output.Trim();
        if (state.Equals("True", StringComparison.OrdinalIgnoreCase)) return true;
        if (state.Equals("False", StringComparison.OrdinalIgnoreCase)) return false;
        throw new InvalidOperationException("Windows no devolvió el estado de Page Combining.");
    }

    private static Task SetPageCombiningStateAsync(bool enabled, CancellationToken cancellationToken)
    {
        string command = enabled
            ? "$ErrorActionPreference='Stop'; Enable-MMAgent -PageCombining"
            : "$ErrorActionPreference='Stop'; Disable-MMAgent -PageCombining";
        return RunPowerShellAsync(command, cancellationToken);
    }

    private async Task<ExecutionResult> SetMemoryCompressionAsync(bool enabled, bool hardwareApplicable, CancellationToken cancellationToken)
    {
        if (enabled && !hardwareApplicable) return new(false, "Este ajuste solo se puede activar con 16 GB de RAM o más.");
        if (!IsAdministrator()) return new(false, "Este ajuste requiere privilegios de administrador. Cierra la app y ábrela con clic derecho → Ejecutar como administrador.");
        const string backupKey = @"Windows\MMAgent\MemoryCompression";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                bool original = await ReadMemoryCompressionAsync(cancellationToken);
                await _backup.SaveOriginalAsync(backupKey, original, cancellationToken);
                await SetMemoryCompressionStateAsync(false, cancellationToken);
                return new(true, "Compresión de memoria desactivada; se guardó el estado previo.");
            }

            bool? snapshot = await _backup.ReadOriginalAsync<bool?>(backupKey, cancellationToken);
            if (snapshot is null) return new(false, "No existe respaldo anterior; no se cambiará el estado actual.");
            await SetMemoryCompressionStateAsync(snapshot.Value, cancellationToken);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauró el estado original de compresión de memoria.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar la compresión de memoria: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> ReadMemoryCompressionAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellAsync("$ErrorActionPreference='Stop'; (Get-MMAgent).MemoryCompression", cancellationToken);
        string state = output.Trim();
        if (state.Equals("True", StringComparison.OrdinalIgnoreCase)) return true;
        if (state.Equals("False", StringComparison.OrdinalIgnoreCase)) return false;
        throw new InvalidOperationException("Windows no devolvió el estado de Memory Compression.");
    }

    private static async Task SetMemoryCompressionStateAsync(bool enabled, CancellationToken cancellationToken)
    {
        string command = enabled
            ? "$ErrorActionPreference='Stop'; Enable-MMAgent -MemoryCompression"
            : "$ErrorActionPreference='Stop'; Disable-MMAgent -MemoryCompression";
        await RunPowerShellAsync(command, cancellationToken);
    }

    private static async Task<string> RunPowerShellAsync(string command, CancellationToken cancellationToken)
    {
        string powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("No se pudo iniciar Windows PowerShell.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await stdout;
        string error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"PowerShell terminó con código {process.ExitCode}." : error.Trim());
        return output;
    }

    private static string MakeBackupKey(RegistryChange change) =>
        $"{change.Hive}\\{change.KeyPath}\\{change.ValueName}";

    private static RegistryKey? OpenKey(RegistryChange change, bool writable) =>
        change.Hive switch
        {
            "HKCU" => Registry.CurrentUser.OpenSubKey(change.KeyPath, writable),
            "HKLM" => Registry.LocalMachine.OpenSubKey(change.KeyPath, writable),
            "HKCR" => Registry.ClassesRoot.OpenSubKey(change.KeyPath, writable),
            _ => null
        };

    private static RegistryValueSnapshot ReadSnapshot(RegistryChange change)
    {
        using RegistryKey? key = OpenKey(change, writable: false);
        if (key is null || !key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase))
            return new(false, change.ValueKind, null);
        object? value = key.GetValue(change.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        RegistryValueKind kind = key.GetValueKind(change.ValueName);
        string? serialized = value is null ? null : JsonSerializer.Serialize(value, value.GetType());
        return new(true, kind.ToString(), serialized);
    }

    private static void WriteValue(RegistryChange change, string data)
    {
        using RegistryKey key = GetRegistryRoot(change.Hive).CreateSubKey(change.KeyPath, writable: true)
            ?? throw new UnauthorizedAccessException("No se pudo abrir la clave de Registro.");
        RegistryValueKind kind = Enum.Parse<RegistryValueKind>(change.ValueKind, ignoreCase: true);
        key.SetValue(change.ValueName, ParseInputValue(kind, data), kind);
    }

    private static void RestoreValue(RegistryChange change, RegistryValueSnapshot snapshot)
    {
        if (!snapshot.Exists)
        {
            using RegistryKey? existingKey = OpenKey(change, writable: true);
            existingKey?.DeleteValue(change.ValueName, throwOnMissingValue: false);
            return;
        }
        using RegistryKey key = GetRegistryRoot(change.Hive).CreateSubKey(change.KeyPath, writable: true)
            ?? throw new UnauthorizedAccessException("No se pudo abrir la clave de Registro.");
        RegistryValueKind kind = Enum.Parse<RegistryValueKind>(snapshot.Kind, ignoreCase: true);
        using JsonDocument document = JsonDocument.Parse(snapshot.SerializedData ?? "null");
        JsonElement data = document.RootElement;
        object value = kind switch
        {
            RegistryValueKind.DWord => data.TryGetInt32(out int i32) ? i32 : unchecked((int)data.GetUInt32()),
            RegistryValueKind.QWord => data.TryGetInt64(out long i64) ? i64 : unchecked((long)data.GetUInt64()),
            RegistryValueKind.Binary or RegistryValueKind.None => data.GetBytesFromBase64(),
            RegistryValueKind.MultiString => data.Deserialize<string[]>() ?? [],
            RegistryValueKind.String or RegistryValueKind.ExpandString => data.GetString() ?? "",
            _ => throw new InvalidOperationException($"Tipo de Registro no compatible para restaurar: {kind}.")
        };
        key.SetValue(change.ValueName, value, kind);
    }

    private static void ApplyRegistryChange(RegistryChange change, string? valueOverride = null)
    {
        if (valueOverride == "__DELETE__")
        {
            using RegistryKey? deleteKey = OpenKey(change, writable: true);
            deleteKey?.DeleteValue(change.ValueName, throwOnMissingValue: false);
            return;
        }
        if (!change.DeleteOnApply)
        {
            WriteValue(change, valueOverride ?? change.ApplyValue);
            return;
        }
        using RegistryKey? key = OpenKey(change, writable: true);
        key?.DeleteValue(change.ValueName, throwOnMissingValue: false);
    }

    private static RegistryKey GetRegistryRoot(string hive) => hive switch
    {
        "HKCU" => Registry.CurrentUser,
        "HKLM" => Registry.LocalMachine,
        "HKCR" => Registry.ClassesRoot,
        _ => throw new InvalidOperationException("Hive no permitido.")
    };

    private static int ParseDWordValue(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return unchecked((int)Convert.ToUInt32(s[2..], 16));
        if (int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int signed))
            return signed;
        if (uint.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out uint unsigned))
            return unchecked((int)unsigned);
        throw new FormatException($"Valor DWord no válido: '{raw}'.");
    }

    private static long ParseQWordValue(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return unchecked((long)Convert.ToUInt64(s[2..], 16));
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long signed))
            return signed;
        if (ulong.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ulong unsigned))
            return unchecked((long)unsigned);
        throw new FormatException($"Valor QWord no válido: '{raw}'.");
    }

    private static object ParseInputValue(RegistryValueKind kind, string value) => kind switch
    {
        RegistryValueKind.DWord => ParseDWordValue(value),
        RegistryValueKind.QWord => ParseQWordValue(value),
        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(value),
        RegistryValueKind.MultiString => value.Split('|', StringSplitOptions.RemoveEmptyEntries),
        RegistryValueKind.String or RegistryValueKind.ExpandString => value,
        _ => throw new InvalidOperationException($"Tipo de Registro no compatible para escribir: {kind}.")
    };

    private static string SerializeInputValue(string kind, string value) =>
        JsonSerializer.Serialize(ParseInputValue(Enum.Parse<RegistryValueKind>(kind, ignoreCase: true), value));

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceDisabled = 0x00000004;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const int ErrorServiceNotActive = 1062;

    [DllImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme")]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerReadACValueIndex")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, out uint valueIndex);

    [DllImport("powrprof.dll", EntryPoint = "PowerReadDCValueIndex")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, out uint valueIndex);

    [DllImport("powrprof.dll", EntryPoint = "PowerWriteACValueIndex")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, uint valueIndex);

    [DllImport("powrprof.dll", EntryPoint = "PowerWriteDCValueIndex")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, uint valueIndex);

    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveScheme")]
    private static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QueryServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

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

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(IntPtr service, IntPtr config, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl,
        IntPtr binaryPathName, IntPtr loadOrderGroup, IntPtr tagId, IntPtr dependencies, IntPtr serviceStartName, IntPtr password, IntPtr displayName);

    [DllImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, uint argumentCount, IntPtr arguments);
}
