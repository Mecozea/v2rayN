using System.Diagnostics;

namespace AmazTool;

internal class UpgradeApp
{
    public static void Upgrade(string fileName)
    {
        Console.WriteLine($"{Resx.Resource.StartUnzipping}\n{fileName}");
        // Fully parse and decompress the archive before stopping the currently running application.
        using var transaction = AppUpdateTransaction.Prepare(fileName, Utils.StartupPath());
        var stopped = false;
        try
        {
            transaction.Apply(() => { StopOwnedApplication(); stopped = true; });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Resx.Resource.FailedUpgrade}: {ex}");
            Environment.ExitCode = 1;
            if (stopped && !transaction.RecoveryRequired)
            {
                transaction.Dispose();
                Utils.StartV2RayN();
            }
            return;
        }
        transaction.Dispose(); // Remove staging/backups before launching a sealed app bundle.
        Console.WriteLine(Resx.Resource.Restartv2rayN);
        Utils.StartV2RayN();
    }

    private static void StopOwnedApplication()
    {
        var target = Path.GetFullPath(Utils.GetPath(OperatingSystem.IsWindows() ? "v2rayN.exe" : "v2rayN"));
        foreach (var process in Process.GetProcessesByName(Utils.V2rayN))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) { continue; }
                string? path;
                try { path = process.MainModule?.FileName; }
                catch (System.ComponentModel.Win32Exception) { continue; }
                catch (InvalidOperationException) { continue; }
                if (path == null || !string.Equals(Path.GetFullPath(path), target, Utils.GetPathComparison())) { continue; }
                // Only this installation's exact executable is targeted; no name-wide process kill.
                if (process.WaitForExit(5000)) { continue; }
                process.Kill();
                if (!process.WaitForExit(5000)) { throw new IOException("Application did not stop before update."); }
            }
        }
    }
}
