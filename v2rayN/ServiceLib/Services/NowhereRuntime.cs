namespace ServiceLib.Services;

/// <summary>Session-only Vector process support. SOCKS readiness is not remote connectivity.</summary>
public static class NowhereRuntime
{
    public static int AllocatePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task WaitForSocksAsync(ProcessService process, int port, CancellationToken token, string processName = "Nowhere")
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var deadline = timeout.Token;
        while (true)
        {
            deadline.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"{processName} exited before its SOCKS listener became ready. Check the preceding core log for the cause.");
            }
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline);
            attempt.CancelAfter(TimeSpan.FromMilliseconds(250));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token);
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, attempt.Token);
                var response = new byte[2];
                await stream.ReadExactlyAsync(response, attempt.Token);
                if (response[0] == 5 && response[1] == 0)
                {
                    return;
                }
            }
            catch (SocketException) { }
            catch (IOException) { }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            await Task.Delay(50, deadline);
        }
    }

    public static string Redact(string text, ProfileItem node)
    {
        if (!string.IsNullOrEmpty(node.Password))
        {
            text = text.Replace(node.Password, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }
        return Regex.Replace(text, @"(?:vector|nowhere)://[^\s]+", "[Nowhere URL redacted]", RegexOptions.IgnoreCase);
    }
}
