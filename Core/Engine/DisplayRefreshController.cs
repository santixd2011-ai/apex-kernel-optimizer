using System.Runtime.InteropServices;

namespace ApexKernelOptimizer.Core.Engine;

public sealed record DisplayRefreshOption(string DeviceName, string DisplayName, uint Width, uint Height, uint BitsPerPixel, uint CurrentHz, uint MaximumHz, bool HasBackup);
public sealed record DisplayRefreshSnapshot(string DeviceName, uint Width, uint Height, uint BitsPerPixel, int Orientation, uint OriginalHz, uint TargetHz);
public sealed record DisplayRefreshResult(bool Success, string Message, DisplayRefreshSnapshot? Snapshot = null);

public static class DisplayRefreshController
{
    private const uint AttachedToDesktop = 0x1;
    private const uint DmDisplayOrientation = 0x00000080;
    private const uint DmBitsPerPel = 0x00040000;
    private const uint DmPelsWidth = 0x00080000;
    private const uint DmPelsHeight = 0x00100000;
    private const uint DmDisplayFrequency = 0x00400000;
    private const uint CdsUpdateRegistry = 0x00000001;
    private const uint CdsTest = 0x00000002;
    private const int EnumCurrentSettings = -1;
    private const int DispChangeSuccessful = 0;
    private const int DispChangeRestart = 1;

