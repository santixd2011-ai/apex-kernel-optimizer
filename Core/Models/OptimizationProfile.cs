namespace ApexKernelOptimizer.Core.Models;

public sealed class OptimizationProfile
{
    public int Version { get; init; } = 1;
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<int> EnabledIds { get; init; } = [];
}
