using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ApexKernelOptimizer.Core.Daemon;

/// <summary>
/// Demonio nativo en tiempo real de ApexKernel (equivalente ligero a Process Lasso):
/// ProBalance por reglas, afinidad inteligente, EcoQoS off, pausa de RGB, purge de standby,
/// smart trim, resolución del temporizador y cambio automático de plan de energía.
/// Lee su configuración de %AppData%\ApexKernel\daemon_config.json en cada ciclo.
/// </summary>
public sealed class ProcessLassoNativeService : IDisposable
{
    private const int ProBalanceIntervalMs = 500;
    private const int MemoryIntervalMs = 30_000;
    private const int TrimIntervalMs = 60_000;

    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;
    private readonly object _loopGate = new();

    // Listas blancas: nunca se tocan con pausas, trims o degradaciones de prioridad.
    private static readonly HashSet<string> SystemWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg", "dwm", "csrss", "lsass", "svchost", "winlogon", "smss", "services", "system", "wininit",
        "explorer", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "dllhost", "conhost", "runtimebroker",
        "apexkerneloptimizer", "msmpeng", "securityhealthservice", "searchindexer", "ntoskrnl", "memcompression"
    };

    // Apps RGB / overlays pesados que se pausan al jugar (regla #27).
    private static readonly string[] RgbProcessNames =
    [
        "openrgb", "icue", "logioptionsplus", "lghub", "lghub_agent", "steelseriesgg", "rogcore",
        "armoury_socket_server", "nzxtcam", "rzsynapse", "ryzenmaster"
    ];

    private static readonly string[] BackgroundHeavyHints =
    [
        "onedrive", "teams", "slack", "discord", "spotify", "chrome", "msedge", "firefox", "brave",
        "photoshop", "premiere", "excel", "winword", "powerpnt", "outlook", "steamwebhelper", "epicgames",
        "battle.net", "galaxyclient", "upc", "ubisoft", "origin", "wallpaper32", "wallpaper64", "spotify"
    ];

    private uint _timerResolutionActive;
    private bool _standbyPrivilegeEnabled;
    private Guid _autoPlanBackupScheme;
    private DateTime _lastSmartTrimUtc = DateTime.MinValue;

    public void Start()
    {
        lock (_loopGate)
        {
            if (_loop is not null) return;
            _loop = Task.Run(() => RunAsync(_cancellation.Token));
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _loop?.Wait(1000); } catch { }

        // 1. Restaurar resolución del temporizador del kernel.
        if (_timerResolutionActive != 0)
        {
            try { NtSetTimerResolution(_timerResolutionActive, false, out _); } catch { }
            _timerResolutionActive = 0;
        }

        // 2. Reanudar apps RGB/overlay que el demonio hubiera suspendido.
        ResumeAllRgbOnExit();

        // 3. Restaurar el plan de energía original si el cambio automático estaba activo.
        if (_autoPlanBackupScheme != Guid.Empty)
        {
            try { SetActiveScheme(_autoPlanBackupScheme); } catch { }
            _autoPlanBackupScheme = Guid.Empty;
        }

        _cancellation.Dispose();
    }

    private static void ResumeAllRgbOnExit()
    {
        foreach (string rgbName in RgbProcessNames)
        {
            foreach (Process process in Process.GetProcessesByName(rgbName))
            {
                try
                {
                    if (!process.HasExited && IsProcessSuspended(process)) ResumeProcess(process);
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        long lastMemory = 0;
        long lastTrim = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                DaemonConfigFile config = DaemonConfigStore.Load();
                ApplyTimerResolution(config);
                Process? game = FindRuleGameProcess(config);
                try
                {
                    if (game is not null) ApplyGameBehaviors(game, config);
                    else ApplyForegroundBehaviors(config); // sin reglas: ajusta el proceso en primer plano
                    if (config.Flags.TryGetValue("25", out bool proBalance) && proBalance) ApplyProBalance(game, config);
                    if (config.Flags.TryGetValue("26", out bool lowBg) && lowBg) ApplyBackgroundLowPriority(game, config);
                    if (config.Flags.TryGetValue("27", out bool pauseRgb) && pauseRgb) ApplyRgbPause(game, config);
                    if (config.Flags.TryGetValue("38", out bool autoPlan) && autoPlan) ApplyAutoPowerPlan(game is not null, cancellationToken);

                    long now = Environment.TickCount64;
                    if (now - lastMemory > MemoryIntervalMs)
                    {
                        lastMemory = now;
                        if (config.Flags.TryGetValue("29", out bool standby) && standby) PurgeStandbyIfFree(config);
                        if (config.Flags.TryGetValue("30", out bool smartTrim) && smartTrim) SmartTrim(game, config);
                    }
                    if (now - lastTrim > TrimIntervalMs)
                    {
                        lastTrim = now;
                        if (config.Flags.TryGetValue("31", out bool timer) && timer) RefreshTimerResolution();
                    }
                }
                finally
                {
                    game?.Dispose(); // se libera al final del ciclo, nunca antes de aplicar las reglas
                }
            }
            catch { }
            await SafeDelay(ProBalanceIntervalMs, cancellationToken);
        }
    }

    private static async Task SafeDelay(int milliseconds, CancellationToken cancellationToken)
    {
        try { await Task.Delay(milliseconds, cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private static Process? FindRuleGameProcess(DaemonConfigFile config)
    {
        var rules = config.Rules;
        if (rules.Count == 0) return null;
        var running = Process.GetProcesses();
        Process? matched = null;
        try
        {
            foreach (DaemonRule rule in rules)
            {
                string target = rule.Exe.Trim().Trim('"');
                if (target.Length == 0) continue;
                string baseName = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target[..^4] : target;
                foreach (Process process in running)
                {
                    try
                    {
                        if (!string.Equals(process.ProcessName, baseName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (process.HasExited) continue;
                        matched = process;
                        return process;
                    }
                    catch { }
                }
            }
            return null;
        }
        finally
        {
            foreach (Process process in running)
            {
                if (ReferenceEquals(process, matched)) continue; // el proceso devuelto se libera en RunAsync
                try { process.Dispose(); } catch { }
            }
        }
    }

    private static void ApplyGameBehaviors(Process game, DaemonConfigFile config)
    {
        DaemonRule? rule = config.Rules.FirstOrDefault(entry => MatchesRule(entry, game));
        if (rule is null) return;
        try
        {
            if (rule.ExcludeCore0) ApplyAffinityExcludingCore0(game);
            else if (rule.PhysicalCoresOnly) ApplyAffinityPhysicalOnly(game);
            if (rule.HighPriority && game.PriorityClass != ProcessPriorityClass.High && game.PriorityClass != ProcessPriorityClass.RealTime)
                game.PriorityClass = ProcessPriorityClass.High;
            else if (rule.LowPriority && game.PriorityClass != ProcessPriorityClass.BelowNormal)
                game.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch { }
        if (config.Flags.TryGetValue("23", out bool ecoOff) && ecoOff) DisableEcoQoS(game);
        if (config.Flags.TryGetValue("24", out bool boost) && boost) EnablePriorityBoost(game);
        if (config.Flags.TryGetValue("28", out bool largePages) && largePages) GrantLockPagesPrivilege(game);
    }

    private static bool HasActiveGameAdjustment(DaemonConfigFile config)
    {
        foreach (string id in new[] { "21", "22", "23", "24", "28" })
        {
            if (config.Flags.TryGetValue(id, out bool value) && value) return true;
        }
        return false;
    }

    private static void ApplyForegroundBehaviors(DaemonConfigFile config)
    {
        if (!HasActiveGameAdjustment(config)) return;
        if (config.Rules.Count != 0) return; // solo si el usuario aún no añadió reglas manuales
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0) return;
            using Process process = Process.GetProcessById((int)processId);
            if (process.HasExited) return;
            if (SystemWhitelist.Contains(process.ProcessName)) return;
            if (config.Flags.TryGetValue("23", out bool ecoOff) && ecoOff) DisableEcoQoS(process);
            if (config.Flags.TryGetValue("24", out bool boost) && boost) EnablePriorityBoost(process);
        }
        catch { }
    }

    private static void EnablePriorityBoost(Process process)
    {
        try { process.PriorityBoostEnabled = true; }
        catch { }
    }

    private static bool MatchesRule(DaemonRule rule, Process game)
    {
        string target = rule.Exe.Trim().Trim('"');
        if (target.Length == 0) return false;
        string baseName = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target[..^4] : target;
        return string.Equals(game.ProcessName, baseName, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyAffinityExcludingCore0(Process game)
    {
        try
        {
            int logicals = Environment.ProcessorCount;
            if (logicals <= 2) return;
            nint current = GetProcessAffinityMask(game);
            nint mask = current & ~((nint)3); // excluye los dos primeros hilos (núcleo 0 + su SMT)
            if (mask != 0 && mask != current) game.ProcessorAffinity = (IntPtr)mask;
        }
        catch { }
    }

    private static void ApplyAffinityPhysicalOnly(Process game)
    {
        try
        {
            int logicals = Environment.ProcessorCount;
            // SMT típico 2: conserva los primeros N/2 procesadores lógicos (núcleos físicos bajos).
            if (logicals <= 2) return;
            nint current = GetProcessAffinityMask(game);
            nint physicalMask = current & (((nint)1 << (logicals / 2)) - 1);
            if (physicalMask != 0 && physicalMask != current) game.ProcessorAffinity = (IntPtr)physicalMask;
        }
        catch { }
    }

    private static nint GetProcessAffinityMask(Process game)
    {
        try { return (nint)game.ProcessorAffinity.ToInt64(); }
        catch { return 0; }
    }

    private static void DisableEcoQoS(Process game)
    {
        try
        {
            var info = new PowerThrottling { Version = 1, ControlMask = 1, StateMask = 0 };
            SetProcessInformation(game.Handle, ProcessInformationClassProcessPowerThrottling, ref info, (uint)Marshal.SizeOf<PowerThrottling>());
        }
        catch { }
    }

    private static void GrantLockPagesPrivilege(Process game)
    {
        try
        {
            if (!OpenProcessToken(game.Handle, TokenAdjustPrivileges | TokenQuery, out IntPtr token)) return;
            try
            {
                if (!LookupPrivilegeValue(null, "SeLockMemoryPrivilege", out Luid luid)) return;
                var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = 2 /*SE_PRIVILEGE_ENABLED*/ };
                AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, out _);
            }
            finally { CloseHandle(token); }
        }
        catch { }
    }

    private static void ApplyProBalance(Process? game, DaemonConfigFile config)
    {
        if (game is null) return;
        try
        {
            DaemonRule? rule = config.Rules.FirstOrDefault(entry => MatchesRule(entry, game));
            if (rule is not null && (rule.HighPriority || rule.ExcludeCore0 || rule.PhysicalCoresOnly)) return; // la regla manda
            if (game.PriorityClass != ProcessPriorityClass.High) game.PriorityClass = ProcessPriorityClass.High;
        }
        catch { }
    }

    private static void ApplyBackgroundLowPriority(Process? game, DaemonConfigFile config)
    {
        List<Process> heavy = EnumerateBackgroundHeavyProcesses(game);
        try
        {
            foreach (Process process in heavy)
            {
                try
                {
                    if (process.PriorityClass == ProcessPriorityClass.Idle) continue;
                    process.PriorityClass = ProcessPriorityClass.Idle;
                    ApplyAffinityExcludingCore0(process);
                }
                catch { }
            }
        }
        finally
        {
            foreach (Process process in heavy) { try { process.Dispose(); } catch { } }
        }
    }

    private static List<Process> EnumerateBackgroundHeavyProcesses(Process? game)
    {
        var results = new List<Process>();
        foreach (Process process in Process.GetProcesses())
        {
            bool keep = false;
            try
            {
                keep = !process.HasExited &&
                    !SystemWhitelist.Contains(process.ProcessName) &&
                    (game is null || process.Id != game.Id) &&
                    process.WorkingSet64 >= 200L * 1024 * 1024 &&
                    BackgroundHeavyHints.Any(hint => process.ProcessName.Contains(hint, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            if (keep) results.Add(process); else { try { process.Dispose(); } catch { } }
        }
        return results;
    }

    private static void ApplyRgbPause(Process? game, DaemonConfigFile config)
    {
        bool gameRunning = game is not null;
        foreach (string rgbName in RgbProcessNames)
        {
            foreach (Process process in Process.GetProcessesByName(rgbName))
            {
                try
                {
                    if (process.HasExited || SystemWhitelist.Contains(process.ProcessName)) continue;
                    if (gameRunning)
                    {
                        if (!IsProcessSuspended(process)) SuspendProcess(process);
                    }
                    else if (IsProcessSuspended(process)) ResumeProcess(process);
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
        }
    }

    private void ApplyAutoPowerPlan(bool gameRunning, CancellationToken cancellationToken)
    {
        try
        {
            if (gameRunning)
            {
                if (_autoPlanBackupScheme != Guid.Empty) return;
                _autoPlanBackupScheme = GetActiveScheme();
                Guid competitive = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
                SetActiveScheme(competitive);
            }
            else if (_autoPlanBackupScheme != Guid.Empty)
            {
                SetActiveScheme(_autoPlanBackupScheme);
                _autoPlanBackupScheme = Guid.Empty;
            }
        }
        catch { _autoPlanBackupScheme = Guid.Empty; }
    }

    private void ApplyTimerResolution(DaemonConfigFile config)
    {
        try
        {
            bool enabled = config.Flags.TryGetValue("31", out bool timer) && timer;
            if (enabled && _timerResolutionActive == 0)
            {
                if (NtSetTimerResolution(5000, true, out uint current) == 0) _timerResolutionActive = current;
            }
            else if (!enabled && _timerResolutionActive != 0)
            {
                NtSetTimerResolution(_timerResolutionActive, false, out _);
                _timerResolutionActive = 0;
            }
        }
        catch { }
    }

    private void RefreshTimerResolution()
    {
        try
        {
            if (_timerResolutionActive != 0)
            {
                NtSetTimerResolution(_timerResolutionActive, false, out _);
                NtSetTimerResolution(5000, true, out _);
            }
        }
        catch { }
    }

    private void PurgeStandbyIfFree(DaemonConfigFile config)
    {
        try
        {
            if (!_standbyPrivilegeEnabled)
            {
                EnableOwnPrivilege("SeProfileSingleProcessPrivilege");
                _standbyPrivilegeEnabled = true;
            }
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            GlobalMemoryStatusEx(ref memory);
            long available = (long)(memory.AvailablePhysical / (1024 * 1024));
            long thresholdMb = Math.Max(2048L, (long)(memory.TotalPhysical / (1024 * 1024) / 3));
            if (available > thresholdMb) return; // purga solo cuando escasea la RAM disponible
            int command = 4; // MemoryPurgeStandbyList
            NtSetSystemInformation(80 /*SystemMemoryListInformation*/, ref command, sizeof(int));
        }
        catch { }
    }

    private void SmartTrim(Process? game, DaemonConfigFile config)
    {
        if (DateTime.UtcNow - _lastSmartTrimUtc < TimeSpan.FromMinutes(5)) return;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);
        if (memory.MemoryLoad < 80) return; // solo con uso global de RAM >= 80 %
        _lastSmartTrimUtc = DateTime.UtcNow;
        var rules = config.Rules;
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited) continue;
                if (SystemWhitelist.Contains(process.ProcessName)) continue;
                if (game is not null && process.Id == game.Id) continue;
                if (rules.Any(rule => MatchesRule(rule, process))) continue; // los juegos con regla nunca se recortan
                if (process.WorkingSet64 < 350L * 1024 * 1024) continue;
                EmptyWorkingSet(process.Handle);
            }
            catch { }
            finally { try { process.Dispose(); } catch { } }
        }
    }

    private static void EnableOwnPrivilege(string privilege)
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!OpenProcessToken(identity.Token, TokenAdjustPrivileges | TokenQuery, out IntPtr token)) return;
            try
            {
                if (!LookupPrivilegeValue(null, privilege, out Luid luid)) return;
                var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = 2 };
                AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, out _);
            }
            finally { CloseHandle(token); }
        }
        catch { }
    }

    // ---------- P/Invoke del demonio ----------

    private static bool IsProcessSuspended(Process process)
    {
        try
        {
            process.Refresh();
            foreach (ProcessThread thread in process.Threads)
            {
                if (thread.ThreadState == System.Diagnostics.ThreadState.Wait && thread.WaitReason == ThreadWaitReason.Suspended) return true;
            }
        }
        catch { }
        return false;
    }

    private static void SuspendProcess(Process process)
    {
        try { NtSuspendProcess(process.Handle); } catch { }
    }

    private static void ResumeProcess(Process process)
    {
        try { NtResumeProcess(process.Handle); } catch { }
    }

    [DllImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottling information, uint size);

    private const int ProcessInformationClassProcessPowerThrottling = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("ntdll.dll", EntryPoint = "NtSuspendProcess")]
    private static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll", EntryPoint = "NtResumeProcess")]
    private static extern int NtResumeProcess(IntPtr process);

    [DllImport("ntdll.dll", EntryPoint = "NtSetTimerResolution")]
    private static extern int NtSetTimerResolution(uint desiredResolution, bool setResolution, out uint currentResolution);

    [DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    private static extern int NtSetSystemInformation(int informationClass, ref int information, int length);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", EntryPoint = "EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
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

    [DllImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveScheme")]
    private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr LocalFree(IntPtr pointer);

    private static Guid GetActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer) != 0) return Guid.Empty;
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    private static void SetActiveScheme(Guid scheme)
    {
        Guid target = scheme;
        PowerSetActiveScheme(IntPtr.Zero, ref target);
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, int bufferLength, IntPtr previousState, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public Luid Luid;
        public int Attributes;
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
