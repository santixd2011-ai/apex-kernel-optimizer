using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Text;

namespace ApexKernelOptimizer.Core.Engine;

public static class BypassIoDiagnostics
{
    public static async Task<string> QueryAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return "BypassIO solo está disponible en Windows.";
        string? productType = null;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            productType = key?.GetValue("ProductType") as string;
        }
        catch { }
        if (productType is not null && !productType.Equals("WinNT", StringComparison.OrdinalIgnoreCase))
            return "Microsoft documenta BypassIO para Windows cliente; esta edición de servidor no está en el ámbito soportado.";
        if (Environment.OSVersion.Version.Build < 22000) return "BypassIO requiere Windows 11 o posterior. Este sistema no cumple ese requisito.";

        string volume = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        string fileSystem;
        try { fileSystem = new DriveInfo(volume).DriveFormat; }
        catch (Exception ex) { return $"No se pudo identificar el sistema de archivos de {volume}: {ex.Message}"; }
        if (!fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            return $"El volumen del sistema usa {fileSystem}; BypassIO solo se admite en NTFS. Volumen consultado: {volume}";

        string executable = Path.Combine(Environment.SystemDirectory, "fsutil.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("bypassIo");
        start.ArgumentList.Add("state");
        start.ArgumentList.Add("/v");
        start.ArgumentList.Add(volume);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("No se pudo iniciar fsutil.exe.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(12), cancellationToken); }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return "La consulta de BypassIO tardó más de 12 segundos y se canceló.";
        }

        string output = (await stdout + Environment.NewLine + await stderr).Trim();
        if (process.ExitCode == 0)
            return $"Volumen consultado: {volume} ({fileSystem})\n\n{output}\n\nLa consulta es de solo lectura; no cambia los controladores ni habilita BypassIO.";

        var message = new StringBuilder();
        message.AppendLine($"Volumen consultado: {volume} ({fileSystem})");
        message.AppendLine();
        message.AppendLine(string.IsNullOrWhiteSpace(output) ? $"fsutil terminó con código {process.ExitCode}." : output);
        if (output.Contains("access is denied", StringComparison.OrdinalIgnoreCase) || output.Contains("acceso denegado", StringComparison.OrdinalIgnoreCase))
            message.AppendLine("Vuelve a abrir ApexKernel como administrador y repite la consulta; este resultado no indica que el controlador sea incompatible.");
        else
            message.AppendLine("El resultado identifica una limitación del sistema, la unidad o un controlador/filtro; no se modificó nada.");
        return message.ToString();
    }
}
