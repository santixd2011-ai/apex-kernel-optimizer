using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ApexKernelOptimizer.Core.Engine;

public static class TimingDiagnostics
{
    public static string MeasureSleepOne()
    {
        var caps = new TimeCaps();
        int result = TimeGetDevCaps(ref caps, (uint)Marshal.SizeOf<TimeCaps>());
        var samples = new double[100];
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Thread.Sleep(1);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        double[] ordered = samples.Order().ToArray();
        double average = samples.Average();
        double p95 = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
        var output = new StringBuilder();
        output.AppendLine("Medición local de Sleep(1)");
        output.AppendLine("100 esperas consecutivas, sin cambiar la resolución global ni los ajustes de energía.");
        output.AppendLine();
        if (result == 0)
            output.AppendLine($"Rango de resolución multimedia admitido: {caps.PeriodMin}–{caps.PeriodMax} ms (timeGetDevCaps).");
        else
            output.AppendLine($"Windows no informó el rango multimedia (código {result}).");
        output.AppendLine("Duración observada por espera:");
        output.AppendLine($"  Mínima: {ordered[0]:0.000} ms");
        output.AppendLine($"  Media:  {average:0.000} ms");
        output.AppendLine($"  P95:    {p95:0.000} ms");
        output.AppendLine($"  Máxima: {ordered[^1]:0.000} ms");
        output.AppendLine();
        output.AppendLine("La espera real también depende de la planificación y carga del sistema; esta muestra no predice por sí sola la latencia de un juego.");
        return output.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeCaps { public uint PeriodMin, PeriodMax; }

    [DllImport("winmm.dll", EntryPoint = "timeGetDevCaps")]
    private static extern int TimeGetDevCaps(ref TimeCaps caps, uint size);
}
