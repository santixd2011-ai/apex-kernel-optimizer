using System.Runtime.InteropServices;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ApexKernelOptimizer.Core.Models;

namespace ApexKernelOptimizer.Core.Engine;

public sealed class HardwareAndAnticheatScanner
{
    private static readonly Guid DisplayClassGuid = Guid.Parse("4d36e968-e325-11ce-bfc1-08002be10318");

    public Task<HardwareSnapshot> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private static HardwareSnapshot Scan(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string cpu = ReadCpuName();
        uint activeProcessorCount = GetActiveProcessorCount(ushort.MaxValue);
        int logical = activeProcessorCount is > 0 and <= int.MaxValue ? (int)activeProcessorCount : Environment.ProcessorCount;
        int physical = Math.Max(1, logical / Math.Max(1, GetSmtRatio(logical)));
        bool dualCcdX3d = cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) &&
            cpu.Contains("X3D", StringComparison.OrdinalIgnoreCase) && physical >= 12;
        double memoryGb = GetMemoryGb();
        List<string> gpus = ReadDisplayAdapters(cancellationToken);
        string drive = GetPhysicalStorageType();
        bool battery = HasBattery();
        Version version = Environment.OSVersion.Version;
        string product = version.Major == 10 && version.Build >= 22000 ? "Windows 11" : version.Major == 10 ? "Windows 10" : $"Windows {version.Major}";
        string edition = ReadWindowsEdition();
        int ubr = ReadWindowsUbr();
        string windows = $"{product} {edition} (build {version.Build})";
        IReadOnlyList<string> usbTopology = ReadUsbTopology(cancellationToken);
        (bool? hagsSupported, bool? hagsEnabled) = ReadHardwareGpuSchedulingCapability(version.Build, cancellationToken);
        return new HardwareSnapshot(cpu, logical, physical, dualCcdX3d, memoryGb, gpus, drive, battery,
            ServiceExists("vgk"), ServiceExists("faceit"), ServiceExists("EasyAntiCheat"), ServiceExists("BEService"), windows, edition, version.Build, ubr, usbTopology, hagsSupported, hagsEnabled);
    }

    private static string ReadWindowsEdition()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("EditionID") as string ?? "Unknown edition";
        }
        catch { return "Unknown edition"; }
    }

    private static int ReadWindowsUbr()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return Convert.ToInt32(key?.GetValue("UBR") ?? 0);
        }
        catch { return 0; }
    }

    private static IReadOnlyList<string> ReadUsbTopology(CancellationToken cancellationToken)
    {
        const uint digcfPresent = 0x2, digcfAllClasses = 0x4;
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, digcfPresent | digcfAllClasses);
        if (set == new IntPtr(-1)) return ["No se pudo consultar el árbol Plug and Play."];
        try
        {
            var all = new Dictionary<string, (string Name, string Class, string Parent)>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = new SpDevinfoData { Size = (uint)Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    break;
                }
                string instanceId = GetDeviceId(data.DevInst);
                if (string.IsNullOrWhiteSpace(instanceId)) continue;
                string deviceClass = GetDeviceProperty(set, ref data, 7); // SPDRP_CLASS
                string name = GetDeviceProperty(set, ref data, 12); // SPDRP_FRIENDLYNAME
                if (string.IsNullOrWhiteSpace(name)) name = GetDeviceProperty(set, ref data, 0); // SPDRP_DEVICEDESC
                if (string.IsNullOrWhiteSpace(name)) name = instanceId;
                string parent = GetParentId(data.DevInst);
                all[instanceId] = (name, deviceClass, parent);
            }

            var selected = all.Where(pair =>
                    pair.Key.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase) ||
                    pair.Value.Class.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                    pair.Value.Class.Equals("USBDevice", StringComparison.OrdinalIgnoreCase) ||
                    pair.Value.Class.Equals("HIDClass", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0) return ["No se detectaron dispositivos USB/HID presentes."];

            var children = selected.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
            var roots = new List<string>();
            foreach (var pair in selected)
            {
                if (!string.IsNullOrEmpty(pair.Value.Parent) && children.TryGetValue(pair.Value.Parent, out List<string>? siblings))
                    siblings.Add(pair.Key);
                else roots.Add(pair.Key);
            }
            var lines = new List<string> { $"{selected.Count} dispositivos USB/HID presentes (árbol PnP de Windows):" };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.OrderBy(id => selected[id].Name, StringComparer.OrdinalIgnoreCase))
                AppendDeviceTree(root, 0, selected, children, visited, lines);
            foreach (string orphan in selected.Keys.Where(id => !visited.Contains(id)).OrderBy(id => selected[id].Name, StringComparer.OrdinalIgnoreCase))
                AppendDeviceTree(orphan, 0, selected, children, visited, lines);
            return lines;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return [$"No se pudo leer el árbol USB: {ex.Message}"]; }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static void AppendDeviceTree(string id, int depth,
        IReadOnlyDictionary<string, (string Name, string Class, string Parent)> devices,
        IReadOnlyDictionary<string, List<string>> children, HashSet<string> visited, List<string> lines)
    {
        if (!visited.Add(id) || !devices.TryGetValue(id, out var device)) return;
        lines.Add($"{new string(' ', depth * 2)}{(depth == 0 ? "• " : "└─ ")}{device.Name} [{device.Class}]");
        foreach (string child in children[id].OrderBy(childId => devices[childId].Name, StringComparer.OrdinalIgnoreCase))
            AppendDeviceTree(child, depth + 1, devices, children, visited, lines);
    }

    private static (bool? Supported, bool? Enabled) ReadHardwareGpuSchedulingCapability(int windowsBuild, CancellationToken cancellationToken)
    {
        if (windowsBuild < 19041) return (false, false);
        bool queriedAny = false, supportedAny = false, enabledAny = false;
        try
        {
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var display = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(null, index, ref display, 0)) break;
                if (string.IsNullOrWhiteSpace(display.DeviceName)) continue;
                var open = new D3dkmtOpenAdapterData { DeviceName = display.DeviceName };
                if (D3dkmtOpenAdapterFromGdiDisplayName(ref open) != 0 || open.AdapterHandle == 0) continue;
                try
                {
                    IntPtr capsBuffer = Marshal.AllocHGlobal(sizeof(uint));
                    try
                    {
                        Marshal.WriteInt32(capsBuffer, 0);
                        var query = new D3dkmtQueryAdapterData { AdapterHandle = open.AdapterHandle, Type = 70, PrivateDriverData = capsBuffer, PrivateDriverDataSize = sizeof(uint) };
                        if (D3dkmtQueryAdapterInfo(ref query) != 0) continue;
                        queriedAny = true;
                        uint caps = unchecked((uint)Marshal.ReadInt32(capsBuffer));
                        if ((caps & 1) != 0)
                        {
                            supportedAny = true;
                            if ((caps & 2) != 0) enabledAny = true;
                        }
                    }
                    finally { Marshal.FreeHGlobal(capsBuffer); }
                }
                finally { _ = D3dkmtCloseAdapter(new D3dkmtCloseAdapterData { AdapterHandle = open.AdapterHandle }); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, null); }
        return queriedAny ? (supportedAny, enabledAny) : (null, null);
    }

    private static string GetDeviceId(uint devInst)
    {
        var buffer = new StringBuilder(1024);
        return CmGetDeviceIdW(devInst, buffer, buffer.Capacity, 0) == 0 ? buffer.ToString() : "";
    }

    private static string GetParentId(uint devInst) =>
        CmGetParent(out uint parent, devInst, 0) == 0 ? GetDeviceId(parent) : "";

    private static string GetDeviceProperty(IntPtr set, ref SpDevinfoData data, uint property)
    {
        var buffer = new byte[4096];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out _)) return "";
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct D3dkmtOpenAdapterData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
        public uint AdapterHandle;
        public long AdapterLuid;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtQueryAdapterData
    {
        public uint AdapterHandle;
        public uint Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtCloseAdapterData { public uint AdapterHandle; }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent", CharSet = CharSet.Unicode)]
    private static extern int CmGetParent(out uint parentDevInst, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern int CmGetDeviceIdW(uint devInst, StringBuilder buffer, int bufferLength, uint flags);
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);
    [DllImport("gdi32.dll", EntryPoint = "D3DKMTOpenAdapterFromGdiDisplayName")]
    private static extern int D3dkmtOpenAdapterFromGdiDisplayName(ref D3dkmtOpenAdapterData data);
    [DllImport("gdi32.dll", EntryPoint = "D3DKMTQueryAdapterInfo")]
    private static extern int D3dkmtQueryAdapterInfo(ref D3dkmtQueryAdapterData data);
    [DllImport("gdi32.dll", EntryPoint = "D3DKMTCloseAdapter")]
    private static extern int D3dkmtCloseAdapter(D3dkmtCloseAdapterData data);

    private static int GetSmtRatio(int logicalProcessors)
    {
        uint size = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size);
        if (size == 0) return 1;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref size)) return 1;
            int cores = 0, offset = 0;
            while (offset < size)
            {
                int relationship = Marshal.ReadInt32(buffer, offset);
                int recordSize = Marshal.ReadInt32(buffer, offset + 4);
                if (recordSize <= 0 || offset + recordSize > size) break;
                if (relationship == 0) cores++;
                offset += recordSize;
            }
            return cores > 0 ? Math.Max(1, logicalProcessors / cores) : 1;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static double GetMemoryGb()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? Math.Round(status.TotalPhysical / 1073741824d, 1) : 0;
    }

    private static string ReadCpuName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string
                ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
                ?? "No disponible";
        }
        catch { return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "No disponible"; }
    }

    private static List<string> ReadDisplayAdapters(CancellationToken cancellationToken)
    {
        var adapters = new List<string>();
        IntPtr classGuid = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(DisplayClassGuid, classGuid, false);
        IntPtr set = SetupDiGetClassDevsW(classGuid, null, IntPtr.Zero, 0x2); // DIGCF_PRESENT
        try
        {
            if (set == new IntPtr(-1)) return adapters;
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = new SpDevinfoData { Size = (uint)Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data)) break;
                string name = GetDeviceProperty(set, ref data, 12); // SPDRP_FRIENDLYNAME
                if (string.IsNullOrWhiteSpace(name)) name = GetDeviceProperty(set, ref data, 0); // SPDRP_DEVICEDESC
                if (!string.IsNullOrWhiteSpace(name)) adapters.Add(name);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        finally
        {
            if (set != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(set);
            Marshal.FreeHGlobal(classGuid);
        }
        return adapters.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string GetPhysicalStorageType()
    {
        const string script = "$letter=$env:SystemDrive.TrimEnd(':'); $partition=Get-Partition -DriveLetter $letter -ErrorAction Stop | Select-Object -First 1; $disk=Get-Disk -Number $partition.DiskNumber -ErrorAction Stop; $physical=Get-PhysicalDisk | Where-Object { [string]$_.DeviceId -eq [string]$disk.Number } | Select-Object -First 1; [pscustomobject]@{BusType=[string]$disk.BusType;MediaType=[string]$physical.MediaType}|ConvertTo-Json -Compress";
        try
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
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
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(encoded);
            using Process? process = Process.Start(start);
            if (process is null) return "No se pudo identificar";
            // Leer ambas secuencias de forma asíncrona ANTES de esperar evita deadlocks por buffer lleno.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "No se pudo identificar";
            }
            string output = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return "No se pudo identificar";
            using JsonDocument document = JsonDocument.Parse(output);
            string bus = document.RootElement.TryGetProperty("BusType", out JsonElement busValue) ? busValue.GetString() ?? "" : "";
            string media = document.RootElement.TryGetProperty("MediaType", out JsonElement mediaValue) ? mediaValue.GetString() ?? "" : "";
            if (bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase)) return "SSD NVMe";
            if (media.Equals("HDD", StringComparison.OrdinalIgnoreCase)) return "HDD";
            if (media.Equals("SSD", StringComparison.OrdinalIgnoreCase)) return bus.Equals("SATA", StringComparison.OrdinalIgnoreCase) ? "SATA SSD" : "SSD";
            return "No se pudo identificar";
        }
        catch { return "No se pudo identificar"; }
    }

    private static bool HasBattery()
    {
        try { return GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255); }
        catch { return false; }
    }

    private static bool ServiceExists(string serviceName)
    {
        try { using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}"); return key is not null; }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    [DllImport("kernel32.dll", EntryPoint = "GetActiveProcessorCount", SetLastError = true)] private static extern uint GetActiveProcessorCount(ushort groupNumber);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
