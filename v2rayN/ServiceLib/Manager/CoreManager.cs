namespace ServiceLib.Manager;

/// <summary>
/// Core process processing class
/// </summary>
public class CoreManager
{
    private static readonly Lazy<CoreManager> _instance = new(() => new());
    public static CoreManager Instance => _instance.Value;
    private Config _config;

    [SupportedOSPlatform("windows")]
    private WindowsJobService? _processJob;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<ProcessService, byte> _nowhereProbes = new();
    private CancellationTokenSource? _launchCts;
    private CancellationTokenSource? _updateCts;
    private long _stopGeneration;
    public long StopGeneration => Volatile.Read(ref _stopGeneration);
    private ProcessService? _processService;
    private ProcessService? _processPreService;
    private bool _linuxSudo = false;
    private bool _requiresPreProcess;
    private Config? _systemProxyOwner;
    private Config? _runningConfig;

    public bool IsRunning => _processService is { HasExited: false }
        && (!_requiresPreProcess || _processPreService is { HasExited: false });
    private Func<bool, string, Task>? _updateFunc;
    private const string _tag = "CoreHandler";

    public async Task Init(Config config, Func<bool, string, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;

        //Copy the bin folder to the storage location (for init)
        if (Environment.GetEnvironmentVariable(Global.LocalAppData) == "1")
        {
            var fromPath = Utils.GetBundledResourcePath("bin");
            var toPath = Utils.GetBinPath("");
            if (fromPath != toPath)
            {
                FileUtils.CopyDirectory(fromPath, toPath, true, false);
            }
        }

        if (Utils.IsNonWindows())
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo();
            foreach (var it in coreInfo)
            {
                if (it.CoreType == ECoreType.v2rayN)
                {
                    if (Utils.UpgradeAppExists(out var upgradeFileName))
                    {
                        await Utils.SetLinuxChmod(upgradeFileName);
                    }
                    continue;
                }

                foreach (var name in it.CoreExes)
                {
                    var exe = Utils.GetBinPath(Utils.GetExeName(name), it.CoreType.ToString());
                    if (File.Exists(exe))
                    {
                        await Utils.SetLinuxChmod(exe);
                    }
                }
            }
        }
    }

    /// <param name="mainContext">Resolved main context (with pre-socks ports already merged if applicable).</param>
    /// <param name="preContext">Optional pre-socks context passed to <see cref="CoreStartPreService"/>.</param>
    public async Task<bool> LoadCore(CoreConfigContext? mainContext, CoreConfigContext? preContext, long? expectedStopGeneration = null)
    {
        mainContext = Snapshot(mainContext);
        preContext = Snapshot(preContext);
        var cts = new CancellationTokenSource();
        CancelLaunch(Interlocked.Exchange(ref _launchCts, cts));
        await _lifecycle.WaitAsync();
        var previousMain = _processService;
        var previousFrontend = _processPreService;
        try
        {
            cts.Token.ThrowIfCancellationRequested();
            if (expectedStopGeneration.HasValue && expectedStopGeneration.Value != StopGeneration) { return false; }
            return await LoadCoreInternal(mainContext, preContext, cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(previousMain, _processService) || !ReferenceEquals(previousFrontend, _processPreService))
            {
                await CoreStopInternal();
            }
            return false;
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(previousMain, _processService) || !ReferenceEquals(previousFrontend, _processPreService))
            {
                await CoreStopInternal();
            }
            if (mainContext?.Node.ConfigType != EConfigType.Nowhere)
            {
                Logging.SaveLog(_tag, ex);
            }
            await UpdateFunc(true, mainContext?.Node.ConfigType == EConfigType.Nowhere
                ? $"{ResUI.FailedToRunCore}: {NowhereRuntime.Redact(ex.Message, mainContext.Node)}"
                : ResUI.FailedToRunCore);
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _launchCts, null, cts);
            cts.Dispose();
            _lifecycle.Release();
        }
    }

    private async Task<bool> LoadCoreInternal(CoreConfigContext? mainContext, CoreConfigContext? preContext, CancellationToken token)
    {
        if (mainContext == null)
        {
            await UpdateFunc(false, ResUI.CheckServerSettings);
            return false;
        }

        var node = mainContext.Node;
        var fileName = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        var result = await CoreConfigHandler.GenerateClientConfig(mainContext, fileName);
        if (result.Success != true)
        {
            await UpdateFunc(true, result.Msg);
            return false;
        }

        if (node.ConfigType == EConfigType.Nowhere)
        {
            await EnsureNowhereCore(token);
            EnsureFrontendAvailable(preContext);
        }
        await UpdateFunc(false, $"{node.GetSummary()}");
        await UpdateFunc(false, $"{Utils.GetRuntimeInfo()}");
        await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
        await CoreStopInternal();
        _requiresPreProcess = preContext != null;
        await Task.Delay(100, token);

        if (Utils.IsWindows() && (mainContext?.IsTunEnabled == true || preContext?.IsTunEnabled == true))
        {
            await Task.Delay(100);
            await WindowsUtils.RemoveTunDevice();
        }

        token.ThrowIfCancellationRequested();
        if (node.ConfigType == EConfigType.Nowhere)
        {
            var started = await StartNowhereWithRetry(node, mainContext.NowherePort, token);
            _processService = started.Process;
            mainContext = mainContext with { NowherePort = started.Port };
            if (preContext == null) { throw new InvalidOperationException("Nowhere requires a local frontend."); }
            preContext.Node.Port = started.Port;
            preContext.AllProxiesMap[preContext.Node.IndexId] = preContext.Node;
            var vector = _processService;
            vector.Exited += () => _ = HandleNowhereExit(vector);
        }
        else
        {
            await CoreStart(mainContext);
        }
        if (node.ConfigType != EConfigType.Nowhere) { await WaitForProxyPort(preContext); }
        token.ThrowIfCancellationRequested();
        await CoreStartPreService(preContext);
        if (node.ConfigType == EConfigType.Nowhere)
        {
            if (_processPreService == null || _processPreService.HasExited)
            {
                throw new InvalidOperationException("Nowhere frontend failed to start.");
            }
            await NowhereRuntime.WaitForSocksAsync(_processPreService,
                preContext!.AppConfig.Inbound.FirstOrDefault(i => i.Protocol == nameof(EInboundProtocol.socks))?.LocalPort ?? 10808, token, $"Nowhere frontend ({preContext.RunCoreType})");
            var frontend = _processPreService;
            frontend.Exited += () => _ = HandleNowhereFrontendExit(frontend);
            if (frontend.HasExited) { throw new InvalidOperationException("Nowhere frontend exited during startup."); }
        }
        token.ThrowIfCancellationRequested();

        if (!IsRunning) { throw new InvalidOperationException(ResUI.FailedToRunCore); }
        _runningConfig = JsonUtils.DeepCopy(preContext?.AppConfig ?? mainContext.AppConfig);
        AppManager.Instance.RunningCoreType = preContext?.RunCoreType ?? mainContext.RunCoreType;

        if (_processService != null)
        {
            await UpdateFunc(true, $"{node.GetSummary()}");
        }
        return true;
    }

    public async Task<ProcessService?> LoadCoreConfigSpeedtest(List<ServerTestItem> selecteds, CancellationToken cancellationToken = default)
    {
        var coreType = selecteds.FirstOrDefault()?.CoreType == ECoreType.sing_box ? ECoreType.sing_box : ECoreType.Xray;
        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        var configPath = Utils.GetBinConfigPath(fileName);
        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, configPath, selecteds, coreType);
        await UpdateFunc(false, result.Msg);
        if (result.Success != true)
        {
            return null;
        }

        await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
        await UpdateFunc(false, configPath);

        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        return await RunProcess(coreInfo, fileName, true, false);
    }

    public async Task<ProcessService?> LoadCoreConfigSpeedtest(ServerTestItem testItem, CancellationToken cancellationToken = default)
    {
        var node = await AppManager.Instance.GetProfileItem(testItem.IndexId);
        if (node is null)
        {
            return null;
        }

        if (node.ConfigType == EConfigType.Nowhere)
        {
            return await LoadNowhereSpeedtest(node, testItem, cancellationToken);
        }
        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        var configPath = Utils.GetBinConfigPath(fileName);
        var (context, _) = await CoreConfigContextBuilder.Build(_config, node);
        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, context, testItem, configPath);
        if (result.Success != true)
        {
            return null;
        }

        var coreType = context.RunCoreType;
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        return await RunProcess(coreInfo, fileName, true, false);
    }

    public async Task CoreStop()
    {
        Interlocked.Increment(ref _stopGeneration);
        CancelLaunch(_updateCts);
        CancelLaunch(_launchCts);
        await _lifecycle.WaitAsync();
        try { await CoreStopInternal(); }
        finally { _lifecycle.Release(); }
    }

    public async Task<bool> ApplySystemProxyAsync(long expectedStopGeneration)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (expectedStopGeneration != StopGeneration || !IsRunning) { return false; }
            var snapshot = JsonUtils.DeepCopy(_runningConfig ?? _config);
            snapshot.SystemProxyItem = JsonUtils.DeepCopy(_config.SystemProxyItem);
            var managesProxy = snapshot.SystemProxyItem.SysProxyType is ESysProxyType.ForcedChange or ESysProxyType.Pac;
            // Setting multiple macOS services can fail after an earlier service succeeded.
            // Record ownership before the write so even a failed cleanup can be retried on stop.
            if (managesProxy) { _systemProxyOwner = snapshot; }
            if (!await SysProxyHandler.UpdateSysProxy(snapshot, false))
            {
                if (managesProxy) { await ClearManagedSystemProxy(); }
                return false;
            }
            _systemProxyOwner = managesProxy ? snapshot : null;
            return true;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ClearManagedSystemProxy()
    {
        var owner = _systemProxyOwner;
        if (owner != null && await SysProxyHandler.ClearOwnedSysProxy(owner))
        {
            _systemProxyOwner = null;
        }
    }

    private async Task CoreStopInternal()
    {
        AppManager.Instance.RunningCoreType = default;
        try { await ClearManagedSystemProxy(); }
        catch (Exception ex) { Logging.SaveLog(_tag, ex); }
        var frontend = _processPreService;
        var main = _processService;
        _processPreService = null;
        _processService = null;
        _requiresPreProcess = false;
        _runningConfig = null;
        if (_linuxSudo)
        {
            try { await CoreAdminManager.Instance.KillProcessAsLinuxSudo(); }
            catch (Exception ex) { Logging.SaveLog(_tag, ex); }
            _linuxSudo = false;
        }
        // Each owned resource is cleaned independently: one failure cannot strand the other core.
        foreach (var process in new[] { frontend, main })
        {
            if (process == null) { continue; }
            try { await process.StopAsync(); }
            catch (Exception ex) { Logging.SaveLog(_tag, ex); }
            finally
            {
                try { process.Dispose(); }
                catch (Exception ex) { Logging.SaveLog(_tag, ex); }
            }
        }
    }

    private static void EnsureFrontendAvailable(CoreConfigContext? context)
    {
        if (context == null) { throw new InvalidOperationException("Nowhere requires its managed SOCKS frontend."); }
        var info = CoreInfoManager.Instance.GetCoreInfo(context.RunCoreType);
        var executable = CoreInfoManager.Instance.GetCoreExecFile(info, out var error);
        if (string.IsNullOrEmpty(executable)) { throw new FileNotFoundException(error); }
    }

    private static CoreConfigContext? Snapshot(CoreConfigContext? context) => context == null ? null : context with
    {
        Node = JsonUtils.DeepCopy(context.Node),
        AppConfig = JsonUtils.DeepCopy(context.AppConfig),
        AllProxiesMap = context.AllProxiesMap.ToDictionary(p => p.Key, p => JsonUtils.DeepCopy(p.Value)),
        ProtectDomainList = new HashSet<string>(context.ProtectDomainList),
        ProtectCoreTypeList = new HashSet<ECoreType>(context.ProtectCoreTypeList),
        RoutingItem = context.RoutingItem == null ? null : JsonUtils.DeepCopy(context.RoutingItem),
        RawDnsItem = context.RawDnsItem == null ? null : JsonUtils.DeepCopy(context.RawDnsItem),
        SimpleDnsItem = JsonUtils.DeepCopy(context.SimpleDnsItem),
        FullConfigTemplate = context.FullConfigTemplate == null ? null : JsonUtils.DeepCopy(context.FullConfigTemplate),
        CustomOutboundContent = new Dictionary<string, string>(context.CustomOutboundContent),
        ServerTestItemMap = new Dictionary<string, string>(context.ServerTestItemMap),
        CustomOutboundMap = new Dictionary<object, string>(context.CustomOutboundMap),
    };

    private async Task<(ProcessService Process, int Port)> StartNowhereWithRetry(ProfileItem node, int port, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return (await StartNowhere(node, port, token), port); }
            catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested
                && ex is InvalidOperationException or OperationCanceledException)
            {
                // Listener allocation and child bind cannot be atomic. Retry on a fresh port.
                port = NowhereRuntime.AllocatePort();
            }
        }
    }

    private static void CancelLaunch(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task HandleNowhereExit(ProcessService vector)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!ReferenceEquals(_processService, vector)) { return; }
            await CoreStopInternal();
            await UpdateFunc(true, "Nowhere exited; its local frontend has been stopped. Reconnect the node to retry.");
        }
        finally { _lifecycle.Release(); }
    }

    private async Task HandleNowhereFrontendExit(ProcessService frontend)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!ReferenceEquals(_processPreService, frontend)) { return; }
            await CoreStopInternal();
            await UpdateFunc(true, ResUI.FailedToRunCore);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task UpdateNowhereAsync(bool rollback, CancellationToken cancellationToken = default, bool updateViaProxy = true)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancelLaunch(Interlocked.Exchange(ref _updateCts, cts));
        var token = cts.Token;
        var lockHeld = false;
        NowhereInstaller.VerifiedPackage? package = null;
        try
        {
            await _lifecycle.WaitAsync(token);
            lockHeld = true;
            if (_nowhereProbes.Keys.Any(process => !process.HasExited))
            {
                throw new InvalidOperationException("Finish Nowhere speed tests before changing the core.");
            }
            var directory = Utils.GetBinPath("", ECoreType.Nowhere.ToString());
            if (!rollback)
            {
                // Download and verify while the previous session can still provide its proxy.
                await UpdateFunc(false, $"{ResUI.Downloading} Nowhere {NowhereInstaller.Version} (official pinned release)");
                using var client = await CreateNowhereDownloadClient(token, updateViaProxy);
                package = await NowhereInstaller.DownloadVerifiedPackageAsync(client, token);
            }
            else
            {
                // A missing or untrusted rollback must not interrupt the current connection.
                await NowhereInstaller.ValidateExecutableAsync(
                    Path.Combine(directory, NowhereInstaller.ExecutableName + ".previous"), token);
            }
            token.ThrowIfCancellationRequested();
            if (_processService != null || _processPreService != null)
            {
                await CoreStopInternal();
            }
            token.ThrowIfCancellationRequested();
            if (rollback)
            {
                await NowhereInstaller.RollbackAsync(directory, token);
            }
            else
            {
                await NowhereInstaller.InstallAsync(package!.Path, directory, token);
            }
        }
        finally
        {
            package?.Dispose();
            Interlocked.CompareExchange(ref _updateCts, null, cts);
            if (lockHeld) { _lifecycle.Release(); }
        }
    }

    private async Task<HttpClient> CreateNowhereDownloadClient(CancellationToken token, bool updateViaProxy = true)
    {
        var proxy = await new DownloadService().GetWebProxy(updateViaProxy, token);
        var handler = new SocketsHttpHandler { Proxy = proxy, UseProxy = proxy != null };
        var certificatePolicy = CertPemManager.Instance.BuildCertificateChainPolicy();
        if (certificatePolicy != null) { handler.SslOptions.CertificateChainPolicy = certificatePolicy; }
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
    }

    private async Task InstallNowhereCore(string directory, CancellationToken token)
    {
        await UpdateFunc(false, $"{ResUI.Downloading} Nowhere {NowhereInstaller.Version} (official pinned release)");
        using var client = await CreateNowhereDownloadClient(token);
        await NowhereInstaller.DownloadAndInstallAsync(client, directory, token);
    }

    private async Task<string> EnsureNowhereCore(CancellationToken token)
    {
        if (!NowhereInstaller.IsSupported)
        {
            throw new PlatformNotSupportedException("Nowhere is not available for this operating system and architecture.");
        }
        var directory = Utils.GetBinPath("", ECoreType.Nowhere.ToString());
        var executable = Path.Combine(directory, NowhereInstaller.ExecutableName);
        if (!File.Exists(executable))
        {
            await InstallNowhereCore(directory, token);
        }
        await NowhereInstaller.ValidateExecutableAsync(executable, token);
        return executable;
    }

    private async Task<ProcessService> StartNowhere(ProfileItem node, int port, CancellationToken token)
    {
        var executable = await EnsureNowhereCore(token);
        var url = NowhereFmt.BuildServiceUrl(node, port);
        var snapshot = JsonUtils.DeepCopy(node);
        var process = new ProcessService(executable, string.Empty, Utils.GetBinConfigPath(), true, false, null,
            (notify, line) => UpdateFunc(notify, NowhereRuntime.Redact(line, snapshot)), [url]);
        try
        {
            await process.StartAsync();
            AddProcessJob(process.Handle);
            await NowhereRuntime.WaitForSocksAsync(process, port, token);
            return process;
        }
        catch
        {
            await process.StopAsync();
            process.Dispose();
            throw;
        }
    }

    private async Task<ProcessService?> LoadNowhereSpeedtest(ProfileItem node, ServerTestItem item, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token);
        try { return await LoadNowhereSpeedtestInternal(node, item, token); }
        finally { _lifecycle.Release(); }
    }

    private async Task<ProcessService?> LoadNowhereSpeedtestInternal(ProfileItem node, ServerTestItem item, CancellationToken token)
    {
        node = JsonUtils.DeepCopy(node);
        EnsureFrontendAvailable(new CoreConfigContext { Node = node, RunCoreType = ECoreType.Xray });
        var started = await StartNowhereWithRetry(node, NowhereRuntime.AllocatePort(), token);
        var port = started.Port;
        var vector = started.Process;
        ProcessService? frontend = null;
        var attached = false;
        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        try
        {
            var config = JsonUtils.DeepCopy(_config);
            config.TunModeItem.EnableTun = false;
            var socks = new ProfileItem { IndexId = node.IndexId, ConfigType = EConfigType.SOCKS,
                CoreType = ECoreType.Xray, Address = Global.Loopback, Port = port };
            var built = await CoreConfigContextBuilder.Build(config, socks);
            if (!built.Success) { return null; }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(config, built.Context, item, Utils.GetBinConfigPath(fileName));
                if (!result.Success) { return null; }
                token.ThrowIfCancellationRequested();
                frontend = await RunProcess(CoreInfoManager.Instance.GetCoreInfo(ECoreType.Xray), fileName, true, false);
                if (frontend == null) { continue; }
                try
                {
                    await NowhereRuntime.WaitForSocksAsync(frontend, item.Port, token);
                    break;
                }
                catch (Exception ex) when (!token.IsCancellationRequested
                    && ex is InvalidOperationException or OperationCanceledException)
                {
                    await frontend.StopAsync();
                    frontend.Dispose();
                    frontend = null;
                }
            }
            if (frontend == null) { return null; }
            frontend.AttachOwnedProcess(vector);
            _nowhereProbes.TryAdd(frontend, 0);
            var probe = frontend;
            frontend.Exited += () => _nowhereProbes.TryRemove(probe, out _);
            attached = true;
            return frontend;
        }
        catch
        {
            if (frontend != null) { await frontend.StopAsync(); frontend.Dispose(); }
            throw;
        }
        finally
        {
            if (!attached)
            {
                await vector.StopAsync();
                vector.Dispose();
            }
            try { File.Delete(Utils.GetBinConfigPath(fileName)); } catch { }
        }
    }

    #region Private

    private async Task CoreStart(CoreConfigContext context)
    {
        var node = context.Node;
        var coreType = AppManager.Instance.GetCoreType(node, node.ConfigType);
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);

        var displayLog = node.ConfigType != EConfigType.Custom || node.DisplayLog;
        var proc = await RunProcess(coreInfo, Global.CoreConfigFileName, displayLog, true, context.IsTunEnabled);
        if (proc is null)
        {
            return;
        }
        _processService = proc;
    }

    private async Task CoreStartPreService(CoreConfigContext? preContext)
    {
        if (_processService is { HasExited: false } && preContext != null)
        {
            var preCoreType = preContext?.Node?.CoreType ?? ECoreType.sing_box;
            var fileName = Utils.GetBinConfigPath(Global.CorePreConfigFileName);
            var result = await CoreConfigHandler.GenerateClientConfig(preContext, fileName);
            if (result.Success)
            {
                var coreInfo = CoreInfoManager.Instance.GetCoreInfo(preCoreType);
                var proc = await RunProcess(coreInfo, Global.CorePreConfigFileName, true, true, preContext.IsTunEnabled);
                if (proc is null)
                {
                    return;
                }
                _processPreService = proc;
            }
        }
    }

    private async Task UpdateFunc(bool notify, string msg)
    {
        if (_updateFunc != null) { await _updateFunc.Invoke(notify, msg); }
    }

    private static async Task WaitForProxyPort(CoreConfigContext? preContext)
    {
        if (preContext is null)
        {
            return;
        }
        if (!preContext.IsTunEnabled)
        {
            return;
        }

        using var rootCts = new CancellationTokenSource(Global.LocalFetch);
        var rootToken = rootCts.Token;

        var port = preContext.Node.Port;
        // SOCKS5 client greeting: VER=5, NMETHODS=1, METHOD=0x00 (no auth)
        ReadOnlyMemory<byte> greeting = new byte[] { 0x05, 0x01, 0x00 };
        var buf = new byte[2];

        while (!rootToken.IsCancellationRequested)
        {
            using var tcp = new TcpClient();
            using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(rootToken, attemptCts.Token);
            var linkedToken = linkedCts.Token;
            try
            {
                await tcp.ConnectAsync(Global.Loopback, port, linkedToken);
                var stream = tcp.GetStream();

                await stream.WriteAsync(greeting, linkedToken);

                var read = await stream.ReadAsync(buf.AsMemory(0, 2), linkedToken);

                // Server selection: VER=5, METHOD=0x00 — proxy is fully ready
                if (read == 2 && buf[0] == 0x05)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                if (!rootToken.IsCancellationRequested)
                {
                    continue;
                }
                Logging.SaveLog($"WaitForProxyPort Timeout waiting for proxy port {port} to be ready.");
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // Connection refused, proxy not ready yet, wait 50ms before retrying
                try
                {
                    await Task.Delay(50, rootToken);
                }
                catch (OperationCanceledException)
                {
                    Logging.SaveLog($"WaitForProxyPort Timeout waiting for proxy port {port} to be ready.");
                    return;
                }
            }
            catch
            {
                // Ignore other exceptions and continue
            }
        }
    }

    #endregion Private

    #region Process

    /// <summary>
    ///     Decides whether a core launch must be elevated on non-Windows platforms.
    ///     The TUN state comes from the immutable <see cref="CoreConfigContext" /> snapshot that
    ///     generated the config, never from the live mutable config: the generated config and the
    ///     launch mode must always agree, even if TUN is toggled while a reload is in flight.
    /// </summary>
    public static bool ShouldRunAsSudo(bool isTunLaunch, ECoreType? coreType, bool isNonWindows)
    {
        return isTunLaunch
            && coreType is ECoreType.sing_box or ECoreType.mihomo or ECoreType.Xray
            && isNonWindows;
    }

    private async Task<ProcessService?> RunProcess(CoreInfo? coreInfo, string configPath, bool displayLog, bool mayNeedSudo, bool isTunLaunch = false)
    {
        var fileName = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var msg);
        if (fileName.IsNullOrEmpty())
        {
            await UpdateFunc(false, msg);
            return null;
        }

        try
        {
            if (mayNeedSudo
                && ShouldRunAsSudo(isTunLaunch, coreInfo.CoreType, Utils.IsNonWindows()))
            {
                _linuxSudo = true;
                await CoreAdminManager.Instance.Init(_config, _updateFunc);
                return await CoreAdminManager.Instance.RunProcessAsLinuxSudo(fileName, coreInfo, configPath);
            }

            return await RunProcessNormal(fileName, coreInfo, configPath, displayLog);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(mayNeedSudo, ex.Message);
            return null;
        }
    }

    private async Task<ProcessService?> RunProcessNormal(string fileName, CoreInfo? coreInfo, string configPath, bool displayLog)
    {
        var environmentVars = new Dictionary<string, string>();
        foreach (var kv in coreInfo.Environment)
        {
            environmentVars[kv.Key] = string.Format(kv.Value, coreInfo.AbsolutePath ? Utils.GetBinConfigPath(configPath).AppendQuotes() : configPath);
        }

        var procService = new ProcessService(
            fileName: fileName,
            arguments: string.Format(coreInfo.Arguments, coreInfo.AbsolutePath ? Utils.GetBinConfigPath(configPath).AppendQuotes() : configPath),
            workingDirectory: Utils.GetBinConfigPath(),
            displayLog: displayLog,
            redirectInput: false,
            environmentVars: environmentVars,
            updateFunc: _updateFunc
        );

        try
        {
            await procService.StartAsync();
            await Task.Delay(100);
            if (procService.HasExited) { throw new Exception(ResUI.FailedToRunCore); }
            AddProcessJob(procService.Handle);
            return procService;
        }
        catch
        {
            await procService.StopAsync();
            procService.Dispose();
            throw;
        }
    }

    private void AddProcessJob(nint processHandle)
    {
        if (Utils.IsWindows())
        {
            _processJob ??= new();
            try
            {
                _processJob?.AddProcess(processHandle);
            }
            catch { }
        }
    }

    #endregion Process
}