    public static IReadOnlyList<DisplayRefreshOption> Enumerate()
    {
        var results = new List<DisplayRefreshOption>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            var display = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref display, 0)) break;
            if ((display.StateFlags & AttachedToDesktop) == 0 || string.IsNullOrWhiteSpace(display.DeviceName) || !visited.Add(display.DeviceName)) continue;
            if (!TryGetCurrent(display.DeviceName, out DevMode current)) continue;
            uint maxHz = FindMaximumRefresh(display.DeviceName, current);
            results.Add(new(display.DeviceName, display.DeviceString ?? display.DeviceName, current.PelsWidth, current.PelsHeight,
                current.BitsPerPel, current.DisplayFrequency, maxHz, false));
        }
        return results;
    }

    public static DisplayRefreshResult PlanMaximumRefresh(string deviceName)
    {
        if (!TryGetCurrent(deviceName, out DevMode current)) return new(false, "No se pudo leer el modo actual del monitor.");
        uint maxHz = FindMaximumRefresh(deviceName, current);
        if (maxHz <= current.DisplayFrequency) return new(false, $"El monitor ya está a su frecuencia máxima detectada ({current.DisplayFrequency} Hz).");
        if (!TryFindMode(deviceName, current.PelsWidth, current.PelsHeight, current.BitsPerPel, GetOrientation(current), maxHz, out DevMode target))
            return new(false, "El controlador dejó de ofrecer el modo máximo detectado.");
        var snapshot = new DisplayRefreshSnapshot(deviceName, current.PelsWidth, current.PelsHeight, current.BitsPerPel,
            GetOrientation(current), current.DisplayFrequency, maxHz);
        int test = ChangeDisplaySettingsEx(deviceName, ref target, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (test != DispChangeSuccessful) return new(false, $"Windows rechazó la prueba del modo {maxHz} Hz (código {test}); no se aplicó.");
        return new(true, $"Modo {maxHz} Hz probado para {current.PelsWidth}×{current.PelsHeight}.", snapshot);
    }

    public static DisplayRefreshResult ApplyPreview(DisplayRefreshSnapshot snapshot)
    {
        if (!TryFindMode(snapshot.DeviceName, snapshot.Width, snapshot.Height, snapshot.BitsPerPixel, snapshot.Orientation, snapshot.TargetHz, out DevMode target))
            return new(false, "El modo máximo probado ya no está disponible.");
        int test = ChangeDisplaySettingsEx(snapshot.DeviceName, ref target, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (test != DispChangeSuccessful) return new(false, $"Windows rechazó la prueba final del modo (código {test}); no se aplicó.");
        int apply = ChangeDisplaySettingsEx(snapshot.DeviceName, ref target, IntPtr.Zero, 0, IntPtr.Zero);
        return apply == DispChangeSuccessful
            ? new(true, $"Vista previa: {snapshot.OriginalHz} → {snapshot.TargetHz} Hz a {snapshot.Width}×{snapshot.Height}. Confirma en 15 segundos para guardar; si no, se restaurará automáticamente.", snapshot)
            : new(false, $"No se pudo aplicar la vista previa del modo {snapshot.TargetHz} Hz (código {apply}).");
    }

    public static DisplayRefreshResult Confirm(DisplayRefreshSnapshot snapshot)
    {
        if (!TryGetCurrent(snapshot.DeviceName, out DevMode current) || !IsSnapshotMode(current, snapshot, snapshot.TargetHz))
            return new(false, "El modo del monitor cambió durante la vista previa; no se guardó el ajuste.");
        if (!TryFindMode(snapshot.DeviceName, snapshot.Width, snapshot.Height, snapshot.BitsPerPixel, snapshot.Orientation, snapshot.TargetHz, out DevMode target))
            return new(false, "El modo de vista previa ya no está disponible.");
        int test = ChangeDisplaySettingsEx(snapshot.DeviceName, ref target, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (test != DispChangeSuccessful) return new(false, $"Windows rechazó confirmar el modo (código {test}).");
        int result = ChangeDisplaySettingsEx(snapshot.DeviceName, ref target, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        return result is DispChangeSuccessful or DispChangeRestart
            ? new(true, result == DispChangeRestart ? "El modo se guardó; Windows solicita reiniciar para aplicarlo completamente." : $"Se guardó {snapshot.TargetHz} Hz para {snapshot.DeviceName}. El valor original se puede restaurar desde el gestor.")
            : new(false, $"No se pudo guardar el modo (código {result}); se mantiene el respaldo para revertir.");
    }

    public static DisplayRefreshResult Restore(DisplayRefreshSnapshot snapshot, bool onlyIfTargetStillCurrent)
    {
        if (!TryGetCurrent(snapshot.DeviceName, out DevMode current)) return new(false, "El monitor no está disponible. El respaldo se conserva para volver a intentarlo al conectarlo.");
        if (onlyIfTargetStillCurrent && !IsSnapshotMode(current, snapshot, snapshot.TargetHz))
            return new(false, "El modo cambió durante la vista previa; no se sobrescribió la elección externa. El respaldo sigue disponible.");
        if (!TryFindMode(snapshot.DeviceName, snapshot.Width, snapshot.Height, snapshot.BitsPerPixel, snapshot.Orientation, snapshot.OriginalHz, out DevMode original))
            return new(false, "La frecuencia original ya no aparece como modo disponible; el respaldo se conserva.");
        int test = ChangeDisplaySettingsEx(snapshot.DeviceName, ref original, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (test != DispChangeSuccessful) return new(false, $"Windows rechazó restaurar {snapshot.OriginalHz} Hz (código {test}); se conserva el respaldo.");
        int result = ChangeDisplaySettingsEx(snapshot.DeviceName, ref original, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        return result is DispChangeSuccessful or DispChangeRestart
            ? new(true, $"Se restauró {snapshot.OriginalHz} Hz para {snapshot.DeviceName}.")
            : new(false, $"No se pudo guardar la restauración (código {result}); se conserva el respaldo.");
    }

    private static bool TryGetCurrent(string deviceName, out DevMode mode)
    {
        mode = NewMode();
        return EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode);
    }

    private static uint FindMaximumRefresh(string deviceName, DevMode current)
    {
        uint max = current.DisplayFrequency;
        for (int index = 0; ; index++)
        {
            DevMode candidate = NewMode();
            if (!EnumDisplaySettings(deviceName, index, ref candidate)) break;
            if (candidate.PelsWidth == current.PelsWidth && candidate.PelsHeight == current.PelsHeight &&
                candidate.BitsPerPel == current.BitsPerPel && GetOrientation(candidate) == GetOrientation(current) &&
                candidate.DisplayFrequency > max && candidate.DisplayFrequency > 1)
                max = candidate.DisplayFrequency;
        }
        return max;
    }

    private static bool TryFindMode(string deviceName, uint width, uint height, uint bpp, int orientation, uint refresh, out DevMode mode)
    {
        for (int index = 0; ; index++)
        {
            DevMode candidate = NewMode();
            if (!EnumDisplaySettings(deviceName, index, ref candidate)) break;
            if (candidate.PelsWidth == width && candidate.PelsHeight == height && candidate.BitsPerPel == bpp &&
                GetOrientation(candidate) == orientation && candidate.DisplayFrequency == refresh)
            {
                mode = candidate;
                return true;
            }
        }
        mode = default;
        return false;
    }

    private static bool IsSnapshotMode(DevMode mode, DisplayRefreshSnapshot snapshot, uint refresh) =>
        mode.PelsWidth == snapshot.Width && mode.PelsHeight == snapshot.Height && mode.BitsPerPel == snapshot.BitsPerPixel &&
        GetOrientation(mode) == snapshot.Orientation && mode.DisplayFrequency == refresh;

    private static int GetOrientation(DevMode mode) => (mode.Fields & DmDisplayOrientation) != 0 ? mode.PositionX : 0;
    private static DevMode NewMode() => new() { Size = (ushort)Marshal.SizeOf<DevMode>() };

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
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplaySettings(string? deviceName, int modeNumber, ref DevMode mode);
    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DevMode mode, IntPtr hwnd, uint flags, IntPtr lParam);
}
