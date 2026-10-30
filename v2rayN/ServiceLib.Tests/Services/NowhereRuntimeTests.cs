namespace ServiceLib.Tests.Services;

[NotInParallel]
public class NowhereRuntimeTests
{
    [Test]
    [Arguments("portal.example.com")]
    [Arguments("192.0.2.10")]
    [Arguments("2001:db8::10")]
    public async Task BuildAll_PreservesRemoteAndCreatesProtectedSocksFrontend(string address)
    {
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        var config = CoreConfig.CoreConfigTestFactory.CreateConfig();
        config.TunModeItem.EnableTun = true;
        CoreConfig.CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = Node();
        node.Address = address;
        var built = await CoreConfigContextBuilder.BuildAll(config, node);
        await built.Success.Should().BeTrue();
        await built.MainResult.Context.Node.Address.Should().BeEqualTo(address);
        await built.MainResult.Context.IsTunEnabled.Should().BeFalse();
        var front = built.PreSocksResult!.Context;
        await front.Node.ConfigType.Should().BeEqualTo(EConfigType.SOCKS);
        await front.Node.Address.Should().BeEqualTo(Global.Loopback);
        await front.Node.Port.Should().BeEqualTo(built.MainResult.Context.NowherePort);
        await front.ProtectCoreTypeList.Contains(ECoreType.Nowhere).Should().BeTrue();
        if (IPAddress.TryParse(address, out _))
        {
            await front.AppConfig.TunModeItem.RouteExcludeAddress.Any(a => a.StartsWith(address + "/")).Should().BeTrue();
        }
        else
        {
            await front.ProtectDomainList.Contains(address).Should().BeTrue();
        }
        await node.ConfigType.Should().BeEqualTo(EConfigType.Nowhere);
    }

    [Test]
    public async Task GeneratedXrayFrontend_StartsAndNegotiatesSocks()
    {
        var xray = Environment.GetEnvironmentVariable("XRAY_TEST_EXE");
        if (string.IsNullOrEmpty(xray)) { return; }
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        var config = CoreConfig.CoreConfigTestFactory.CreateConfig();
        config.Inbound[0].LocalPort = NowhereRuntime.AllocatePort();
        CoreConfig.CoreConfigTestFactory.BindAppManagerConfig(config);
        var built = await CoreConfigContextBuilder.BuildAll(config, Node());
        await built.Success.Should().BeTrue();
        var file = Path.Combine(Path.GetTempPath(), $"nowhere-frontend-{Guid.NewGuid():N}.json");
        try
        {
            var generated = await CoreConfigHandler.GenerateClientConfig(built.PreSocksResult!.Context, file);
            await generated.Success.Should().BeTrue().Because($"{generated.Msg}; node={built.PreSocksResult!.Context.Node.ConfigType}:{built.PreSocksResult.Context.Node.Port}; core={built.PreSocksResult.Context.RunCoreType}");
            using var process = new ProcessService(xray, "", Path.GetDirectoryName(xray)!, false, false, null, null,
                ["run", "-c", file]);
            await process.StartAsync();
            try
            {
                await NowhereRuntime.WaitForSocksAsync(process, config.Inbound[0].LocalPort, CancellationToken.None);
                await process.HasExited.Should().BeFalse();
            }
            finally { await process.StopAsync(); }
        }
        finally { File.Delete(file); }
    }

    [Test]
    public async Task UnstartedProcess_DisposeAndStopAreSafe()
    {
        using var process = new ProcessService("missing-nowhere-executable", "", ".", false, false, null, null);
        await process.StopAsync();
        await process.HasExited.Should().BeTrue();
    }

    [Test]
    public async Task SubscriptionChain_WithNowhere_IsExplicitlyRejected()
    {
        SQLiteHelper.Instance.CreateTable<SubItem>();
        var node = Node();
        node.Subid = $"nowhere-chain-test-{Guid.NewGuid():N}";
        var sub = new SubItem { Id = node.Subid, PrevProfile = "other-node" };
        await SQLiteHelper.Instance.ReplaceAsync(sub);
        try
        {
            var context = CoreConfig.CoreConfigTestFactory.CreateContext(
                CoreConfig.CoreConfigTestFactory.CreateConfig(), node, ECoreType.Nowhere);
            var (_, validation) = await CoreConfigContextBuilder.ResolveNodeAsync(context, node);
            await validation.Success.Should().BeFalse();
            await validation.Errors.Any(error => error.Contains("chains are not supported")).Should().BeTrue();
        }
        finally { await SQLiteHelper.Instance.DeleteAsync(sub); }
    }

