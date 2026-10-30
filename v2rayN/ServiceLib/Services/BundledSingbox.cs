namespace ServiceLib.Services;

/// <summary>Keep old user cores intact while using a frontend that supports generated DNS rules.</summary>
public static class BundledSingbox
{
    private static readonly Version MinimumVersion = new(1, 14, 0);

    public static string Resolve(string installed, string bundled)
    {
        if (ReadVersion(installed) is { } installedVersion && installedVersion >= MinimumVersion)
        {
            return installed;
        }
        if (ReadVersion(bundled) is { } bundledVersion && bundledVersion >= MinimumVersion)
        {
            return bundled;
        }
        throw new InvalidOperationException("sing-box 1.14.0 or newer is required by the generated DNS configuration. Update the sing-box core or reinstall the complete application bundle.");
    }

    private static Version? ReadVersion(string executable)
    {
        if (!File.Exists(executable)) { return null; }
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    ArgumentList = { "version" },
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return null;
            }
            if (process.ExitCode != 0) { return null; }
            var match = Regex.Match(output.GetAwaiter().GetResult(), @"(?m)^sing-box version (\d+\.\d+\.\d+)(?:\s|$)");
            _ = error.GetAwaiter().GetResult();
            return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
