using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ApexKernelOptimizer.Core.Models;
using ApexKernelOptimizer.Core.Daemon;

namespace ApexKernelOptimizer.Core.Engine;

public sealed record ServiceManagerEntry(string Name, string DisplayName, string Mode, string Category, bool IsThirdParty, bool Exists, bool HasBackup)
{
    public override string ToString() => $"[{Category}] {DisplayName} ({Name})  ·  Estado: {Mode}{(HasBackup ? "  ·  [Respaldado]" : "")}";
}

public sealed record StartupEntry(string Kind, string Name, string Command, bool Enabled, string Location)
{
    public override string ToString() => $"[{(Enabled ? "ACTIVO" : "DESACTIVADO")}] {Name} ({Location})  →  {Command}";
}
public sealed record InstanceListBackup(List<string> InstanceIds);
public sealed record PowerSchemeBackup(Guid Scheme);

public sealed partial class ExecutionEngine
{
    private sealed record WifiAdapterBackup(string Name, string Status);

    private const string FlatMouseCurve = "AAAAAAAAAAAKAAAAAAAAABQAAAAAAAAAHgAAAAAAAAAoAAAAAAAAAA==";
    private const string CleanPreferencesMask = "kBIDgBAAAAA=";
    private const string PhysXGpuValue = "BAAAAA==";
    private const string ZeroBinaryValue = "AAAAAA==";
    private const string GpuClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string DisplayClassKey = GpuClassRoot + @"\0000";
    private const string GpuBackupPrefix = @"Windows\GPU\";
    private const string WifiAdapterBackupKey = @"Windows\Net\WifiAdapter";
    private const string NicClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const string UsbClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000}";
    private const string HidClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{745a17a0-74e3-11d0-b5fe-00a0c91efbcb}";
    private const string AudioClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}";
    private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string MmcssKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string DwmKey = @"SOFTWARE\Microsoft\Windows\Dwm";

    private static readonly Guid SubProcessor = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid SubGraphics = Guid.Parse("5fb4938d-1ee8-4b0f-9a3c-5036b0ab995c");
    private static readonly Guid PerfIncThreshold = Guid.Parse("06cadf0e-64ed-448a-8927-ce7bf90eb35d");
    private static readonly Guid PerfDecThreshold = Guid.Parse("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6");
    private static readonly Guid CpMinCores = Guid.Parse("0cc5b647-c1df-4637-891a-dec35c318583");
    private static readonly Guid IdleDemote = Guid.Parse("4b92d758-5a24-4851-a470-815d78aee119");
    private static readonly Guid IdlePromote = Guid.Parse("7b224883-b3cc-4d79-819f-8374152cbe7c");
    private static readonly Guid IdleDisable = Guid.Parse("5d76a2ca-e8c0-402f-a133-2158492d58ad");
    private static readonly Guid PerfBoostMode = Guid.Parse("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid PerfEpp = Guid.Parse("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
    private static readonly Guid DistributeUtil = Guid.Parse("e0007330-f589-42ed-a401-5ddb10e785d3");
    private static readonly Guid GpuPreferencePolicy = Guid.Parse("dd848b2a-8a5d-4451-9ae2-39cd41658f6c");
    private static readonly Guid SchemeMax = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid SchemeMin = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private static readonly string PowerSaverSchemeId = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private static readonly string HighPerfSchemeId = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private static bool IsAllowedExtended(OptimizationItem item)
    {
        switch (item.Id)
        {
            // ===== Ajustes basados únicamente en Registro =====
            case 2:
                return Match(item, @"Control Panel\Mouse",
                    ("SmoothMouseXCurve", FlatMouseCurve, FlatMouseCurve, "Binary"),
                    ("SmoothMouseYCurve", FlatMouseCurve, FlatMouseCurve, "Binary"));
            case 35:
                return MatchManyHive(item,
                    ("HKLM", @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", "0", "1", "DWord"),
                    ("HKLM", @"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags", "0", "1", "DWord"));
            case 36:
                return MatchHive(item, "HKLM", MemoryManagementKey,
                    ("FeatureSettingsOverride", "3", "0", "DWord"),
                    ("FeatureSettingsOverrideMask", "3", "0", "DWord"));
            case 58:
                return MatchHive(item, "HKLM", DisplayClassKey, ("DisableDynamicPstate", "1", "0", "DWord"));
            case 59:
                return MatchHive(item, "HKLM", DisplayClassKey, ("RMHdcpKeyglobZero", "1", "0", "DWord"));
            case 63:
                return MatchHive(item, "HKLM", DisplayClassKey, ("PhysXGpu", PhysXGpuValue, PhysXGpuValue, "Binary"));
            case 70:
                return MatchManyHive(item,
                    ("HKLM", @"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID4424", "0", "1", "DWord"),
                    ("HKLM", @"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID64640", "0", "1", "DWord"),
                    ("HKLM", @"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID66610", "0", "1", "DWord"));
            case 73:
                return MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Services\AFD\Parameters",
                    ("FastSendDatagramThreshold", "65536", "1024", "DWord"));
            case 74:
                return MatchManyHive(item,
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", "0", "20", "DWord"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\QoS\ApexKernel_DSCP46", "Version", "1.0", "1.0", "String"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\QoS\ApexKernel_DSCP46", "Application Name", "*", "*", "String"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\QoS\ApexKernel_DSCP46", "Protocol", "*", "*", "String"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\QoS\ApexKernel_DSCP46", "DSCP Value", "46", "0", "String"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\QoS\ApexKernel_DSCP46", "Throttle Rate", "-1", "0", "String"));
            case 78:
                return MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters",
                    ("DisabledComponents", "255", "32", "DWord"));
            case 84:
                return MatchManyHive(item,
                    ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\audiodg.exe\PerfOptions", "CpuPriorityClass", "3", "2", "DWord"),
                    ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\audiodg.exe\PerfOptions", "IoPriority", "3", "0", "DWord"));
            case 88:
                return MatchManyHive(item,
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "0", "1", "DWord"),
                    ("HKCU", @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", "0", "1", "DWord"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", "0", "1", "DWord"));
            case 91:
                return MatchManyHive(item,
                    ("HKCU", @"Control Panel\Desktop", "UserPreferencesMask", CleanPreferencesMask, "nh4HgBIAAAA=", "Binary"),
                    ("HKCU", @"Control Panel\Desktop", "DragFullWindows", "0", "1", "String"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", "3", "0", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", "0", "1", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewAlphaSelect", "0", "1", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewShadow", "0", "1", "DWord"));
            case 94:
                return MatchManyHive(item,
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", "0", "1", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", "NOC_GLOBAL_SETTING_TOASTS_ENABLED", "0", "1", "DWord"));
            case 98:
                return MatchManyHive(item,
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", "0", "1", "DWord"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", "1", "0", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338393Enabled", "0", "1", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", "0", "1", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", "0", "1", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", "0", "1", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", "0", "1", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", "0", "1", "DWord"));
            case 99:
                return MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager",
                    ("DisableWpbtExecution", "1", "0", "DWord"));
            case 113:
                return MatchHive(item, "HKLM", MmcssKey, ("NoLazyMode", "1", "0", "DWord"));
            case 114:
                return MatchHive(item, "HKLM", MmcssKey, ("LazyModeTimeout", "10000", "10000", "DWord"));
            case 116:
                return MatchHive(item, "HKLM", KernelKey, ("ThreadDpcEnable", "1", "0", "DWord"));
            case 117:
                return MatchHive(item, "HKLM", KernelKey, ("InterruptSteeringDisabled", "1", "0", "DWord"));
            case 119:
                return MatchHive(item, "HKLM", MemoryManagementKey, ("DisablePagingExecutive", "1", "0", "DWord"));
            case 120:
                return MatchHive(item, "HKLM", DwmKey, ("ForceDirectFlip", "1", "0", "DWord"));
            case 121:
                return MatchHive(item, "HKLM", DwmKey, ("ForceIndependentFlip", "1", "0", "DWord"));
            case 122:
                return MatchHive(item, "HKLM", DwmKey, ("ForceFlipTrueImmediate", "1", "0", "DWord"));
            case 123:
                return MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device",
                    ("IoLatencyCap", "20", "80", "DWord"));
            case 124:
                return MatchDeletedValues(item, ("HKLM", MemoryManagementKey, "IoPageLockLimit"));
            case 127:
                return MatchManyHive(item,
                    ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions", "CpuPriorityClass", "3", "2", "DWord"),
                    ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions", "IoPriority", "3", "0", "DWord"));
            case 131:
                return MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                    ("DefaultTTL", "128", "128", "DWord"));
            case 136:
                return MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Control\CI\Config",
                    ("VulnerableDriverBlocklistEnable", "0", "1", "DWord"));
            case 137:
                return MatchHive(item, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
                    ("EnableLUA", "0", "1", "DWord"));
            case 141:
                return MatchManyHive(item,
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableCdp", "0", "1", "DWord"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableMmx", "0", "1", "DWord"));
            case 142:
                return MatchHive(item, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                    ("UseDefaultTile", "0", "1", "DWord"));
            case 159:
                return MatchHive(item, "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU",
                    ("NoAutoRebootWithLoggedOnUsers", "1", "0", "DWord"));
            case 164:
                return MatchManyHive(item,
                    ("HKCU", @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", "1", "0", "DWord"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", "1", "0", "DWord"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", "0", "1", "DWord"),
                    ("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", "1", "0", "DWord"));
            case 166:
                return MatchManyHive(item,
                    ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "DisableSR", "0", "1", "DWord"),
                    ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency", "0", "0", "DWord"));
            case 170:
                return MatchHive(item, "HKLM", @"SOFTWARE\Policies\Microsoft\Windows\EdgeUI",
                    ("AllowEdgeSwipe", "0", "1", "DWord"));
            case 171:
                return MatchHive(item, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                    ("EnableSnapAssistFlyout", "0", "1", "DWord"));
            case 173:
                return MatchManyHive(item,
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "UseCompactMode", "1", "0", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", "1", "1", "DWord"),
                    ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", "0", "1", "DWord"));
            case 191:
                return MatchHive(item, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SecurityCenter",
                    ("Enabled", "0", "1", "DWord"));
            case 93:
                return MatchManyHive(item,
                    ("HKCU", @"Software\Policies\Google\Chrome", "HardwareAccelerationModeEnabled", "0", "1", "DWord"),
                    ("HKCU", @"Software\Policies\Microsoft\Edge", "HardwareAccelerationModeEnabled", "0", "1", "DWord"),
                    ("HKCU", @"Software\Policies\BraveSoftware\Brave", "HardwareAccelerationModeEnabled", "0", "1", "DWord"))
                    && item.TechnicalSummary.Contains("discord", StringComparison.OrdinalIgnoreCase);
            case 202:
                return MatchHive(item, "HKLM", @"SYSTEM\CurrentControlSet\Control\BitLocker",
                    ("PreventDeviceEncryption", "1", "0", "DWord"));
            case 145:
                return MatchManyHive(item,
                    ("HKLM", DisplayClassKey, "EnableUlps", "0", "1", "DWord"),
                    ("HKLM", DisplayClassKey, "EnableUlps_NA", "0", "1", "DWord"),
                    ("HKLM", DisplayClassKey, "PP_ThermalAutoThrottlingEnable", "0", "1", "DWord"),
                    ("HKLM", DisplayClassKey, "DisableDMACopy", "1", "0", "DWord"),
                    ("HKLM", DisplayClassKey, "StutterMode", "0", "2", "DWord"));
            case 60 or 61 or 62 or 65 or 66 or 194:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("NvApiDrs", StringComparison.OrdinalIgnoreCase);
            case 31:
                return MatchHive(item, "HKLM", KernelKey, ("GlobalTimerResolutionRequests", "1", "0", "DWord"))
                    && item.TechnicalSummary.Contains("ProcessLassoNativeService", StringComparison.OrdinalIgnoreCase);

            // ===== Ajustes no-registry: validados por marcador en TechnicalSummary =====
            case 37:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(PerfIncThreshold.ToString(), StringComparison.OrdinalIgnoreCase);
            case 39:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(CpMinCores.ToString(), StringComparison.OrdinalIgnoreCase);
            case 40:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(IdleDemote.ToString(), StringComparison.OrdinalIgnoreCase);
            case 41:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(IdlePromote.ToString(), StringComparison.OrdinalIgnoreCase);
            case 42:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(PerfBoostMode.ToString(), StringComparison.OrdinalIgnoreCase);
            case 43:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(DistributeUtil.ToString(), StringComparison.OrdinalIgnoreCase);
            case 45:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(GpuPreferencePolicy.ToString(), StringComparison.OrdinalIgnoreCase);
            case 162:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(IdleDisable.ToString(), StringComparison.OrdinalIgnoreCase);
            case 157:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(PowerSaverSchemeId, StringComparison.OrdinalIgnoreCase);
            case 206:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains(HighPerfSchemeId, StringComparison.OrdinalIgnoreCase);
            case 50:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("HibernateEnabled", StringComparison.OrdinalIgnoreCase);
            case 75:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("autotuning", StringComparison.OrdinalIgnoreCase);
            case 81:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("teredo", StringComparison.OrdinalIgnoreCase);
            case 77:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("NetAdapterBinding", StringComparison.OrdinalIgnoreCase);
            case 80:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("InterruptModeration", StringComparison.OrdinalIgnoreCase);
            case 184:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("disable8dot3", StringComparison.OrdinalIgnoreCase);
            case 185:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("Set-ReservedStorageState", StringComparison.OrdinalIgnoreCase);
            case 187:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("Microsoft Compatibility Appraiser", StringComparison.OrdinalIgnoreCase);
            case 200:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("NetFirewallRule", StringComparison.OrdinalIgnoreCase);
            case 196:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("Enable-NetAdapterRss", StringComparison.OrdinalIgnoreCase);
            case 197:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("ReceiveBuffers", StringComparison.OrdinalIgnoreCase);
            case 132:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("XblAuthManager", StringComparison.OrdinalIgnoreCase);
            case 133:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("WlanSvc", StringComparison.OrdinalIgnoreCase);
            case 134:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("bthserv", StringComparison.OrdinalIgnoreCase);
            case 135:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("Spooler", StringComparison.OrdinalIgnoreCase);
            case 165:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("LanmanServer", StringComparison.OrdinalIgnoreCase);
            case 178:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("DiagTrack", StringComparison.OrdinalIgnoreCase);
            case 179:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("InstallService", StringComparison.OrdinalIgnoreCase);
            case 186:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("SysMain", StringComparison.OrdinalIgnoreCase);
            case 138:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("nx OptIn", StringComparison.OrdinalIgnoreCase);
            case 175:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("bcdedit /timeout", StringComparison.OrdinalIgnoreCase);
            case 181:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("allowedinmemorystores", StringComparison.OrdinalIgnoreCase);
            case 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 38:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("ProcessLassoNativeService", StringComparison.OrdinalIgnoreCase);
            case 85 or 86:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("Disable-PnpDevice", StringComparison.OrdinalIgnoreCase);
            case 79:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("PnPCapabilities", StringComparison.OrdinalIgnoreCase);
            case 128:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("TcpAckFrequency", StringComparison.OrdinalIgnoreCase);
            case 198:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("ConservationIdleTime", StringComparison.OrdinalIgnoreCase);
            case 199:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("MSPower_DeviceEnable", StringComparison.OrdinalIgnoreCase);
            case 139:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("TextInput.InputApp", StringComparison.OrdinalIgnoreCase);
            case 140:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("SearchHost", StringComparison.OrdinalIgnoreCase);            case 218:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("e9a42b02-d5df-448d-aa00-03f14749eb61", StringComparison.OrdinalIgnoreCase);
            case 219:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase);
            case 220 or 221 or 222:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("36687f9e-e3a5-4dbf-b1dc-15eb381c6863", StringComparison.OrdinalIgnoreCase);
            case 223:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("DwmEnableMMCSS", StringComparison.OrdinalIgnoreCase);
            case 224:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("AdvancedEEE", StringComparison.OrdinalIgnoreCase);
            case 225:
                return item.RegistryChanges.Count == 0 && item.TechnicalSummary.Contains("WinSAT", StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    // ============ Consultas de estado (IsApplied) ============

    private static bool CheckRegistryChanges(OptimizationItem item)
    {
        if (item.RegistryChanges.Count == 0) return false;
        foreach (RegistryChange change in item.RegistryChanges)
        {
            using RegistryKey? key = OpenKey(change, writable: false);
            if (change.DeleteOnApply)
            {
                if (key is not null && key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
                continue;
            }
            if (key is null || !key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
            if (!string.Equals(SerializeCurrentValue(key.GetValue(change.ValueName), key.GetValueKind(change.ValueName)), change.ApplyValue, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static (uint Ac, uint Dc) ReadPowerIndex(Guid subgroup, Guid setting)
    {
        Guid scheme = GetActivePowerScheme();
        (uint ac, uint dc) = ReadPowerSetting(scheme, subgroup, setting);
        return (ac, dc);
    }

    private static bool ReadRegistryDWord(string hive, string path, string name, out uint value)
    {
        value = 0;
        RegistryKey? root = hive == "HKLM" ? Registry.LocalMachine : hive == "HKCU" ? Registry.CurrentUser : null;
        if (root is null) return false;
        using RegistryKey? key = root.OpenSubKey(path, writable: false);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        object? raw = key.GetValue(name);
        if (raw is not int asInt) return false;
        value = unchecked((uint)asInt);
        return true;
    }

    private static bool DaemonFlagOn(int id) => DaemonConfigStore.GetFlag(id.ToString());

    private async Task<bool?> TryAppliedExtendedAsync(OptimizationItem item, CancellationToken cancellationToken)
    {
        try
        {
            switch (item.Id)
            {
                // Solo registro: el motor genérico ya los evalúa.
                case 2 or 35 or 36 or 70 or 73 or 74 or 78 or 84 or 88 or 91 or 94 or 98 or 99:
                case 113 or 114 or 116 or 117 or 119 or 120 or 121 or 122 or 123 or 124 or 127 or 131 or 136 or 137:
                case 141 or 142 or 159 or 164 or 166 or 170 or 171 or 173 or 191:
                    return null;

                case 58 or 59 or 63 or 145:
                {
                    string? keyPath = FindGpuSubKey(item.Id is not 145);
                    if (keyPath is null) return false;
                    foreach (RegistryChange change in GpuChangesAt(item, keyPath))
                    {
                        using RegistryKey? key = OpenKey(change, writable: false);
                        if (change.DeleteOnApply)
                        {
                            if (key is not null && key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
                            continue;
                        }
                        if (key is null || !key.GetValueNames().Contains(change.ValueName, StringComparer.OrdinalIgnoreCase)) return false;
                        if (!string.Equals(SerializeCurrentValue(key.GetValue(change.ValueName), key.GetValueKind(change.ValueName)), change.ApplyValue, StringComparison.Ordinal)) return false;
                    }
                    return true;
                }

                case 93:
                    return CheckRegistryChanges(item) && DiscordHardwareAccelerationDisabled();

                case 37:
                {
                    (uint acInc, uint dcInc) = ReadPowerIndex(SubProcessor, PerfIncThreshold);
                    (uint acDec, uint dcDec) = ReadPowerIndex(SubProcessor, PerfDecThreshold);
                    return acInc == 0 && dcInc == 0 && acDec == 0 && dcDec == 0;
                }
                case 39:
                {
                    (uint ac, uint dc) = ReadPowerIndex(SubProcessor, CpMinCores);
                    return ac == 100 && dc == 100;
                }
                case 40:
                {
                    (uint ac, uint dc) = ReadPowerIndex(SubProcessor, IdleDemote);
                    return ac == 100 && dc == 100;
                }
                case 41:
                {
                    (uint ac, uint dc) = ReadPowerIndex(SubProcessor, IdlePromote);
                    return ac == 100 && dc == 100;
                }
                case 42:
                {
                    (uint ac, uint dc) = ReadPowerIndex(SubProcessor, PerfBoostMode);
                    return ac == 2 && dc == 2;
                }
                case 43:
                {
                    (uint ac, uint dc) = ReadPowerIndex(SubProcessor, DistributeUtil);
                    return ac == 0 && dc == 0;
                }
                case 45:
                {
                    (uint ac, uint dc) = ReadPowerIndex(SubGraphics, GpuPreferencePolicy);
                    return ac == 0 && dc == 0;
                }
                case 162:
                {
                    (uint ac, uint dc) = ReadPowerIndex(SubProcessor, IdleDisable);
                    return ac == 0 && dc == 0;
                }
                case 157:
                    return GetActivePowerScheme() == SchemeMax;
                case 206:
                    return GetActivePowerScheme() == SchemeMin;

                case 50:
                    return ReadRegistryDWord("HKLM", @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", out uint hibernation) && hibernation == 0;

                case 75:
                    return await ReadNetshAutotuningAsync(cancellationToken) == "normal";
                case 81:
                    return await IsTeredoDisabledAsync(cancellationToken);

                case 77:
                    return await AreBindingsDisabledAsync(cancellationToken);
                case 80:
                    return await AreOffloadsDisabledAsync(cancellationToken);
                case 196:
                    return await IsRssEnabledAsync(cancellationToken);
                case 197:
                    return await AreBuffersMaxedAsync(cancellationToken);

                case 184:
                    return await IsFsutilTweakedAsync(cancellationToken);
                case 185:
                    return await IsReservedStorageDisabledAsync(cancellationToken);
                case 187:
                    return await AreTelemetryTasksDisabledAsync(cancellationToken);
                case 200:
                    return await AreTelemetryFirewallRulesPresentAsync(cancellationToken);

                case 133:
                    return await IsWifiSetAppliedAsync(cancellationToken);
                case 132 or 134 or 135 or 165 or 178 or 179 or 186:
                    return await IsServiceSetAppliedAsync(item.Id, cancellationToken);

                case 138:
                {
                    string? value = await ReadBcdOptionAsync("nx", cancellationToken);
                    return string.Equals(value, "OptIn", StringComparison.OrdinalIgnoreCase);
                }
                case 175:
                    return await ReadBcdTimeoutAsync(cancellationToken) == 3;
                case 181:
                {
                    string? value = await ReadBcdOptionAsync("allowedinmemorystores", cancellationToken);
                    return string.Equals(value, "0", StringComparison.Ordinal);
                }

                case 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 38:
                    return DaemonFlagOn(item.Id);

                case 60 or 61 or 62 or 65 or 66 or 194:
                    return IsDrsToggleApplied(item.Id);

                case 31:
                    return CheckRegistryChanges(item) && DaemonFlagOn(31);

                case 85:
                    return await AreGhostAudioDevicesDisabledAsync(cancellationToken);
                case 86:
                    return await IsMidiSynthDisabledAsync(cancellationToken);

                case 79:
                    return IsClassCapabilityApplied([NicClassRoot], "PnPCapabilities", 24, requireNetCfg: true);
                case 199:
                    return IsClassCapabilityApplied([UsbClassRoot, HidClassRoot], "PnPCapabilities", 24, requireNetCfg: false);
                case 198:
                    return IsHdmiIdleDisabled();
                case 128:
                    return IsNagleDisabled();

                case 139:
                    return IsSystemAppRenamed(TextInputFolders);
                case 140:
                    return IsSystemAppRenamed(SearchHostFolders);

                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static readonly string[] TextInputFolders =
    [
        @"MicrosoftWindows.Client.CBS_cw5n1h2txyewy",
        @"WindowsInternal.ComposableShell.Experiences.TextInput.InputApp_cw5n1h2txyewy"
    ];
    private static readonly string[] SearchHostFolders = [@"Microsoft.Windows.Search_cw5n1h2txyewy"];

    private static string SystemAppsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");

    private static bool IsSystemAppRenamed(string[] candidates)
    {
        foreach (string folder in candidates)
        {
            string renamed = Path.Combine(SystemAppsRoot, folder + "_off");
            if (Directory.Exists(renamed) && !Directory.Exists(Path.Combine(SystemAppsRoot, folder))) return true;
        }
        return false;
    }

    private static bool DiscordHardwareAccelerationDisabled()
    {
        try
        {
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "settings.json");
            if (!File.Exists(settings)) return false;
            JsonNode? node = JsonNode.Parse(File.ReadAllText(settings));
            return node is JsonObject root &&
                root["ENABLE_HARDWARE_ACCELERATION"] is JsonValue value &&
                value.TryGetValue<bool>(out bool enabled) && !enabled;
        }
        catch { return false; }
    }

    // ---------- GPU: detección dinámica de la subclave real del adaptador ----------

    private static string? FindGpuSubKey(bool nvidia)
    {
        using RegistryKey? parent = Registry.LocalMachine.OpenSubKey(GpuClassRoot, writable: false);
        if (parent is null) return null;
        foreach (string sub in parent.GetSubKeyNames())
        {
            if (!sub.StartsWith("00", StringComparison.Ordinal) || sub.Length != 4) continue;
            using RegistryKey? key = parent.OpenSubKey(sub, writable: false);
            if (key is null) continue;
            if (key.GetValue("ProviderName") is not string provider || string.IsNullOrWhiteSpace(provider)) continue;
            bool matches = nvidia
                ? provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                : provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
                  provider.Contains("ATI", StringComparison.OrdinalIgnoreCase);
            if (matches) return GpuClassRoot + "\\" + sub;
        }
        return null;
    }

    private static List<RegistryChange> GpuChangesAt(OptimizationItem item, string keyPath)
    {
        var changes = new List<RegistryChange>(item.RegistryChanges.Count);
        foreach (RegistryChange change in item.RegistryChanges)
            changes.Add(new RegistryChange
            {
                Hive = change.Hive,
                KeyPath = keyPath,
                ValueName = change.ValueName,
                ValueKind = change.ValueKind,
                ApplyValue = change.ApplyValue,
                RevertValue = change.RevertValue,
                DeleteOnApply = change.DeleteOnApply
            });
        return changes;
    }

    private static bool IsClassCapabilityApplied(string[] classRoots, string valueName, int target, bool requireNetCfg)
    {
        int matched = 0;
        foreach (string root in classRoots)
        {
            using RegistryKey? parent = Registry.LocalMachine.OpenSubKey(root, writable: false);
            if (parent is null) continue;
            foreach (string sub in parent.GetSubKeyNames())
            {
                if (!sub.StartsWith("00", StringComparison.Ordinal)) continue;
                string path = root + "\\" + sub;
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: false);
                if (key is null) continue;
                if (!key.GetValueNames().Contains("DriverDesc", StringComparer.OrdinalIgnoreCase)) continue;
                if (requireNetCfg && !key.GetValueNames().Contains("NetCfgInstanceId", StringComparer.OrdinalIgnoreCase)) continue;
                object? raw = key.GetValue(valueName);
                if (raw is not int value || value != target) return false;
                matched++;
            }
        }
        return matched > 0;
    }

    private static bool IsHdmiIdleDisabled()
    {
        int matched = 0;
        using RegistryKey? parent = Registry.LocalMachine.OpenSubKey(AudioClassRoot, writable: false);
        if (parent is null) return false;
        foreach (string sub in parent.GetSubKeyNames())
        {
            if (!sub.StartsWith("00", StringComparison.Ordinal)) continue;
            string powerSettingsPath = AudioClassRoot + "\\" + sub + "\\PowerSettings";
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(powerSettingsPath, writable: false);
            if (key is null) continue;
            bool found = false;
            foreach (string name in new[] { "ConservationIdleTime", "PerformanceIdleTime" })
            {
                if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
                object? raw = key.GetValue(name);
                if (raw is not byte[] bytes || bytes.Length == 0 || bytes.Any(b => b != 0)) return false;
                found = true;
            }
            if (found) matched++;
        }
        return matched > 0;
    }

    private static bool IsNagleDisabled()
    {
        int matched = 0;
        using RegistryKey? parent = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", writable: false);
        if (parent is null) return false;
        foreach (string sub in parent.GetSubKeyNames())
        {
            string path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + sub;
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            if (key is null) continue;
            if (!IsActiveTcpInterface(key)) continue;
            object? ack = key.GetValue("TcpAckFrequency");
            object? noDelay = key.GetValue("TCPNoDelay");
            object? delAck = key.GetValue("TcpDelAckTicks");
            if (ack is not int || ((int)ack != 1) || noDelay is not int || ((int)noDelay != 1) || delAck is not int || ((int)delAck != 0)) return false;
            matched++;
        }
        return matched > 0;
    }

    private static bool IsActiveTcpInterface(RegistryKey key)
    {
        string[] names = key.GetValueNames();
        if (names.Contains("DhcpIPAddress", StringComparer.OrdinalIgnoreCase))
        {
            string? dhcp = key.GetValue("DhcpIPAddress") as string;
            if (!string.IsNullOrWhiteSpace(dhcp) && dhcp != "0.0.0.0") return true;
        }
        if (names.Contains("IPAddress", StringComparer.OrdinalIgnoreCase) && key.GetValue("IPAddress") is string[] statics)
        {
            if (statics.Any(ip => !string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0")) return true;
        }
        return false;
    }

    // ============ Consultas de respaldo (HasBackup) ============

    private async Task<bool?> TryBackupExtendedAsync(OptimizationItem item, CancellationToken cancellationToken)
    {
        switch (item.Id)
        {
            case 58 or 59 or 63 or 145:
                return await _backup.ReadOriginalAsync<DevicePowerSnapshot>(GpuBackupPrefix + item.Id, cancellationToken) is not null;
            case 37 or 39 or 40 or 41 or 42 or 43 or 45 or 162:
                return await _backup.ReadOriginalAsync<PowerIndexSnapshot>(@"Windows\Power\Ext\" + item.Id, cancellationToken) is not null;
            case 157 or 206:
                return await _backup.ReadOriginalAsync<PowerSchemeBackup>(@"Windows\Power\ActiveScheme", cancellationToken) is not null;
            case 50:
                return await _backup.ReadOriginalAsync<RegistryValueSnapshot>(@"Windows\Power\Hibernate", cancellationToken) is not null;
            case 75:
                return await _backup.ReadOriginalAsync<string>(@"Windows\Net\Autotuning", cancellationToken) is not null;
            case 81:
                return await _backup.ReadOriginalAsync<string>(@"Windows\Net\Teredo", cancellationToken) is not null;
            case 77:
                return await _backup.HasBackupAsync(@"Windows\Net\Bindings", cancellationToken);
            case 80:
                return await _backup.HasBackupAsync(@"Windows\Net\Offloading", cancellationToken);
            case 196:
                return await _backup.HasBackupAsync(@"Windows\Net\RSS", cancellationToken);
            case 197:
                return await _backup.HasBackupAsync(@"Windows\Net\MaxBuffers", cancellationToken);
            case 184:
                return await _backup.HasBackupAsync(@"Windows\Storage\Fsutil", cancellationToken);
            case 185:
                return await _backup.HasBackupAsync(@"Windows\Storage\ReservedStorage", cancellationToken);
            case 187:
                return await _backup.HasBackupAsync(@"Windows\Tasks\TelemetryTasks", cancellationToken);
            case 200:
                return await _backup.HasBackupAsync(@"Windows\Firewall\TelemetryBlock", cancellationToken);
            case 133:
                return await IsWifiSetBackedUpAsync(cancellationToken);
            case 132 or 134 or 135 or 165 or 178 or 179 or 186:
                return await IsServiceSetBackedUpAsync(item.Id, cancellationToken);
            case 138:
                return await _backup.ReadOriginalAsync<BcdOptionSnapshot>(BcdBackupKey("nx"), cancellationToken) is not null;
            case 175:
                return await _backup.HasBackupAsync(@"Windows\BCD\timeout", cancellationToken);
            case 181:
                return await _backup.ReadOriginalAsync<BcdOptionSnapshot>(BcdBackupKey("allowedinmemorystores"), cancellationToken) is not null;
            case 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 38:
                return await _backup.HasBackupAsync(@"Daemon\Pre\" + item.Id, cancellationToken);
            case 60 or 61 or 62 or 65 or 66 or 194:
                return false; // el revert usa NvAPI_DRS_RestoreProfileDefaultSetting; no hay respaldo propio
            case 31:
            {
                if (item.RegistryChanges.Count != 1) return false;
                if (await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(item.RegistryChanges[0]), cancellationToken) is null) return false;
                return await _backup.HasBackupAsync(@"Daemon\Pre\31", cancellationToken);
            }
            case 85 or 86:
                return await _backup.ReadOriginalAsync<InstanceListBackup>(@"Windows\Devices\Pnp\" + item.Id, cancellationToken) is not null;
            case 79:
                return await _backup.ReadOriginalAsync<DevicePowerSnapshot>(@"Windows\NIC\PnPCap", cancellationToken) is not null;
            case 199:
                return await _backup.ReadOriginalAsync<DevicePowerSnapshot>(@"Windows\Devices\UsbPnPCap", cancellationToken) is not null;
            case 198:
                return await _backup.ReadOriginalAsync<DevicePowerSnapshot>(@"Windows\Audio\HdmiIdle", cancellationToken) is not null;
            case 128:
                return await _backup.ReadOriginalAsync<DevicePowerSnapshot>(@"Windows\NIC\Nagle", cancellationToken) is not null;
            case 139:
                return await _backup.HasBackupAsync(@"Windows\SystemApps\139", cancellationToken);
            case 140:
                return await _backup.HasBackupAsync(@"Windows\SystemApps\140", cancellationToken);
            case 93:
            {
                if (item.RegistryChanges.Count != 3) return false;
                foreach (RegistryChange change in item.RegistryChanges)
                    if (await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(change), cancellationToken) is null) return false;
                return await _backup.ReadOriginalAsync<string>(@"Windows\Apps\DiscordHWA", cancellationToken) is not null;
            }
            default:
                return null;
        }
    }

    // ============ Aplicar / restaurar (SetEnabled) ============

    private async Task<ExecutionResult?> TrySetExtendedAsync(OptimizationItem item, bool enabled, bool hardwareApplicable, CancellationToken cancellationToken)
    {
        switch (item.Id)
        {
            case 58 or 59 or 63 or 145:
                return await SetGpuRegistryAsync(item, enabled, cancellationToken);
            case 37 or 39 or 40 or 41 or 42 or 43 or 45 or 162:
                return await SetPowerIndexToggleAsync(item.Id, enabled, cancellationToken);
            case 157:
                return await SetPowerSchemeToggleAsync(SchemeMax, "Ahorro de energía", enabled, cancellationToken);
            case 206:
                return await SetPowerSchemeToggleAsync(SchemeMin, "Competitivo (máximo rendimiento)", enabled, cancellationToken);
            case 50:
                return await SetHibernationAsync(enabled, cancellationToken);
            case 75:
                return await SetAutotuningAsync(enabled, cancellationToken);
            case 81:
                return await SetTeredoAsync(enabled, cancellationToken);
            case 77:
                return await SetNicBindingsAsync(enabled, cancellationToken);
            case 80:
                if (enabled && !hardwareApplicable) return new(false, "Este ajuste requiere un procesador de 6 o más núcleos físicos.");
                return await SetNicOffloadsAsync(enabled, cancellationToken);
            case 196:
                return await SetNicRssAsync(enabled, cancellationToken);
            case 197:
                return await SetNicMaxBuffersAsync(enabled, cancellationToken);
            case 184:
                return await SetFsutilAsync(enabled, cancellationToken);
            case 185:
                return await SetReservedStorageAsync(enabled, cancellationToken);
            case 187:
                return await SetTelemetryTasksAsync(enabled, cancellationToken);
            case 200:
                return await SetTelemetryFirewallAsync(enabled, cancellationToken);
            case 133:
                return await SetWifiSetAsync(enabled, cancellationToken);
            case 132 or 134 or 135 or 165 or 178 or 179 or 186:
                if (item.Id == 186 && enabled && !hardwareApplicable) return new(false, "SysMain se desactiva solo cuando la unidad del sistema es un SSD.");
                return await SetServiceSetAsync(item.Id, enabled, cancellationToken);
            case 138:
                return await SetBcdValueAsync("nx", "OptIn", enabled, cancellationToken);
            case 181:
                return await SetBcdValueAsync("allowedinmemorystores", "0", enabled, cancellationToken);
            case 175:
                return await SetBootTimeoutAsync(enabled, cancellationToken);
            case 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 38:
                if ((item.Id == 21 || item.Id == 22 || item.Id == 28) && enabled && !hardwareApplicable) return new(false, "Este ajuste requiere un procesador de 6 o más núcleos físicos y 16 GB de RAM.");
                return await SetDaemonFlagAsync(item.Id, enabled, cancellationToken);
            case 60 or 61 or 62 or 65 or 66 or 194:
                return await SetDrsToggleAsync(item.Id, enabled, cancellationToken);
            case 31:
                return await SetTimerResolutionItemAsync(item, enabled, cancellationToken);
            case 85 or 86:
                return await SetPnpDevicesAsync(item.Id, enabled, cancellationToken);
            case 79:
                return await SetClassCapabilityAsync(item.Id, @"Windows\NIC\PnPCap", [NicClassRoot], "PnPCapabilities", true, enabled, cancellationToken);
            case 199:
                return await SetClassCapabilityAsync(item.Id, @"Windows\Devices\UsbPnPCap", [UsbClassRoot, HidClassRoot], "PnPCapabilities", false, enabled, cancellationToken);
            case 198:
                return await SetHdmiIdleAsync(enabled, cancellationToken);
            case 128:
                return await SetNagleAsync(enabled, cancellationToken);
            case 139:
                return await SetSystemAppRenameAsync(139, TextInputFolders, "TextInputHost.exe", enabled, cancellationToken);
            case 140:
                return await SetSystemAppRenameAsync(140, SearchHostFolders, "SearchHost.exe", enabled, cancellationToken);
            case 93:
                return await SetDiscordBrowserHwaAsync(item, enabled, cancellationToken);
            case 218:
                return await SetPowerSchemeToggleAsync(Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61"), "Ultimate Performance", enabled, cancellationToken);
            case 219:
                return await SetPowerSchemeToggleAsync(Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), "High Performance", enabled, cancellationToken);
            case 220 or 221 or 222:
                return await SetPowerIndexToggleAsync(item.Id, enabled, cancellationToken);
            case 223:
                return await SetDwmMmcssAsync(enabled, cancellationToken);
            case 224:
                return await SetNicEeeGreenAsync(enabled, cancellationToken);
            case 225:
                return await SetWinsatTaskAsync(enabled, cancellationToken);
            default:
                return null;
        }
    }

    // ---------- Registro de GPU en la subclave detectada (#58/#59/#63/#145) ----------

    private async Task<ExecutionResult> SetGpuRegistryAsync(OptimizationItem item, bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Este ajuste requiere privilegios de administrador.");
        bool nvidia = item.Id is not 145;
        string? keyPath = FindGpuSubKey(nvidia);
        if (keyPath is null)
            return new(false, nvidia
                ? "No se encontró ninguna subclave de GPU NVIDIA en el Registro; no se modificó nada."
                : "No se encontró ninguna subclave de GPU AMD/ATI en el Registro; no se modificó nada.");
        List<RegistryChange> changes = GpuChangesAt(item, keyPath);
        string backupKey = GpuBackupPrefix + item.Id;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                if (await _backup.ReadOriginalAsync<DevicePowerSnapshot>(backupKey, cancellationToken) is not null)
                    return new(false, "Ya hay un respaldo pendiente de este ajuste; restáuralo antes de aplicar de nuevo.");
                var originals = new List<DevicePowerKeySnapshot>();
                foreach (RegistryChange change in changes)
                    originals.Add(new(keyPath, [new DevicePowerValueSnapshot(change.ValueName, ReadSnapshot(change))]));
                await _backup.SaveOriginalAsync(backupKey, new DevicePowerSnapshot(originals), cancellationToken);
                try
                {
                    foreach (RegistryChange change in changes)
                        ApplyRegistryChange(change);
                }
                catch
                {
                    for (int i = 0; i < changes.Count; i++)
                    {
                        try { RestoreValue(changes[i], originals[i].Values[0].Original); } catch { }
                    }
                    try { await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None); } catch { }
                    throw;
                }
                return new(true, $"Se aplicó el ajuste en la subclave real de la GPU ({keyPath}) y se respaldaron los valores originales.");
            }

            DevicePowerSnapshot? saved = await _backup.ReadOriginalAsync<DevicePowerSnapshot>(backupKey, cancellationToken);
            if (saved is null) return new(false, "No hay respaldo previo de este ajuste; no se cambió nada.");
            DevicePowerKeySnapshot? savedKey = saved.Keys.FirstOrDefault(k => string.Equals(k.RegistryPath, keyPath, StringComparison.OrdinalIgnoreCase));
            foreach (RegistryChange change in changes)
            {
                DevicePowerValueSnapshot? value = savedKey?.Values.FirstOrDefault(v => string.Equals(v.Name, change.ValueName, StringComparison.OrdinalIgnoreCase));
                if (value is null) continue;
                RestoreValue(change, value.Original);
            }
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauraron exactamente los valores originales de la subclave de GPU detectada.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or ArgumentException)
        {
            return new(false, $"No se pudo aplicar el ajuste de GPU: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    // ---------- Ajustes de energía ----------

    private async Task<ExecutionResult> SetPowerIndexToggleAsync(int id, bool enabled, CancellationToken cancellationToken)
    {
        (Guid subgroup, Guid setting, uint target) = id switch
        {
            37 => (SubProcessor, PerfIncThreshold, 0u),
            39 => (SubProcessor, CpMinCores, 100u),
            40 => (SubProcessor, IdleDemote, 100u),
            41 => (SubProcessor, IdlePromote, 100u),
            42 => (SubProcessor, PerfBoostMode, 2u),
            43 => (SubProcessor, DistributeUtil, 0u),
            45 => (SubGraphics, GpuPreferencePolicy, 0u),
            220 => (SubProcessor, PerfEpp, 0u),
            221 => (SubProcessor, PerfEpp, 30u),
            222 => (SubProcessor, PerfEpp, 50u),
            _ => (SubProcessor, IdleDisable, 0u)
        };
        // #37 también baja el umbral de degradación para un perfil de latencia baja real.
        if (id == 37)
        {
            await _gate.WaitAsync(cancellationToken);
            try { return await SetPowerPairCoreAsync(37, enabled, cancellationToken); }
            finally { _gate.Release(); }
        }
        string backupKey = @"Windows\Power\Ext\" + id;
        if (!IsAdministrator()) return new(false, "Cambiar el plan de energía requiere ejecutar la aplicación como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Guid scheme = GetActivePowerScheme();
            (uint ac, uint dc) = ReadPowerSetting(scheme, subgroup, setting);
            if (enabled)
            {
                if (ac == target && dc == target)
                    return new(true, "El índice de energía ya estaba en el valor solicitado; se guardó el estado previo para poder restaurarlo.");
                PowerIndexSnapshot original = new(scheme, subgroup, setting, ac, dc);
                if (await _backup.ReadOriginalAsync<PowerIndexSnapshot>(backupKey, cancellationToken) is not null)
                    return new(false, "Ya hay un respaldo pendiente de este ajuste; restáuralo antes de aplicar de nuevo.");
                await _backup.SaveOriginalAsync(backupKey, original, cancellationToken);
                try
                {
                    WritePowerSetting(original, target, target, refreshIfActive: true);
                }
                catch
                {
                    try { WritePowerSetting(original, original.AcValue, original.DcValue, refreshIfActive: true); } catch { }
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, $"Se aplicó el índice de energía solicitado (CA/DC={target}) y se respaldó el estado previo.");
            }
            PowerIndexSnapshot? original2 = await _backup.ReadOriginalAsync<PowerIndexSnapshot>(backupKey, cancellationToken);
            if (original2 is null) return new(false, "No hay respaldo previo de este índice de energía; no se modificó el plan.");
            Guid scheme2 = GetActivePowerScheme();
            var restore = new PowerIndexSnapshot(scheme2, original2.Subgroup, original2.Setting, original2.AcValue, original2.DcValue);
            WritePowerSetting(restore, original2.AcValue, original2.DcValue, refreshIfActive: true);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauró el índice de energía original.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or Win32Exception or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar el plan de energía: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetPowerPairCoreAsync(int id, bool enabled, CancellationToken cancellationToken)
    {
        const string backupKey = @"Windows\Power\Ext\37";
        Guid scheme = GetActivePowerScheme();
        if (enabled)
        {
            (uint incAc, uint incDc) = ReadPowerSetting(scheme, SubProcessor, PerfIncThreshold);
            (uint decAc, uint decDc) = ReadPowerSetting(scheme, SubProcessor, PerfDecThreshold);
            if (await _backup.ReadOriginalAsync<PowerIndexSnapshot>(backupKey, cancellationToken) is not null)
                return new(false, "Ya hay un respaldo pendiente del perfil de latencia baja; restáuralo antes de aplicar de nuevo.");
            var a = new PowerIndexSnapshot(scheme, SubProcessor, PerfIncThreshold, incAc, incDc);
            var b = new PowerIndexSnapshot(scheme, SubProcessor, PerfDecThreshold, decAc, decDc);
            await _backup.SaveOriginalAsync(backupKey, a, cancellationToken);
            await _backup.SaveOriginalAsync(backupKey + "b", b, cancellationToken);
            try
            {
                WritePowerSetting(a, 0, 0, refreshIfActive: true);
                WritePowerSetting(b, 0, 0, refreshIfActive: true);
            }
            catch
            {
                try { WritePowerSetting(a, a.AcValue, a.DcValue, refreshIfActive: true); } catch { }
                try { WritePowerSetting(b, b.AcValue, b.DcValue, refreshIfActive: true); } catch { }
                await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                await _backup.RemoveOriginalAsync(backupKey + "b", CancellationToken.None);
                throw;
            }
            return new(true, "Perfil de latencia baja aplicado: subida y bajada de frecuencia inmediatas (umbrales a 0).");
        }
        PowerIndexSnapshot? a2 = await _backup.ReadOriginalAsync<PowerIndexSnapshot>(backupKey, cancellationToken);
        PowerIndexSnapshot? b2 = await _backup.ReadOriginalAsync<PowerIndexSnapshot>(backupKey + "b", cancellationToken);
        if (a2 is null || b2 is null) return new(false, "No hay respaldo previo del perfil de latencia baja; no se modificó el plan.");
        Guid scheme2 = GetActivePowerScheme();
        WritePowerSetting(new PowerIndexSnapshot(scheme2, a2.Subgroup, a2.Setting, a2.AcValue, a2.DcValue), a2.AcValue, a2.DcValue, refreshIfActive: true);
        WritePowerSetting(new PowerIndexSnapshot(scheme2, b2.Subgroup, b2.Setting, b2.AcValue, b2.DcValue), b2.AcValue, b2.DcValue, refreshIfActive: true);
        await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
        await _backup.RemoveOriginalAsync(backupKey + "b", cancellationToken);
        return new(true, "Se restauraron los umbrales originales de frecuencia del procesador.");
    }

    private async Task<ExecutionResult> SetPowerSchemeToggleAsync(Guid targetScheme, string label, bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Cambiar el plan de energía activo requiere ejecutar la aplicación como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Guid active = GetActivePowerScheme();
            if (enabled)
            {
                if (active == targetScheme)
                    return new(true, $"El plan {label} ya está activo; se guardó el plan previo para poder volver atrás.");
                if (await _backup.ReadOriginalAsync<PowerSchemeBackup>(@"Windows\Power\ActiveScheme", cancellationToken) is not null)
                    return new(false, "Ya existe un respaldo del plan activo; restáuralo antes de cambiar de nuevo.");
                await _backup.SaveOriginalAsync(@"Windows\Power\ActiveScheme", new PowerSchemeBackup(active), cancellationToken);
                Guid target = targetScheme;
                uint status = PowerSetActiveScheme(IntPtr.Zero, ref target);
                ThrowPowerError(status, "No se pudo activar el plan solicitado.");
                return new(true, $"Se activó el plan {label}; el plan anterior quedó respaldado.");
            }
            PowerSchemeBackup? original = await _backup.ReadOriginalAsync<PowerSchemeBackup>(@"Windows\Power\ActiveScheme", cancellationToken);
            if (original is null) return new(false, "No hay respaldo del plan de energía previo; no se cambió el plan activo.");
            Guid restore = original.Scheme;
            uint status2 = PowerSetActiveScheme(IntPtr.Zero, ref restore);
            ThrowPowerError(status2, "No se pudo restaurar el plan de energía.");
            await _backup.RemoveOriginalAsync(@"Windows\Power\ActiveScheme", cancellationToken);
            return new(true, "Se restauró el plan de energía original.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or JsonException or Win32Exception or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar el plan de energía: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetHibernationAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Desactivar la hibernación requiere ejecutar la aplicación como administrador.");
        var change = new RegistryChange
        {
            Hive = "HKLM",
            KeyPath = @"SYSTEM\CurrentControlSet\Control\Power",
            ValueName = "HibernateEnabled",
            ValueKind = "DWord",
            ApplyValue = "0",
            RevertValue = "1"
        };
        string backupKey = @"Windows\Power\Hibernate";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                if (ReadRegistryDWord("HKLM", change.KeyPath, "HibernateEnabled", out uint current) && current == 0)
                {
                    if (await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, cancellationToken) is not null)
                        return new(true, "La hibernación ya estaba desactivada; el respaldo se conserva para poder revertir.");
                }
                RegistryValueSnapshot original = ReadSnapshot(change);
                await _backup.SaveOriginalAsync(backupKey, original, cancellationToken);
                try
                {
                    WriteValue(change, "0");
                    await RunPowerShellAsync("powercfg /hibernate off", cancellationToken);
                }
                catch
                {
                    try { RestoreValue(change, original); } catch { }
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "Hibernación desactivada y hiberfil.sys eliminado; se respaldó el estado previo.");
            }
            RegistryValueSnapshot? saved = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(backupKey, cancellationToken);
            if (saved is null) return new(false, "No hay respaldo previo de la hibernación; no se cambió nada.");
            RestoreValue(change, saved);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            try { await RunPowerShellAsync("powercfg /hibernate on", cancellationToken); } catch { }
            return new(true, "Se restauró la configuración original de hibernación.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or Win32Exception)
        {
            return new(false, $"No se pudo cambiar la hibernación: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    // ---------- Comandos netsh / fsutil / DISM / schtasks / firewall ----------

    private async Task<ExecutionResult> SetCommandToggleAsync(string backupKey, string applyCommand, string revertCommand,
        Func<CancellationToken, Task<bool>> isApplied, bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Este ajuste requiere ejecutar la aplicación como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                if (await _backup.HasBackupAsync(backupKey, cancellationToken))
                    return new(false, "Ya existe un respaldo pendiente de este ajuste; restáuralo antes de aplicar de nuevo.");
                await _backup.SaveOriginalAsync(backupKey, true, cancellationToken);
                try { await RunPowerShellAsync(applyCommand, cancellationToken); }
                catch
                {
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                bool applied = await isApplied(CancellationToken.None);
                return new(applied, applied ? "Comando aplicado y verificado." : "El comando se ejecutó pero la verificación no confirmó el estado; revisa el resultado.");
            }
            if (!await _backup.HasBackupAsync(backupKey, cancellationToken))
                return new(false, "No hay respaldo previo de este ajuste; no se ejecutó la reversión.");
            await RunPowerShellAsync(revertCommand, cancellationToken);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauró el estado original mediante comando.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or Win32Exception)
        {
            return new(false, $"No se pudo completar el comando: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetAutotuningAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Cambiar el autoajuste TCP requiere ejecutar la aplicación como administrador.");
        const string backupKey = @"Windows\Net\Autotuning";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                string? current = await ReadNetshAutotuningAsync(cancellationToken);
                if (await _backup.ReadOriginalAsync<string>(backupKey, cancellationToken) is not null)
                    return new(false, "Ya existe un respaldo del nivel de autoajuste; restáuralo antes de aplicar de nuevo.");
                await _backup.SaveOriginalAsync(backupKey, current ?? "normal", cancellationToken);
                try { await RunPowerShellAsync("netsh int tcp set global autotuning=normal", cancellationToken); }
                catch
                {
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "Autoajuste TCP configurado en normal; se respaldó el nivel previo.");
            }
            string? saved = await _backup.ReadOriginalAsync<string>(backupKey, cancellationToken);
            if (saved is null) return new(false, "No hay respaldo del nivel de autoajuste; no se modificó nada.");
            string restore = string.IsNullOrWhiteSpace(saved) || saved == "normal" ? "normal" : saved;
            await RunPowerShellAsync($"netsh int tcp set global autotuning={restore}", cancellationToken);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, $"Se restauró el autoajuste TCP en {restore}.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or Win32Exception)
        {
            return new(false, $"No se pudo cambiar el autoajuste TCP: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static async Task<string?> ReadNetshAutotuningAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync("netsh int tcp show global", cancellationToken);
        foreach (string rawLine in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!rawLine.Contains("autotun", StringComparison.OrdinalIgnoreCase) && !rawLine.Contains("autoajust", StringComparison.OrdinalIgnoreCase)) continue;
            Match match = Regex.Match(rawLine, @":\s*(\S+)");
            if (match.Success) return match.Groups[1].Value.Trim().ToLowerInvariant();
        }
        return null;
    }

    private async Task<ExecutionResult> SetTeredoAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Desactivar Teredo requiere ejecutar la aplicación como administrador.");
        const string backupKey = @"Windows\Net\Teredo";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                string state = await RunPowerShellAsync("netsh int teredo show state", cancellationToken);
                if (await _backup.ReadOriginalAsync<string>(backupKey, cancellationToken) is not null)
                    return new(false, "Ya existe un respaldo del estado de Teredo; restáuralo antes de aplicar de nuevo.");
                await _backup.SaveOriginalAsync(backupKey, state, cancellationToken);
                try { await RunPowerShellAsync("netsh int teredo set state disabled", cancellationToken); }
                catch
                {
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "Túnel Teredo desactivado; se respaldó el estado previo.");
            }
            if (await _backup.ReadOriginalAsync<string>(backupKey, cancellationToken) is null)
                return new(false, "No hay respaldo del estado de Teredo; no se modificó nada.");
            await RunPowerShellAsync("netsh int teredo set state default", cancellationToken);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Teredo volvió a su estado por defecto.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or Win32Exception)
        {
            return new(false, $"No se pudo cambiar Teredo: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> IsTeredoDisabledAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync("netsh int teredo show state", cancellationToken);
        return output.Contains("disabled", StringComparison.OrdinalIgnoreCase) || output.Contains("deshabilitad", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ExecutionResult> SetNicBindingsAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetCommandToggleAsync(@"Windows\Net\Bindings",
            "Get-NetAdapter -Physical | ForEach-Object { Disable-NetAdapterBinding -Name $_.Name -ComponentID ms_server,ms_msclient,ms_lltdio,ms_rspndr -ErrorAction SilentlyContinue }",
            "Get-NetAdapter -Physical | ForEach-Object { Enable-NetAdapterBinding -Name $_.Name -ComponentID ms_server,ms_msclient,ms_lltdio,ms_rspndr -ErrorAction SilentlyContinue }",
            AreBindingsDisabledAsync, enabled, cancellationToken);

    private static async Task<bool> AreBindingsDisabledAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync(
            "$b = Get-NetAdapterBinding -ErrorAction SilentlyContinue | Where-Object { $_.ComponentID -in 'ms_server','ms_msclient','ms_lltdio','ms_rspndr' }; if ($b.Count -eq 0) { 'NONE' } elseif (@($b | Where-Object Enabled).Count -eq 0) { 'APPLIED' } else { 'ACTIVE' }",
            cancellationToken);
        return output.Trim().EndsWith("APPLIED", StringComparison.Ordinal);
    }

    private async Task<ExecutionResult> SetNicOffloadsAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetCommandToggleAsync(@"Windows\Net\Offloading",
            "Get-NetAdapter -Physical | ForEach-Object { $n = $_.Name; foreach ($p in @('*Interrupt Moderation*','*Flow Control*','*LSO*')) { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $p -RegistryValue 0 -ErrorAction SilentlyContinue } }",
            "Get-NetAdapter -Physical | ForEach-Object { $n = $_.Name; foreach ($p in @('*Interrupt Moderation*','*Flow Control*')) { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $p -RegistryValue 1 -ErrorAction SilentlyContinue } foreach ($p in @('*LSO*')) { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $p -RegistryValue 1 -ErrorAction SilentlyContinue } }",
            AreOffloadsDisabledAsync, enabled, cancellationToken);

    private static async Task<bool> AreOffloadsDisabledAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync(
            "$p = Get-NetAdapterAdvancedProperty -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*Interrupt Moderation*' -or $_.DisplayName -like '*Flow Control*' }; if ($p.Count -eq 0) { 'NONE' } elseif (@($p | Where-Object { $_.RegistryValue -ne 0 }).Count -eq 0) { 'APPLIED' } else { 'ACTIVE' }",
            cancellationToken);
        return output.Trim().EndsWith("APPLIED", StringComparison.Ordinal);
    }

    private async Task<ExecutionResult> SetNicRssAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetCommandToggleAsync(@"Windows\Net\RSS",
            "Enable-NetAdapterRss -Name * -ErrorAction SilentlyContinue; Get-NetAdapter -Physical | ForEach-Object { Set-NetAdapterAdvancedProperty -Name $_.Name -DisplayName '*RSS Queues*' -RegistryValue 4 -ErrorAction SilentlyContinue }",
            "Disable-NetAdapterRss -Name * -ErrorAction SilentlyContinue",
            IsRssEnabledAsync, enabled, cancellationToken);

    private static async Task<bool> IsRssEnabledAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync("$r = Get-NetAdapterRss -ErrorAction SilentlyContinue | Where-Object Enabled; if (@($r).Count -gt 0) { 'APPLIED' } else { 'INACTIVE' }", cancellationToken);
        return output.Trim().EndsWith("APPLIED", StringComparison.Ordinal);
    }

    private async Task<ExecutionResult> SetNicMaxBuffersAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetCommandToggleAsync(@"Windows\Net\MaxBuffers",
            "Get-NetAdapter -Physical | ForEach-Object { $n = $_.Name; foreach ($p in @('*Receive Buffers*','*Transmit Buffers*')) { $prop = Get-NetAdapterAdvancedProperty -Name $n -DisplayName $p -ErrorAction SilentlyContinue; if ($prop -and $prop.ValidRegistryValueDisplayNames) { $max = [uint32]($prop.ValidRegistryValueDisplayNames | Sort-Object { [uint32]$_ } | Select-Object -Last 1); if ($max -gt 0) { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $p -RegistryValue $max -ErrorAction SilentlyContinue } } } }",
            "Get-NetAdapter -Physical | ForEach-Object { $n = $_.Name; foreach ($p in @('*Receive Buffers*','*Transmit Buffers*')) { Set-NetAdapterAdvancedProperty -Name $n -DisplayName $p -RegistryValue 512 -ErrorAction SilentlyContinue } }",
            AreBuffersMaxedAsync, enabled, cancellationToken);

    private static async Task<bool> AreBuffersMaxedAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync(
            "$p = Get-NetAdapterAdvancedProperty -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*Receive Buffers*' -or $_.DisplayName -like '*Transmit Buffers*' }; if ($p.Count -eq 0) { 'NONE' } elseif (@($p | Where-Object { $cur=[uint32]$_.RegistryValue; $max=[uint32]($_.ValidRegistryValueDisplayNames | Sort-Object { [uint32]$_ } | Select-Object -Last 1); $cur -lt $max }).Count -eq 0) { 'APPLIED' } else { 'BELOW' }",
            cancellationToken);
        return output.Trim().EndsWith("APPLIED", StringComparison.Ordinal);
    }

    private async Task<ExecutionResult> SetFsutilAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetCommandToggleAsync(@"Windows\Storage\Fsutil",
            "fsutil behavior set disable8dot3 1; fsutil behavior set disablelastaccess 1",
            "fsutil behavior set disable8dot3 2; fsutil behavior set disablelastaccess 2",
            IsFsutilTweakedAsync, enabled, cancellationToken);

    private static async Task<bool> IsFsutilTweakedAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync("fsutil behavior query disable8dot3; fsutil behavior query disablelastaccess", cancellationToken);
        MatchCollection matches = Regex.Matches(output, @"=\s*(\d)");
        return matches.Count >= 2 && matches.All(match => match.Groups[1].Value == "1");
    }

    private async Task<ExecutionResult> SetReservedStorageAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetCommandToggleAsync(@"Windows\Storage\ReservedStorage",
            "DISM /Online /Set-ReservedStorageState /State:Disabled",
            "DISM /Online /Set-ReservedStorageState /State:Enabled",
            IsReservedStorageDisabledAsync, enabled, cancellationToken);

    private static async Task<bool> IsReservedStorageDisabledAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync("DISM /Online /Get-ReservedStorageState /English", cancellationToken);
        return output.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ExecutionResult> SetTelemetryTasksAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetCommandToggleAsync(@"Windows\Tasks\TelemetryTasks",
            TelemetryTasksCommand(disable: true),
            TelemetryTasksCommand(disable: false),
            AreTelemetryTasksDisabledAsync, enabled, cancellationToken);

    private static string TelemetryTasksCommand(bool disable) =>
        "$tasks = @('\\Microsoft\\Windows\\Application Experience\\Microsoft Compatibility Appraiser','\\Microsoft\\Windows\\Application Experience\\ProgramDataUpdater','\\Microsoft\\Windows\\Customer Experience Improvement Program\\Consolidator','\\Microsoft\\Windows\\Customer Experience Improvement Program\\UsbCeip','\\Microsoft\\Windows\\DiskDiagnostic\\Microsoft-Windows-DiskDiagnosticDataCollector'); foreach ($t in $tasks) { schtasks /Change /TN \"$t\" " + (disable ? "/DISABLE" : "/ENABLE") + " 2>$null }";

    private static async Task<bool> AreTelemetryTasksDisabledAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync(
            "$tasks = @('\\Microsoft\\Windows\\Application Experience\\Microsoft Compatibility Appraiser','\\Microsoft\\Windows\\Application Experience\\ProgramDataUpdater','\\Microsoft\\Windows\\Customer Experience Improvement Program\\Consolidator','\\Microsoft\\Windows\\Customer Experience Improvement Program\\UsbCeip','\\Microsoft\\Windows\\DiskDiagnostic\\Microsoft-Windows-DiskDiagnosticDataCollector'); $found = 0; $disabled = 0; foreach ($t in $tasks) { $task = Get-ScheduledTask -TaskPath ($t | Split-Path -Parent) -TaskName ($t | Split-Path -Leaf) -ErrorAction SilentlyContinue; if ($task) { $found++; if ($task.State -eq 'Disabled') { $disabled++ } } }; if ($found -gt 0 -and $disabled -eq $found) { 'APPLIED' } else { 'ACTIVE' }",
            cancellationToken);
        return output.Trim().EndsWith("APPLIED", StringComparison.Ordinal);
    }

    private async Task<ExecutionResult> SetTelemetryFirewallAsync(bool enabled, CancellationToken cancellationToken) =>
        await SetCommandToggleAsync(@"Windows\Firewall\TelemetryBlock",
            TelemetryFirewallCommand(create: true),
            TelemetryFirewallCommand(create: false),
            AreTelemetryFirewallRulesPresentAsync, enabled, cancellationToken);

    private static string TelemetryFirewallCommand(bool create) =>
        "$exes = @('CompatTelRunner.exe','DiagTrackRunner.exe','WerFault.exe'); $i = 0; foreach ($exe in $exes) { $i++; if (" + (create ? "$true" : "$false") + ") { New-NetFirewallRule -DisplayName ('ApexKernel-TelBlock-' + $i) -Direction Outbound -Action Block -Program (Join-Path $env:windir (Join-Path 'System32' $exe)) -ErrorAction SilentlyContinue | Out-Null } else { Get-NetFirewallRule -DisplayName 'ApexKernel-TelBlock-*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue } }";

    private static async Task<bool> AreTelemetryFirewallRulesPresentAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync(
            "$rules = Get-NetFirewallRule -DisplayName 'ApexKernel-TelBlock-*' -ErrorAction SilentlyContinue; if (@($rules).Count -ge 3) { 'APPLIED' } else { 'ACTIVE' }",
            cancellationToken);
        return output.Trim().EndsWith("APPLIED", StringComparison.Ordinal);
    }

    private static async Task<string> RunPowerShellStaticAsync(string command, CancellationToken cancellationToken)
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
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error.Trim());
        return output;
    }

    // ---------- Conjuntos de servicios ----------

    private static readonly (int Id, string[] Services, uint TargetStart, bool StartAfter, string Label)[] ServiceSetTable =
    [
        (132, ["XblAuthManager", "XblGameSave", "XboxNetApiSvc", "GamingServices", "DoSvc", "WinHttpAutoProxySvc", "CryptSvc", "iphlpsvc", "NcbService"], 3, true, "Perfil de rescate"),
        (133, ["WlanSvc"], 4, false, "Wi-Fi"),
        (134, ["bthserv", "BTAGService", "BthAvctpSvc", "BthEnum", "BthPan", "BTHPORT", "BTHUSB"], 4, false, "Bluetooth"),
        (135, ["Spooler", "PrintNotify"], 4, false, "Impresoras"),
        (165, ["LanmanServer"], 4, false, "Uso compartido de archivos"),
        (178, ["DiagTrack"], 2, true, "Telemetría restaurada"),
        (179, ["InstallService", "AppXSvc"], 3, false, "Microsoft Store"),
        (186, ["SysMain"], 4, false, "SysMain")
    ];

    private static (string[] Services, uint TargetStart, bool StartAfter, string Label) GetServiceSet(int id) =>
        ServiceSetTable.First(entry => entry.Id == id) is { } entry ? (entry.Services, entry.TargetStart, entry.StartAfter, entry.Label) : throw new InvalidOperationException();

    private static string ServiceSetBackupKey(int id, string service) => @"Windows\Services\Ext\" + id + "\\" + service;

    private async Task<bool> IsServiceSetAppliedAsync(int id, CancellationToken cancellationToken)
    {
        (string[] services, uint target, _, _) = GetServiceSet(id);
        int existing = 0;
        foreach (string service in services)
        {
            ServiceStateSnapshot state = await ReadServiceStateAsync(service, cancellationToken);
            existing++;
            if (state.StartType != target) return false;
        }
        return existing > 0;
    }

    private async Task<bool> IsServiceSetBackedUpAsync(int id, CancellationToken cancellationToken)
    {
        (string[] services, _, _, _) = GetServiceSet(id);
        foreach (string service in services)
            if (await _backup.ReadOriginalAsync<ServiceStateSnapshot>(ServiceSetBackupKey(id, service), cancellationToken) is null)
                return false;
        return true;
    }

    private async Task<ExecutionResult> SetServiceSetAsync(int id, bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Cambiar servicios de Windows requiere ejecutar la aplicación como administrador.");
        (string[] services, uint target, bool startAfter, string label) = GetServiceSet(id);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                var originals = new List<(string Service, ServiceStateSnapshot State)>();
                foreach (string service in services)
                {
                    ServiceStateSnapshot state = await ReadServiceStateAsync(service, cancellationToken);
                    if (state.CurrentState is not (ServiceStopped or ServiceRunning))
                        return new(false, $"{service} está en transición; espera a que termine y vuelve a intentarlo.");
                    originals.Add((service, state));
                }
                foreach ((string service, ServiceStateSnapshot state) in originals)
                    await _backup.SaveOriginalAsync(ServiceSetBackupKey(id, service), state, cancellationToken);
                try
                {
                    foreach (string service in services)
                    {
                        ChangeServiceStartType(service, target);
                        if (target == ServiceDisabled)
                        {
                            ServiceStatusProcess status = QueryServiceStatusByName(service);
                            if (status.CurrentState == ServiceRunning) await SetServiceRunningAsync(service, false, cancellationToken);
                        }
                        else if (startAfter)
                        {
                            ServiceStatusProcess status = QueryServiceStatusByName(service);
                            if (status.CurrentState != ServiceRunning) await SetServiceRunningAsync(service, true, cancellationToken);
                        }
                    }
                }
                catch
                {
                    foreach ((string service, ServiceStateSnapshot state) in originals)
                    {
                        try { await RestoreServiceStateAsync(service, state, CancellationToken.None); } catch { }
                        try { await _backup.RemoveOriginalAsync(ServiceSetBackupKey(id, service), CancellationToken.None); } catch { }
                    }
                    throw;
                }
                return new(true, $"{label}: {services.Length} servicio(s) configurados según lo solicitado; se respaldó el estado previo de cada uno.");
            }
            foreach (string service in services)
            {
                ServiceStateSnapshot? original = await _backup.ReadOriginalAsync<ServiceStateSnapshot>(ServiceSetBackupKey(id, service), cancellationToken);
                if (original is null) return new(false, $"Falta el respaldo de {service}; no se restauró nada para evitar una reversión parcial.");
            }
            foreach (string service in services)
            {
                ServiceStateSnapshot original = (await _backup.ReadOriginalAsync<ServiceStateSnapshot>(ServiceSetBackupKey(id, service), cancellationToken))!;
                try { await RestoreServiceStateAsync(service, original, cancellationToken); }
                catch { }
                await _backup.RemoveOriginalAsync(ServiceSetBackupKey(id, service), cancellationToken);
            }
            return new(true, $"{label}: se restauró el tipo de inicio y el estado original de cada servicio.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or Win32Exception or TimeoutException)
        {
            return new(false, $"No se pudo cambiar el conjunto de servicios: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    // ---------- Wi-Fi (#133): WlanSvc + adaptador físico (Disable/Enable-NetAdapter) ----------

    private static string EscapePsSingleQuotes(string value) => value.Replace("'", "''");

    private static async Task<(string Name, string Status)?> ReadWifiAdapterAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellAsync(
            "$ErrorActionPreference='Stop'; Get-NetAdapter | Where-Object { $_.InterfaceType -eq 'Wireless' -and $_.Status -ne 'Not Present' } | Select-Object -First 1 | ForEach-Object { $_.Name + '|' + $_.Status }",
            cancellationToken);
        string line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        int separator = line.IndexOf('|');
        if (separator <= 0 || separator == line.Length - 1) return null;
        return (line[..separator].Trim(), line[(separator + 1)..].Trim());
    }

    private async Task<bool> IsWifiSetAppliedAsync(CancellationToken cancellationToken)
    {
        ServiceStateSnapshot service = await ReadServiceStateAsync("WlanSvc", cancellationToken);
        if (service.StartType != ServiceDisabled) return false;
        (string Name, string Status)? adapter = await ReadWifiAdapterAsync(cancellationToken);
        return adapter is not null && string.Equals(adapter.Value.Status, "Disabled", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsWifiSetBackedUpAsync(CancellationToken cancellationToken) =>
        await _backup.ReadOriginalAsync<ServiceStateSnapshot>(ServiceSetBackupKey(133, "WlanSvc"), cancellationToken) is not null &&
        await _backup.ReadOriginalAsync<WifiAdapterBackup>(WifiAdapterBackupKey, cancellationToken) is not null;

    private async Task<ExecutionResult> SetWifiSetAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Cambiar servicios de Windows requiere ejecutar la aplicación como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                if (await _backup.ReadOriginalAsync<ServiceStateSnapshot>(ServiceSetBackupKey(133, "WlanSvc"), cancellationToken) is not null ||
                    await _backup.ReadOriginalAsync<WifiAdapterBackup>(WifiAdapterBackupKey, cancellationToken) is not null)
                    return new(false, "Ya hay un respaldo pendiente de Wi-Fi; restáuralo antes de aplicar de nuevo.");
                ServiceStateSnapshot service = await ReadServiceStateAsync("WlanSvc", cancellationToken);
                if (service.CurrentState is not (ServiceStopped or ServiceRunning))
                    return new(false, "WlanSvc está en transición; espera a que termine y vuelve a intentarlo.");
                (string Name, string Status)? adapter = await ReadWifiAdapterAsync(cancellationToken);
                if (adapter is null) return new(false, "No se encontró ningún adaptador Wi-Fi físico; no se cambió nada.");
                string adapterName = EscapePsSingleQuotes(adapter.Value.Name);
                await _backup.SaveOriginalAsync(ServiceSetBackupKey(133, "WlanSvc"), service, cancellationToken);
                await _backup.SaveOriginalAsync(WifiAdapterBackupKey, new WifiAdapterBackup(adapter.Value.Name, adapter.Value.Status), cancellationToken);
                try
                {
                    await RunPowerShellAsync("Disable-NetAdapter -Name '" + adapterName + "' -Confirm:$false -ErrorAction Stop", cancellationToken);
                    ChangeServiceStartType("WlanSvc", ServiceDisabled);
                    ServiceStatusProcess status = QueryServiceStatusByName("WlanSvc");
                    if (status.CurrentState == ServiceRunning) await SetServiceRunningAsync("WlanSvc", false, cancellationToken);
                }
                catch
                {
                    try { await RestoreServiceStateAsync("WlanSvc", service, CancellationToken.None); } catch { }
                    try
                    {
                        if (!string.Equals(adapter.Value.Status, "Disabled", StringComparison.OrdinalIgnoreCase))
                            await RunPowerShellAsync("Enable-NetAdapter -Name '" + adapterName + "' -Confirm:$false -ErrorAction Stop", CancellationToken.None);
                    }
                    catch { }
                    try { await _backup.RemoveOriginalAsync(ServiceSetBackupKey(133, "WlanSvc"), CancellationToken.None); } catch { }
                    try { await _backup.RemoveOriginalAsync(WifiAdapterBackupKey, CancellationToken.None); } catch { }
                    throw;
                }
                return new(true, "Wi-Fi desactivado: WlanSvc quedó deshabilitado y el adaptador físico " + adapter.Value.Name +
                    " se desactivó con Disable-NetAdapter; se respaldó el estado previo de ambos.");
            }

            ServiceStateSnapshot? originalService = await _backup.ReadOriginalAsync<ServiceStateSnapshot>(ServiceSetBackupKey(133, "WlanSvc"), cancellationToken);
            WifiAdapterBackup? originalAdapter = await _backup.ReadOriginalAsync<WifiAdapterBackup>(WifiAdapterBackupKey, cancellationToken);
            if (originalService is null || originalAdapter is null)
                return new(false, "Faltan respaldos de Wi-Fi; no se restauró nada para evitar una reversión parcial.");
            try
            {
                if (!string.Equals(originalAdapter.Status, "Disabled", StringComparison.OrdinalIgnoreCase))
                    await RunPowerShellAsync("Enable-NetAdapter -Name '" + EscapePsSingleQuotes(originalAdapter.Name) + "' -Confirm:$false -ErrorAction Stop", cancellationToken);
            }
            catch { }
            try { await RestoreServiceStateAsync("WlanSvc", originalService, cancellationToken); }
            catch { }
            await _backup.RemoveOriginalAsync(ServiceSetBackupKey(133, "WlanSvc"), cancellationToken);
            await _backup.RemoveOriginalAsync(WifiAdapterBackupKey, cancellationToken);
            return new(true, "Se restauraron exactamente el estado original del adaptador Wi-Fi y de WlanSvc.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or Win32Exception or TimeoutException)
        {
            return new(false, $"No se pudo cambiar el estado de Wi-Fi: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    // ---------- BCD ----------

    private async Task<ExecutionResult> SetBcdValueAsync(string option, string desired, bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Modificar el almacén de arranque requiere ejecutar la aplicación como administrador.");
        string backupKey = BcdBackupKey(option);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? current = await ReadBcdOptionAsync(option, cancellationToken);
            if (enabled)
            {
                if (await _backup.ReadOriginalAsync<BcdOptionSnapshot>(backupKey, cancellationToken) is not null)
                    return new(false, $"Ya existe un respaldo de {option}; restáuralo antes de aplicar de nuevo.");
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
                    catch { }
                    throw;
                }
                return new(true, $"Se configuró {option}={desired} en el BCD; reinicia para que surta efecto. El estado previo quedó respaldado.");
            }
            BcdOptionSnapshot? original = await _backup.ReadOriginalAsync<BcdOptionSnapshot>(backupKey, cancellationToken);
            if (original is null) return new(false, $"No hay respaldo original de {option}; no se modificó el BCD.");
            await RestoreBcdOptionAsync(option, original, cancellationToken);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, $"Se restauró el valor original de {option}. Reinicia para que surta efecto.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or OperationCanceledException or Win32Exception or JsonException)
        {
            return new(false, $"No se pudo cambiar {option}: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetBootTimeoutAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Modificar el arranque requiere ejecutar la aplicación como administrador.");
        const string backupKey = @"Windows\BCD\timeout";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                int current = await ReadBcdTimeoutAsync(cancellationToken);
                if (await _backup.HasBackupAsync(backupKey, cancellationToken))
                    return new(false, "Ya existe un respaldo del tiempo de espera de arranque; restáuralo antes de aplicar de nuevo.");
                await _backup.SaveOriginalAsync(backupKey, current, cancellationToken);
                try { await RunBcdEditAsync(["/timeout", "3"], cancellationToken); }
                catch
                {
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, "Tiempo de espera del menú de arranque reducido a 3 segundos; se respaldó el valor previo.");
            }
            if (!await _backup.HasBackupAsync(backupKey, cancellationToken))
                return new(false, "No hay respaldo del tiempo de espera de arranque; no se cambió nada.");
            int saved = await _backup.ReadOriginalAsync<int>(backupKey, cancellationToken);
            await RunBcdEditAsync(["/timeout", saved.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken);
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, $"Se restauró el tiempo de espera de arranque ({saved} segundos).");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or OperationCanceledException or Win32Exception or JsonException)
        {
            return new(false, $"No se pudo cambiar el arranque: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static async Task<int> ReadBcdTimeoutAsync(CancellationToken cancellationToken)
    {
        string output = await RunBcdEditAsync([], cancellationToken);
        foreach (string line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = Regex.Match(line, @"^\s*timeout\s+(\d+)\s*$", RegexOptions.IgnoreCase);
            if (match.Success) return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return 30;
    }

    // ---------- Demonio ----------

    private async Task<ExecutionResult> SetDaemonFlagAsync(int id, bool enabled, CancellationToken cancellationToken)
    {
        const string backupKey = @"Daemon\Pre";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                if (!await _backup.HasBackupAsync(backupKey + "\\" + id, cancellationToken))
                {
                    bool previous = DaemonConfigStore.GetFlag(id.ToString());
                    await _backup.SaveOriginalAsync(backupKey + "\\" + id, previous, cancellationToken);
                }
                DaemonConfigStore.SetFlag(id.ToString(), true);
                return new(true, "El demonio nativo (ProcessLassoNativeService) aplicará este comportamiento en tiempo real; el estado previo quedó respaldado.");
            }
            if (!await _backup.HasBackupAsync(backupKey + "\\" + id, cancellationToken))
                return new(false, "No hay respaldo del estado previo de este interruptor del demonio; no se cambió nada.");
            bool original = await _backup.ReadOriginalAsync<bool>(backupKey + "\\" + id, cancellationToken);
            DaemonConfigStore.SetFlag(id.ToString(), original);
            await _backup.RemoveOriginalAsync(backupKey + "\\" + id, cancellationToken);
            return new(true, "Se restauró el estado previo del interruptor del demonio.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar la configuración del demonio: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetTimerResolutionItemAsync(OptimizationItem item, bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                if (item.RegistryChanges.Count != 1) return new(false, "El ajuste de resolución del temporizador no está bien configurado en el catálogo.");
                RegistryChange change = item.RegistryChanges[0];
                string registryBackupKey = MakeBackupKey(change);
                RegistryValueSnapshot originalRegistry = ReadSnapshot(change);
                await _backup.SaveOriginalAsync(registryBackupKey, originalRegistry, cancellationToken);
                await _backup.SaveOriginalAsync(@"Daemon\Pre\31", DaemonConfigStore.GetFlag("31"), cancellationToken);
                try
                {
                    WriteValue(change, change.ApplyValue);
                    DaemonConfigStore.SetFlag("31", true);
                }
                catch
                {
                    try { RestoreValue(change, originalRegistry); } catch { }
                    try { DaemonConfigStore.RemoveFlag("31"); } catch { }
                    await _backup.RemoveOriginalAsync(registryBackupKey, CancellationToken.None);
                    await _backup.RemoveOriginalAsync(@"Daemon\Pre\31", CancellationToken.None);
                    throw;
                }
                return new(true, "Resolución global del temporizador a 0.5 ms activada en el demonio nativo y GlobalTimerResolutionRequests=1; el estado previo quedó respaldado.");
            }
            RegistryValueSnapshot? savedRegistry = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(item.RegistryChanges[0]), cancellationToken);
            bool hasFlagBackup = await _backup.HasBackupAsync(@"Daemon\Pre\31", cancellationToken);
            if (savedRegistry is null || !hasFlagBackup)
                return new(false, "Falta uno de los respaldos del temporizador; no se cambió nada para evitar una reversión parcial.");
            bool savedFlag = await _backup.ReadOriginalAsync<bool>(@"Daemon\Pre\31", cancellationToken);
            RestoreValue(item.RegistryChanges[0], savedRegistry);
            DaemonConfigStore.SetFlag("31", savedFlag);
            await _backup.RemoveOriginalAsync(MakeBackupKey(item.RegistryChanges[0]), cancellationToken);
            await _backup.RemoveOriginalAsync(@"Daemon\Pre\31", cancellationToken);
            return new(true, "Se restauraron el registro y el interruptor del temporizador de alta resolución.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return new(false, $"No se pudo cambiar la resolución del temporizador: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    // ---------- Dispositivos PnP (audio fantasma / MIDI) ----------

    private async Task<ExecutionResult> SetPnpDevicesAsync(int id, bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Deshabilitar dispositivos requiere ejecutar la aplicación como administrador.");
        const string backupKey = @"Windows\Devices\Pnp";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string matchScript = id switch
            {
                85 => "Get-PnpDevice -Class MEDIA -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'DISPLAY\\*' }",
                _ => "Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -like '*Wavetable Synth*' -or $_.FriendlyName -like '*Sintetizador*' }"
            };
            if (enabled)
            {
                string instances = await RunPowerShellAsync(
                    $"($ids = ({matchScript} | Select-Object -ExpandProperty InstanceId)); if ($ids) {{ $ids -join ';' }} else {{ '' }}", cancellationToken);
                var list = instances.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (list.Count == 0) return new(false, "No se encontraron dispositivos que coincidan; no se cambió nada.");
                if (await _backup.ReadOriginalAsync<InstanceListBackup>(backupKey + "\\" + id, cancellationToken) is not null)
                    return new(false, "Ya existe un respaldo de esta desactivación; restáuralo antes de aplicar de nuevo.");
                await _backup.SaveOriginalAsync(backupKey + "\\" + id, new InstanceListBackup(list), cancellationToken);
                try
                {
                    await RunPowerShellAsync($"{matchScript} | Disable-PnpDevice -Confirm:$false -ErrorAction Stop", cancellationToken);
                }
                catch
                {
                    await _backup.RemoveOriginalAsync(backupKey + "\\" + id, CancellationToken.None);
                    throw;
                }
                return new(true, $"Se desactivaron {list.Count} dispositivo(s); se respaldó la lista para poder reactivarlos.");
            }
            InstanceListBackup? saved = await _backup.ReadOriginalAsync<InstanceListBackup>(backupKey + "\\" + id, cancellationToken);
            if (saved is null || saved.InstanceIds.Count == 0)
                return new(false, "No hay respaldo de los dispositivos desactivados; no se cambió nada.");
            foreach (string instanceId in saved.InstanceIds)
            {
                try
                {
                    await RunPowerShellAsync($"Get-PnpDevice -InstanceId '{instanceId.Replace("'", "''")}' -ErrorAction SilentlyContinue | Enable-PnpDevice -Confirm:$false -ErrorAction Stop", cancellationToken);
                }
                catch { }
            }
            await _backup.RemoveOriginalAsync(backupKey + "\\" + id, cancellationToken);
            return new(true, "Se reactivaron los dispositivos respaldados.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or Win32Exception)
        {
            return new(false, $"No se pudo cambiar el estado de los dispositivos: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> AreGhostAudioDevicesDisabledAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync(
            "$d = Get-PnpDevice -Class MEDIA -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'DISPLAY\\*' }; if ($d -eq $null) { 'NONE' } elseif (@($d | Where-Object { $_.Status -eq 'OK' }).Count -eq 0) { 'APPLIED' } else { 'ACTIVE' }",
            cancellationToken);
        return output.Trim().EndsWith("APPLIED", StringComparison.Ordinal);
    }

    private static async Task<bool> IsMidiSynthDisabledAsync(CancellationToken cancellationToken)
    {
        string output = await RunPowerShellStaticAsync(
            "$d = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -like '*Wavetable Synth*' -or $_.FriendlyName -like '*Sintetizador*' }; if ($d -eq $null) { 'NONE' } elseif (@($d | Where-Object { $_.Status -eq 'OK' }).Count -eq 0) { 'APPLIED' } else { 'ACTIVE' }",
            cancellationToken);
        return output.Trim().EndsWith("APPLIED", StringComparison.Ordinal);
    }

    // ---------- Registro dinámico por dispositivo ----------

    private async Task<ExecutionResult> SetClassCapabilityAsync(int id, string backupKey, string[] classRoots, string valueName, bool requireNetCfg, bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Cambiar propiedades de dispositivos requiere ejecutar la aplicación como administrador.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = new List<DevicePowerKeySnapshot>();
            foreach (string root in classRoots)
            {
                using RegistryKey? parent = Registry.LocalMachine.OpenSubKey(root, writable: false);
                if (parent is null) continue;
                foreach (string sub in parent.GetSubKeyNames())
                {
                    if (!sub.StartsWith("00", StringComparison.Ordinal)) continue;
                    string path = root + "\\" + sub;
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: false);
                    if (key is null) continue;
                    if (!key.GetValueNames().Contains("DriverDesc", StringComparer.OrdinalIgnoreCase)) continue;
                    if (requireNetCfg && !key.GetValueNames().Contains("NetCfgInstanceId", StringComparer.OrdinalIgnoreCase)) continue;
                    keys.Add(new DevicePowerKeySnapshot(path, [new DevicePowerValueSnapshot(valueName, ReadSnapshot(new RegistryChange { Hive = "HKLM", KeyPath = path, ValueName = valueName, ValueKind = "DWord", ApplyValue = "0", RevertValue = "0" }))]));
                }
            }
            if (keys.Count == 0) return new(false, "No se encontraron dispositivos compatibles; no se cambió nada.");
            if (enabled)
            {
                if (await _backup.ReadOriginalAsync<DevicePowerSnapshot>(backupKey, cancellationToken) is not null)
                    return new(false, "Ya existe un respaldo de estos dispositivos; restáuralo antes de aplicar de nuevo.");
                await _backup.SaveOriginalAsync(backupKey, new DevicePowerSnapshot(keys), cancellationToken);
                try
                {
                    foreach (DevicePowerKeySnapshot keySnapshot in keys)
                        WriteValue(new RegistryChange { Hive = "HKLM", KeyPath = keySnapshot.RegistryPath, ValueName = valueName, ValueKind = "DWord", ApplyValue = "24", RevertValue = "0" }, "24");
                }
                catch
                {
                    try
                    {
                        DevicePowerSnapshot snapshot = new(keys);
                        foreach (DevicePowerKeySnapshot keySnapshot in keys)
                            foreach (DevicePowerValueSnapshot value in keySnapshot.Values)
                                RestoreValue(new RegistryChange { Hive = "HKLM", KeyPath = keySnapshot.RegistryPath, ValueName = value.Name, ValueKind = "DWord", ApplyValue = "0", RevertValue = "0" }, value.Original);
                    }
                    catch { }
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, $"Se desactivó el ahorro de energía ({valueName}=24) en {keys.Count} dispositivo(s); el estado previo quedó respaldado.");
            }
            DevicePowerSnapshot? saved = await _backup.ReadOriginalAsync<DevicePowerSnapshot>(backupKey, cancellationToken);
            if (saved is null) return new(false, "No hay respaldo de estos dispositivos; no se cambió nada.");
            foreach (DevicePowerKeySnapshot keySnapshot in saved.Keys)
                foreach (DevicePowerValueSnapshot value in keySnapshot.Values)
                {
                    try
                    {
                        RestoreValue(new RegistryChange { Hive = "HKLM", KeyPath = keySnapshot.RegistryPath, ValueName = value.Name, ValueKind = "DWord", ApplyValue = "0", RevertValue = "0" }, value.Original);
                    }
                    catch { }
                }
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauró la configuración de energía original de los dispositivos.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or System.Security.SecurityException)
        {
            return new(false, $"No se pudo cambiar la configuración de dispositivos: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetHdmiIdleAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Cambiar la suspensión del audio HDMI requiere ejecutar la aplicación como administrador.");
        const string backupKey = @"Windows\Audio\HdmiIdle";
        string[] valueNames = ["ConservationIdleTime", "PerformanceIdleTime"];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = new List<DevicePowerKeySnapshot>();
            using (RegistryKey? parent = Registry.LocalMachine.OpenSubKey(AudioClassRoot, writable: false))
            {
                if (parent is null) return new(false, "No se pudo abrir la clase de dispositivos de audio.");
                foreach (string sub in parent.GetSubKeyNames())
                {
                    if (!sub.StartsWith("00", StringComparison.Ordinal)) continue;
                    string path = AudioClassRoot + "\\" + sub + "\\PowerSettings";
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: false);
                    if (key is null) continue;
                    var values = new List<DevicePowerValueSnapshot>();
                    foreach (string name in valueNames)
                    {
                        RegistryChange change = new() { Hive = "HKLM", KeyPath = path, ValueName = name, ValueKind = "Binary", ApplyValue = ZeroBinaryValue, RevertValue = ZeroBinaryValue };
                        values.Add(new DevicePowerValueSnapshot(name, ReadSnapshot(change)));
                    }
                    keys.Add(new DevicePowerKeySnapshot(path, values));
                }
            }
            if (keys.Count == 0) return new(false, "No se encontraron dispositivos de audio con PowerSettings; no se cambió nada.");
            if (enabled)
            {
                if (await _backup.ReadOriginalAsync<DevicePowerSnapshot>(backupKey, cancellationToken) is not null)
                    return new(false, "Ya existe un respaldo del audio HDMI; restáuralo antes de aplicar de nuevo.");
                await _backup.SaveOriginalAsync(backupKey, new DevicePowerSnapshot(keys), cancellationToken);
                try
                {
                    foreach (DevicePowerKeySnapshot keySnapshot in keys)
                        foreach (string name in valueNames)
                            WriteValue(new RegistryChange { Hive = "HKLM", KeyPath = keySnapshot.RegistryPath, ValueName = name, ValueKind = "Binary", ApplyValue = ZeroBinaryValue, RevertValue = ZeroBinaryValue }, ZeroBinaryValue);
                }
                catch
                {
                    try
                    {
                        DevicePowerSnapshot snapshot = new(keys);
                        foreach (DevicePowerKeySnapshot keySnapshot in snapshot.Keys)
                            foreach (DevicePowerValueSnapshot value in keySnapshot.Values)
                                RestoreValue(new RegistryChange { Hive = "HKLM", KeyPath = keySnapshot.RegistryPath, ValueName = value.Name, ValueKind = "Binary", ApplyValue = ZeroBinaryValue, RevertValue = ZeroBinaryValue }, value.Original);
                    }
                    catch { }
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, $"Se eliminó la suspensión automática del audio HDMI/DisplayPort en {keys.Count} dispositivo(s).");
            }
            DevicePowerSnapshot? saved = await _backup.ReadOriginalAsync<DevicePowerSnapshot>(backupKey, cancellationToken);
            if (saved is null) return new(false, "No hay respaldo del audio HDMI; no se cambió nada.");
            foreach (DevicePowerKeySnapshot keySnapshot in saved.Keys)
                foreach (DevicePowerValueSnapshot value in keySnapshot.Values)
                {
                    try
                    {
                        RestoreValue(new RegistryChange { Hive = "HKLM", KeyPath = keySnapshot.RegistryPath, ValueName = value.Name, ValueKind = "Binary", ApplyValue = ZeroBinaryValue, RevertValue = ZeroBinaryValue }, value.Original);
                    }
                    catch { }
                }
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauraron los tiempos de suspensión originales del audio HDMI.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or System.Security.SecurityException)
        {
            return new(false, $"No se pudo cambiar el audio HDMI: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetNagleAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Cambiar la configuración TCP requiere ejecutar la aplicación como administrador.");
        const string backupKey = @"Windows\NIC\Nagle";
        const string interfacesRoot = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        (string Name, string Kind, string Apply)[] targets = [("TcpAckFrequency", "DWord", "1"), ("TCPNoDelay", "DWord", "1"), ("TcpDelAckTicks", "DWord", "0")];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = new List<DevicePowerKeySnapshot>();
            using (RegistryKey? parent = Registry.LocalMachine.OpenSubKey(interfacesRoot, writable: false))
            {
                if (parent is null) return new(false, "No se pudo abrir la lista de interfaces TCP/IP.");
                foreach (string sub in parent.GetSubKeyNames())
                {
                    string path = interfacesRoot + "\\" + sub;
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path, writable: false);
                    if (key is null || !IsActiveTcpInterface(key)) continue;
                    var values = new List<DevicePowerValueSnapshot>();
                    foreach ((string name, string kind, string _) in targets)
                        values.Add(new DevicePowerValueSnapshot(name, ReadSnapshot(new RegistryChange { Hive = "HKLM", KeyPath = path, ValueName = name, ValueKind = kind, ApplyValue = "0", RevertValue = "0" })));
                    keys.Add(new DevicePowerKeySnapshot(path, values));
                }
            }
            if (keys.Count == 0) return new(false, "No se encontraron interfaces TCP activas; no se cambió nada.");
            if (enabled)
            {
                if (await _backup.ReadOriginalAsync<DevicePowerSnapshot>(backupKey, cancellationToken) is not null)
                    return new(false, "Ya existe un respaldo del algoritmo de Nagle; restáuralo antes de aplicar de nuevo.");
                await _backup.SaveOriginalAsync(backupKey, new DevicePowerSnapshot(keys), cancellationToken);
                try
                {
                    foreach (DevicePowerKeySnapshot keySnapshot in keys)
                        foreach ((string name, string kind, string apply) in targets)
                            WriteValue(new RegistryChange { Hive = "HKLM", KeyPath = keySnapshot.RegistryPath, ValueName = name, ValueKind = kind, ApplyValue = apply, RevertValue = "0" }, apply);
                }
                catch
                {
                    try
                    {
                        DevicePowerSnapshot snapshot = new(keys);
                        foreach (DevicePowerKeySnapshot keySnapshot in snapshot.Keys)
                            foreach (DevicePowerValueSnapshot value in keySnapshot.Values)
                                RestoreValue(new RegistryChange { Hive = "HKLM", KeyPath = keySnapshot.RegistryPath, ValueName = value.Name, ValueKind = value.Original.Kind, ApplyValue = "0", RevertValue = "0" }, value.Original);
                    }
                    catch { }
                    await _backup.RemoveOriginalAsync(backupKey, CancellationToken.None);
                    throw;
                }
                return new(true, $"Algoritmo de Nagle desactivado en {keys.Count} interfaz(es) TCP activas (TcpAckFrequency=1, TCPNoDelay=1, TcpDelAckTicks=0).");
            }
            DevicePowerSnapshot? saved = await _backup.ReadOriginalAsync<DevicePowerSnapshot>(backupKey, cancellationToken);
            if (saved is null) return new(false, "No hay respaldo del algoritmo de Nagle; no se cambió nada.");
            foreach (DevicePowerKeySnapshot keySnapshot in saved.Keys)
                foreach (DevicePowerValueSnapshot value in keySnapshot.Values)
                {
                    try
                    {
                        RestoreValue(new RegistryChange { Hive = "HKLM", KeyPath = keySnapshot.RegistryPath, ValueName = value.Name, ValueKind = value.Original.Kind, ApplyValue = "0", RevertValue = "0" }, value.Original);
                    }
                    catch { }
                }
            await _backup.RemoveOriginalAsync(backupKey, cancellationToken);
            return new(true, "Se restauró la configuración TCP original de cada interfaz.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or System.Security.SecurityException)
        {
            return new(false, $"No se pudo cambiar el algoritmo de Nagle: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    // ---------- Carpetas SystemApps (TrustedInstaller) ----------

    private async Task<ExecutionResult> SetSystemAppRenameAsync(int id, string[] candidates, string processName, bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Renombrar carpetas de SystemApps requiere ejecutar la aplicación como administrador.");
        const string backupKey = @"Windows\SystemApps";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? target = null;
            foreach (string folder in candidates)
            {
                if (Directory.Exists(Path.Combine(SystemAppsRoot, folder))) { target = folder; break; }
            }
            if (enabled)
            {
                if (IsSystemAppRenamed(candidates))
                {
                    if (await _backup.HasBackupAsync(backupKey + "\\" + id, cancellationToken))
                        return new(true, "El componente ya estaba desactivado; el respaldo se conserva para poder revertir.");
                }
                if (target is null) return new(false, "No se encontró la carpeta del componente en SystemApps; no se cambió nada.");
                await _backup.SaveOriginalAsync(backupKey + "\\" + id, true, cancellationToken);
                try
                {
                    foreach (Process process in Process.GetProcessesByName(processName.Replace(".exe", "")))
                    {
                        try { process.Kill(); } catch { }
                        process.Dispose();
                    }
                    await SetServiceRunningAsync("TrustedInstaller", true, cancellationToken);
                    await TrustedInstallerRunner.RunAsync($"ren \"{Path.Combine(SystemAppsRoot, target)}\" \"{target}_off\"", cancellationToken);
                }
                catch
                {
                    await _backup.RemoveOriginalAsync(backupKey + "\\" + id, CancellationToken.None);
                    throw;
                }
                if (!IsSystemAppRenamed(candidates))
                    return new(false, "El renombrado no surtió efecto; el componente sigue activo. El respaldo se conserva para reintentar la reversión.");
                return new(true, $"Se desactivó el componente renombrando su carpeta en SystemApps (requiere reiniciar). Se guardó el nombre original; los paneles de emojis/recortes o la búsqueda podrían dejar de responder hasta restaurarlo.");
            }
            if (!await _backup.HasBackupAsync(backupKey + "\\" + id, cancellationToken))
                return new(false, "No hay respaldo de esta desactivación; no se cambió nada.");
            foreach (string folder in candidates)
            {
                string renamed = Path.Combine(SystemAppsRoot, folder + "_off");
                if (Directory.Exists(renamed))
                {
                    await SetServiceRunningAsync("TrustedInstaller", true, cancellationToken);
                    await TrustedInstallerRunner.RunAsync($"ren \"{renamed}\" \"{folder}\"", cancellationToken);
                }
            }
            await _backup.RemoveOriginalAsync(backupKey + "\\" + id, cancellationToken);
            return new(true, "Se restauró la carpeta original del componente (requiere reiniciar).");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or Win32Exception)
        {
            return new(false, $"No se pudo cambiar el componente del sistema: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    // ---------- Discord / navegadores: aceleración por hardware ----------

    private async Task<ExecutionResult> SetDiscordBrowserHwaAsync(OptimizationItem item, bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            const string discordBackupKey = @"Windows\Apps\DiscordHWA";
            const string missingFileMarker = "<apexkernel:discord-settings-ausente>";
            string discordFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "settings.json");
            if (enabled)
            {
                if (item.RegistryChanges.Count != 3) return new(false, "El ajuste de Discord/navegadores no está bien configurado en el catálogo.");
                bool fileExists = File.Exists(discordFile);
                string originalFile = missingFileMarker;
                JsonObject? root = null;
                if (fileExists)
                {
                    originalFile = File.ReadAllText(discordFile);
                    try { root = JsonNode.Parse(originalFile) as JsonObject; }
                    catch (JsonException) { root = null; }
                    if (root is null)
                        return new(false, "El archivo settings.json de Discord no contiene un objeto JSON válido; no se modificó nada para evitar dañarlo.");
                }
                var registryOriginals = new List<(RegistryChange Change, RegistryValueSnapshot Snapshot)>();
                foreach (RegistryChange change in item.RegistryChanges)
                    registryOriginals.Add((change, ReadSnapshot(change)));
                foreach ((RegistryChange change, RegistryValueSnapshot snapshot) in registryOriginals)
                    await _backup.SaveOriginalAsync(MakeBackupKey(change), snapshot, cancellationToken);
                await _backup.SaveOriginalAsync(discordBackupKey, originalFile, cancellationToken);
                try
                {
                    foreach ((RegistryChange change, RegistryValueSnapshot _) in registryOriginals)
                        ApplyRegistryChange(change);
                    if (root is not null)
                    {
                        root["ENABLE_HARDWARE_ACCELERATION"] = false;
                        File.WriteAllText(discordFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch
                {
                    foreach ((RegistryChange change, RegistryValueSnapshot snapshot) in registryOriginals)
                    {
                        try { RestoreValue(change, snapshot); } catch { }
                    }
                    if (fileExists)
                    {
                        try { File.WriteAllText(discordFile, originalFile); } catch { }
                    }
                    foreach ((RegistryChange change, RegistryValueSnapshot _) in registryOriginals)
                    {
                        try { await _backup.RemoveOriginalAsync(MakeBackupKey(change), CancellationToken.None); } catch { }
                    }
                    try { await _backup.RemoveOriginalAsync(discordBackupKey, CancellationToken.None); } catch { }
                    throw;
                }
                return fileExists
                    ? new(true, "Aceleración por hardware desactivada en Discord (ENABLE_HARDWARE_ACCELERATION=false en settings.json) y en las políticas de Chrome/Edge/Brave; todo quedó respaldado.")
                    : new(true, "No se encontró settings.json de Discord; se aplicaron y respaldaron las políticas de Chrome/Edge/Brave.");
            }
            var missing = new List<RegistryChange>();
            foreach (RegistryChange change in item.RegistryChanges)
            {
                if (await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(change), cancellationToken) is null) missing.Add(change);
            }
            if (missing.Count > 0 || await _backup.ReadOriginalAsync<string>(discordBackupKey, cancellationToken) is null)
                return new(false, "Faltan respaldos de esta optimización; no se cambió nada para evitar una reversión parcial.");
            foreach (RegistryChange change in item.RegistryChanges)
            {
                RegistryValueSnapshot? snapshot = await _backup.ReadOriginalAsync<RegistryValueSnapshot>(MakeBackupKey(change), cancellationToken);
                if (snapshot is not null)
                {
                    try { RestoreValue(change, snapshot); } catch { }
                }
                try { await _backup.RemoveOriginalAsync(MakeBackupKey(change), cancellationToken); } catch { }
            }
            string? savedFile = await _backup.ReadOriginalAsync<string>(discordBackupKey, cancellationToken);
            if (savedFile is not null)
            {
                if (savedFile == missingFileMarker)
                {
                    try { if (File.Exists(discordFile)) File.Delete(discordFile); } catch { }
                }
                else
                {
                    try { File.WriteAllText(discordFile, savedFile); } catch { }
                }
                await _backup.RemoveOriginalAsync(discordBackupKey, cancellationToken);
            }
            return new(true, "Se restauraron las políticas de los navegadores y la configuración original de Discord exactamente como estaba.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or System.Security.SecurityException)
        {
            return new(false, $"No se pudo cambiar la aceleración por hardware: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmEnableMMCSS([MarshalAs(UnmanagedType.Bool)] bool fEnable);

    private async Task<ExecutionResult> SetDwmMmcssAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "DwmEnableMMCSS requiere ejecutar la aplicación como administrador.");
        const string key = @"Windows\DWM\MMCSS";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                if (await _backup.ReadOriginalAsync<bool>(key, cancellationToken))
                    return new(false, "Ya hay un respaldo pendiente de DWM MMCSS; restáuralo antes de aplicar de nuevo.");
                int hr = DwmEnableMMCSS(true);
                if (hr < 0) return new(false, $"DwmEnableMMCSS(TRUE) devolvió 0x{hr:X8}.");
                await _backup.SaveOriginalAsync(key, true, cancellationToken);
                return new(true, "DWM registrado en MMCSS con planificación de tiempo crítico.");
            }
            bool saved = await _backup.ReadOriginalAsync<bool>(key, cancellationToken);
            if (!saved) return new(false, "No hay respaldo previo de DWM MMCSS; no se modificó nada.");
            int hr2 = DwmEnableMMCSS(false);
            if (hr2 < 0) return new(false, $"DwmEnableMMCSS(FALSE) devolvió 0x{hr2:X8}.");
            await _backup.RemoveOriginalAsync(key, cancellationToken);
            return new(true, "Se restauró la prioridad MMCSS original de DWM.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or JsonException or OperationCanceledException or System.Security.SecurityException)
        {
            return new(false, $"No se pudo cambiar DWM MMCSS: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetNicEeeGreenAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Cambiar EEE/Green Ethernet requiere ejecutar la aplicación como administrador.");
        const string key = @"Windows\Net\EeeGreen";
        string[] names = ["AdvancedEEE", "GreenEthernet", "EEE"];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, Dictionary<string, uint>>? saved = null;
            if (!enabled)
            {
                saved = await _backup.ReadOriginalAsync<Dictionary<string, Dictionary<string, uint>>>(key, cancellationToken);
                if (saved is null) return new(false, "No hay respaldo previo de EEE/Green Ethernet; no se modificó nada.");
            }
            var touched = new Dictionary<string, Dictionary<string, uint>>(StringComparer.OrdinalIgnoreCase);
            using (RegistryKey? root = Registry.LocalMachine.OpenSubKey(NicClassRoot, true))
            {
                if (root is null) return new(false, "No se encontró la raíz del Registro de adaptadores de red.");
                foreach (string instance in root.GetSubKeyNames())
                {
                    using RegistryKey? adapter = root.OpenSubKey(instance, true);
                    if (adapter is null) continue;
                    var originals = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in names)
                    {
                        object? value = adapter.GetValue(name);
                        if (value is null) continue;
                        uint original = value is int i ? unchecked((uint)i) : Convert.ToUInt32(value);
                        if (enabled)
                        {
                            originals[name] = original;
                            adapter.SetValue(name, 0u, RegistryValueKind.DWord);
                        }
                        else if (saved!.TryGetValue(instance, out var perAdapter) && perAdapter.TryGetValue(name, out uint restore))
                        {
                            adapter.SetValue(name, restore, RegistryValueKind.DWord);
                        }
                    }
                    if (originals.Count > 0) touched[instance] = originals;
                }
            }
            if (enabled)
            {
                if (touched.Count == 0) return new(false, "No se encontraron adaptadores con valores EEE/Green Ethernet.");
                await _backup.SaveOriginalAsync(key, touched, cancellationToken);
                return new(true, $"EEE/Green Ethernet desactivado en {touched.Count} adaptador(es); valores originales respaldados.");
            }
            await _backup.RemoveOriginalAsync(key, cancellationToken);
            return new(true, "Se restauraron los valores originales de EEE/Green Ethernet.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or System.Security.SecurityException)
        {
            return new(false, $"No se pudo cambiar EEE/Green Ethernet: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    private async Task<ExecutionResult> SetWinsatTaskAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsAdministrator()) return new(false, "Desactivar la tarea WinSAT requiere ejecutar la aplicación como administrador.");
        const string key = @"Windows\Tasks\WinSAT";
        const string task = @"\Microsoft\Windows\Maintenance\WinSAT";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (enabled)
            {
                if (await _backup.ReadOriginalAsync<bool>(key, cancellationToken))
                    return new(false, "Ya hay un respaldo pendiente de la tarea WinSAT; restáurala antes de aplicar de nuevo.");
                string state = await RunPowerShellStaticAsync($"(Get-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Maintenance\\' -TaskName 'WinSAT').State", cancellationToken);
                bool wasEnabled = !state.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
                await _backup.SaveOriginalAsync(key, wasEnabled, cancellationToken);
                await RunPowerShellStaticAsync($@"schtasks /Change /TN ""{task}"" /Disable | Out-Null", cancellationToken);
                return new(true, "Tarea WinSAT deshabilitada; estado original respaldado.");
            }
            bool restore = await _backup.ReadOriginalAsync<bool>(key, cancellationToken);

            if (!restore && !await _backup.HasBackupAsync(key, cancellationToken))
                return new(false, "No hay respaldo previo de la tarea WinSAT; no se modificó nada.");
            if (restore)
                await RunPowerShellStaticAsync($@"schtasks /Change /TN ""{task}"" /Enable | Out-Null", cancellationToken);
            await _backup.RemoveOriginalAsync(key, cancellationToken);
            return new(true, restore ? "Tarea WinSAT restaurada a su estado original." : "Se eliminó el respaldo sin reactivar la tarea (estaba deshabilitada).");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or JsonException or OperationCanceledException or System.Security.SecurityException or Win32Exception)
        {
            return new(false, $"No se pudo cambiar la tarea WinSAT: {ex.Message}");
        }
        finally { _gate.Release(); }
    }}
