namespace ServiceLib.Services;

public class ProcessService : IDisposable
{
    private readonly Process _process;
    private readonly Func<bool, string, Task>? _updateFunc;
    private bool _isDisposed;
    private bool _started;
    private ProcessService? _ownedProcess;

    public void AttachOwnedProcess(ProcessService process) => _ownedProcess = process;

    public event Action? Exited;

    public int Id => _process.Id;
    public IntPtr Handle => _process.Handle;
    public bool HasExited => !_started || _isDisposed || _process.HasExited;

    public ProcessService(
        string fileName,
        string arguments,
        string workingDirectory,
        bool displayLog,
        bool redirectInput,
        Dictionary<string, string>? environmentVars,
        Func<bool, string, Task>? updateFunc,
        IReadOnlyList<string>? argumentList = null)
    {
        _updateFunc = updateFunc;

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = displayLog,
                RedirectStandardError = displayLog,
                CreateNoWindow = true,
                StandardOutputEncoding = displayLog ? Encoding.UTF8 : null,
                StandardErrorEncoding = displayLog ? Encoding.UTF8 : null,
            },
            EnableRaisingEvents = true
        };

        _process.Exited += (_, _) => Exited?.Invoke();

        if (argumentList != null)
        {
            _process.StartInfo.Arguments = string.Empty;
            foreach (var argument in argumentList)
            {
                _process.StartInfo.ArgumentList.Add(argument);
            }
        }

        if (environmentVars != null)
        {
            foreach (var kv in environmentVars)
            {
                _process.StartInfo.Environment[kv.Key] = kv.Value;
            }
        }

        if (displayLog)
        {
            RegisterEventHandlers();
        }
    }

    public async Task StartAsync(string pwd = null)
    {
        _process.Start();
        _started = true;

        if (_process.StartInfo.RedirectStandardOutput)
        {
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        if (_process.StartInfo.RedirectStandardInput)
        {
            await Task.Delay(10);
            await _process.StandardInput.WriteLineAsync(pwd);
            _process.StandardInput.Close(); // sudo must fail on EOF rather than wait forever for another password.
        }
    }

    public async Task StopAsync()
    {
        try
        {
            if (!HasExited)
            {
                // Only the process handle owned by this session is targeted, including children.
                _process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await _process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { }
            }
        }
        catch (InvalidOperationException) { } // Process exited concurrently or never started.
        catch (System.ComponentModel.Win32Exception)
        {
            // A process can disappear between HasExited and Kill.
            if (!HasExited) { throw; }
        }
        finally
        {
            // A frontend stops before the Vector on which it depends.
            var owned = Interlocked.Exchange(ref _ownedProcess, null);
            if (owned != null)
            {
                try { await owned.StopAsync(); }
                finally { owned.Dispose(); }
            }
        }
    }

    private void RegisterEventHandlers()
    {
        void dataHandler(object sender, DataReceivedEventArgs e)
        {
            if (e.Data.IsNotEmpty())
            {
                _ = _updateFunc?.Invoke(false, e.Data + Environment.NewLine);
            }
        }

        _process.OutputDataReceived += dataHandler;
        _process.ErrorDataReceived += dataHandler;

        _process.Exited += (s, e) =>
        {
            try
            {
                _process.OutputDataReceived -= dataHandler;
                _process.ErrorDataReceived -= dataHandler;
            }
            catch
            {
            }
        };
    }

    public void Dispose()
    {
        if (_isDisposed) { return; }
        try
        {
            if (_started && !_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
                catch (InvalidOperationException) { }
            }
        }
        finally
        {
            _isDisposed = true;
            _process.Dispose();
            Interlocked.Exchange(ref _ownedProcess, null)?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
