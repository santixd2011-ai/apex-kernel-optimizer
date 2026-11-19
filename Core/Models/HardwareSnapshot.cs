namespace ApexKernelOptimizer.Core.Models;

public sealed record HardwareSnapshot(
    string CpuName,
    int LogicalProcessors,
    int PhysicalCores,
    bool IsDualCcdX3d,
    double MemoryGb,
    IReadOnlyList<string> GraphicsAdapters,
    string SystemDriveType,
    bool HasBattery,
    bool HasVanguard,
    bool HasFaceit,
    bool HasEasyAntiCheat,
    bool HasBattlEye,
    string WindowsVersion,
    string WindowsEdition,
    int WindowsBuild,
    int WindowsUbr,
    IReadOnlyList<string> UsbTopology,
    bool? SupportsHardwareGpuScheduling,
    bool? IsHardwareGpuSchedulingEnabled);