    [Test]
    public async Task Redact_RemovesKeyAndServiceUrl()
    {
        var node = Node();
        var text = NowhereRuntime.Redact($"key={node.Password} failed vector://{node.Password}@localhost/tcp:443?pin=private", node);
        await text.Contains(node.Password).Should().BeFalse();
        await text.Contains("pin=private").Should().BeFalse();
    }

    [Test]
    public async Task InvalidOrStoppedStartup_ReturnsFalseWithoutApplyingProxy()
    {
        var manager = new CoreManager();
        await (await manager.LoadCore(null, null)).Should().BeFalse();
        await manager.CoreStop();
        await (await manager.LoadCore(null, null, expectedStopGeneration: 0)).Should().BeFalse();
        await (await manager.ApplySystemProxyAsync(expectedStopGeneration: 0)).Should().BeFalse();
        await manager.IsRunning.Should().BeFalse();
    }

    [Test]
    public async Task GeneratedSingboxTunFrontend_PassesOfflineConfigurationCheck()
    {
        var executable = Environment.GetEnvironmentVariable("SINGBOX_TEST_EXE");
        if (string.IsNullOrEmpty(executable)) { return; }
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        var config = CoreConfig.CoreConfigTestFactory.CreateConfig();
        config.TunModeItem.EnableTun = true;
        config.TunModeItem.AutoRoute = true;
        config.TunModeItem.StrictRoute = true;
        CoreConfig.CoreConfigTestFactory.BindAppManagerConfig(config);
        var built = await CoreConfigContextBuilder.BuildAll(config, Node());
        await built.Success.Should().BeTrue();
        var file = Path.Combine(Path.GetTempPath(), $"nowhere-tun-check-{Guid.NewGuid():N}.json");
        try
        {
            var generated = await CoreConfigHandler.GenerateClientConfig(built.PreSocksResult!.Context, file);
            await generated.Success.Should().BeTrue().Because($"{generated.Msg}; node={built.PreSocksResult!.Context.Node.ConfigType}:{built.PreSocksResult.Context.Node.Port}; core={built.PreSocksResult.Context.RunCoreType}");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("check");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(file);
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout;
                var error = await stderr;
                if (process.ExitCode != 0) { throw new InvalidOperationException(output + error); }
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        }
        finally { File.Delete(file); }
    }

    [Test]
    public async Task UserStop_CancelsQueuedUpdateAndKeepsLifecycleUsable()
    {
        var manager = new CoreManager();
        var gate = (SemaphoreSlim)typeof(CoreManager).GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        await gate.WaitAsync();
        var cancelled = false;
        Task stop;
        try
        {
            var update = manager.UpdateNowhereAsync(false);
            stop = manager.CoreStop();
            try { await update; }
            catch (OperationCanceledException) { cancelled = true; }
        }
        finally { gate.Release(); }
        await stop;
        await cancelled.Should().BeTrue();
        await manager.StopGeneration.Should().BeEqualTo(1L);
    }

    [Test]
    public async Task FrontendExit_StopsVectorAndIgnoresStaleCallbacks()
    {
        var executable = Environment.GetEnvironmentVariable("NOWHERE_TEST_EXE");
        if (string.IsNullOrEmpty(executable)) { return; }
        var firstPort = NowhereRuntime.AllocatePort();
        var secondPort = NowhereRuntime.AllocatePort();
        using var vector = new ProcessService(executable, "", Path.GetDirectoryName(executable)!, false, false, null, null,
            [NowhereFmt.BuildServiceUrl(Node(), firstPort)]);
        using var frontend = new ProcessService(executable, "", Path.GetDirectoryName(executable)!, false, false, null, null,
            [NowhereFmt.BuildServiceUrl(Node(), secondPort)]);
        var manager = new CoreManager();
        var type = typeof(CoreManager);
        type.GetField("_processService", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, vector);
        type.GetField("_processPreService", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, frontend);
        type.GetField("_updateFunc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager,
            (Func<bool, string, Task>)((_, _) => Task.CompletedTask));
        var handler = type.GetMethod("HandleNowhereFrontendExit", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            await vector.StartAsync();
            await frontend.StartAsync();
            await NowhereRuntime.WaitForSocksAsync(vector, firstPort, CancellationToken.None);
            await NowhereRuntime.WaitForSocksAsync(frontend, secondPort, CancellationToken.None);
            // A callback belonging to another generation must not stop this session.
            await (Task)handler.Invoke(manager, [vector])!;
            await vector.HasExited.Should().BeFalse();
            await frontend.StopAsync();
            await (Task)handler.Invoke(manager, [frontend])!;
            await vector.HasExited.Should().BeTrue();
            await AppManager.Instance.RunningCoreType.Should().BeEqualTo(default(ECoreType));
        }
        finally
        {
            await frontend.StopAsync();
            await vector.StopAsync();
        }
    }

