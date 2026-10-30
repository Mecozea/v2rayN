using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Manager;

public class CoreAdminManager
{
    private static readonly Lazy<CoreAdminManager> _instance = new(() => new());
    public static CoreAdminManager Instance => _instance.Value;
    private Config _config;
    private Func<bool, string, Task>? _updateFunc;
    private int _linuxSudoPid = -1;
    private const string _tag = "CoreAdminHandler";

    public async Task Init(Config config, Func<bool, string, Task> updateFunc)
    {
        if (_config != null)
        {
            return;
        }
        _config = config;
        _updateFunc = updateFunc;

        await Task.CompletedTask;
    }

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }

    // Double quotes alone still execute shell expansions in user-controlled paths.
    public static string QuoteShellArgument(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    public async Task<ProcessService?> RunProcessAsLinuxSudo(string fileName, CoreInfo coreInfo, string configPath)
    {
        StringBuilder sb = new();
        sb.AppendLine("#!/bin/bash");
        var cmdLine = $"{QuoteShellArgument(fileName)} {string.Format(coreInfo.Arguments, QuoteShellArgument(Utils.GetBinConfigPath(configPath)))}";

        // Passing environment variables to the sudo command, here it only xray or sing-box.
        if (coreInfo.Environment.Count > 0)
        {
            var envArgs = string.Join(" ", coreInfo.Environment.Where(kv => kv.Value.IsNotEmpty()).Select(kv => $"{kv.Key}={QuoteShellArgument(string.Format(kv.Value, Utils.GetBinConfigPath(configPath)))}"));
            sb.AppendLine($"exec sudo -S -p '' -- env {envArgs} {cmdLine}");
        }
        else
        {
            sb.AppendLine($"exec sudo -S -p '' -- {cmdLine}");
        }

        var shFilePath = await FileUtils.CreateLinuxShellFile("run_as_sudo.sh", sb.ToString(), true);

        var procService = new ProcessService(
            fileName: shFilePath,
            arguments: "",
            workingDirectory: Utils.GetBinConfigPath(),
            displayLog: true,
            redirectInput: true,
            environmentVars: null,
            updateFunc: _updateFunc
        );

        try
        {
            await procService.StartAsync(AppManager.Instance.LinuxSudoPwd);
            _linuxSudoPid = procService.Id;
            if (procService.HasExited) { throw new Exception(ResUI.FailedToRunCore); }
            return procService;
        }
        catch
        {
            try { await KillProcessAsLinuxSudo(); }
            finally
            {
                try { await procService.StopAsync(); }
                finally { procService.Dispose(); }
            }
            throw;
        }
    }

    public async Task KillProcessAsLinuxSudo()
    {
        if (_linuxSudoPid < 0)
        {
            return;
        }

        try
        {
            var shellFileName = Utils.IsMacOS() ? Global.KillAsSudoOSXShellFileName : Global.KillAsSudoLinuxShellFileName;
            var shFilePath = await FileUtils.CreateLinuxShellFile("kill_as_sudo.sh", EmbedUtils.GetEmbedText(shellFileName), true);
            var arg = new List<string>() { "-S", "-p", "", "--", "/bin/bash", shFilePath, _linuxSudoPid.ToString() };
            var result = await Cli.Wrap("/usr/bin/sudo")
                .WithArguments(arg)
                .WithStandardInputPipe(PipeSource.FromString(AppManager.Instance.LinuxSudoPwd + "\n"))
                .ExecuteBufferedAsync();

            await UpdateFunc(false, result.StandardOutput.ToString());

            await Task.Delay(1000); // Wait for a second to ensure the process is killed
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        _linuxSudoPid = -1;
    }
}