    [Test]
    public async Task VectorLifecycle100Cycles_LeavesNoOwnedProcessOrListener()
    {
        var executable = Environment.GetEnvironmentVariable("NOWHERE_TEST_EXE");
        if (string.IsNullOrEmpty(executable)) { return; }
        for (var cycle = 0; cycle < 100; cycle++)
        {
            var port = NowhereRuntime.AllocatePort();
            using var process = new ProcessService(executable, "", Path.GetDirectoryName(executable)!, false, false, null, null,
                [NowhereFmt.BuildServiceUrl(Node(), port)]);
            await process.StartAsync();
            try { await NowhereRuntime.WaitForSocksAsync(process, port, CancellationToken.None); }
            finally { await process.StopAsync(); }
            await process.HasExited.Should().BeTrue();
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); }
            finally { listener.Stop(); }
        }
    }

    [Test]
    public async Task OwnedVector_StopsWithFrontendAndReleasesBothPorts()
    {
        var executable = Environment.GetEnvironmentVariable("NOWHERE_TEST_EXE");
        if (string.IsNullOrEmpty(executable)) { return; }
        var firstPort = NowhereRuntime.AllocatePort();
        var secondPort = NowhereRuntime.AllocatePort();
        using var first = new ProcessService(executable, "", Path.GetDirectoryName(executable)!, false, false, null, null,
            [NowhereFmt.BuildServiceUrl(Node(), firstPort)]);
        var second = new ProcessService(executable, "", Path.GetDirectoryName(executable)!, false, false, null, null,
            [NowhereFmt.BuildServiceUrl(Node(), secondPort)]);
        first.AttachOwnedProcess(second);
        try
        {
            await first.StartAsync();
            await second.StartAsync();
            await NowhereRuntime.WaitForSocksAsync(first, firstPort, CancellationToken.None);
            await NowhereRuntime.WaitForSocksAsync(second, secondPort, CancellationToken.None);
        }
        finally { await first.StopAsync(); }
        await first.HasExited.Should().BeTrue();
        await second.HasExited.Should().BeTrue();
        foreach (var port in new[] { firstPort, secondPort })
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); }
            finally { listener.Stop(); }
        }
    }

    [Test]
    public async Task VectorProcess_ReadyCancelledAndStopped_ReleasesListener()
    {
        var executable = Environment.GetEnvironmentVariable("NOWHERE_TEST_EXE");
        if (string.IsNullOrEmpty(executable)) { return; }
        var port = NowhereRuntime.AllocatePort();
        using var process = new ProcessService(executable, string.Empty, Path.GetDirectoryName(executable)!,
            false, false, null, null, [NowhereFmt.BuildServiceUrl(Node(), port)]);
        await process.StartAsync();
        try
        {
            await NowhereRuntime.WaitForSocksAsync(process, port, CancellationToken.None);
            await process.HasExited.Should().BeFalse();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var observedCancellation = false;
            try { await NowhereRuntime.WaitForSocksAsync(process, port, cancelled.Token); }
            catch (OperationCanceledException) { observedCancellation = true; }
            await observedCancellation.Should().BeTrue();
        }
        finally { await process.StopAsync(); }
        await process.HasExited.Should().BeTrue();
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        finally { listener.Stop(); }
    }

    [Test]
    [Arguments(ECoreType.Xray)]
    [Arguments(ECoreType.sing_box)]
    public async Task NowhereNode_InNativeCore_IsRejected(ECoreType core)
    {
        var result = NodeValidator.Validate(Node(), core);
        await result.Success.Should().BeFalse();
        await result.Errors.Any().Should().BeTrue();
    }

    private static ProfileItem Node() => new()
    {
        IndexId = "test-nowhere-runtime",
        ConfigType = EConfigType.Nowhere,
        Address = "127.0.0.1",
        Port = 9,
        Password = "0123456789abcdef0123456789abcdef",
    };
}
