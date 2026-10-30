using System.IO.Compression;
using System.Formats.Tar;

namespace ServiceLib.Services;

/// <summary>
/// Windows x64 / macOS Apple Silicon installer with an application-pinned release manifest. Subscription data must
/// never choose the source, digest, executable name, arguments, or managed directory.
/// The manifest trusts GitHub's release API over HTTPS; upstream provides no signature here.
/// </summary>
public static class NowhereInstaller
{
    public const string Version = "3.2.0";
    public sealed record ReleaseManifest(string AssetName, string PackageSha256, string ExecutableSha256, string ExecutableName, string VersionPlatform);
    public static ReleaseManifest WindowsX64 { get; } = new("nowhere-x86_64-pc-windows-msvc.zip", "bdc01f2df2ec8c7e01c7d994ab78e0e1977f9e278e396634aac206d8cefded82", "36b90a7eabbe530d508ec0c941f60c22d8decf7177f0b08261c13c81cdc0490c", "nowhere.exe", "windows/x86_64");
    public static ReleaseManifest MacArm64 { get; } = new("nowhere-aarch64-apple-darwin.tar.gz", "bf1f348eb65498d69b390066b567d3ff4a20b1cba04db13fe1e08dba8cd86f79", "774c5126ae706f9c1569ba11e42e1d32cac1f807fb28fd72e5c370860c1538f9", "nowhere", "macos/aarch64");
    public static ReleaseManifest CurrentManifest => OperatingSystem.IsMacOS() ? MacArm64 : WindowsX64;
    public static string ExecutableName => CurrentManifest.ExecutableName;
    public static string ExecutableSha256 => CurrentManifest.ExecutableSha256;
    public static string PackageSha256 => CurrentManifest.PackageSha256;
    public static Uri PackageUri => new($"https://github.com/NodePassProject/Nowhere/releases/download/v{Version}/{CurrentManifest.AssetName}");
    private const long MaxPackageBytes = 128 * 1024 * 1024;
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public static bool IsSupported => (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64
        && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        || (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64
        && RuntimeInformation.ProcessArchitecture == Architecture.Arm64);

    /// <summary>Check against the application-trusted release without changing files or the session.</summary>
    public static async Task<UpdateResult> CheckUpdateAsync(string executable, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) { return new UpdateResult(false, ResUI.NowhereUnsupportedPlatform); }
        try
        {
            await ValidateExecutableAsync(executable, cancellationToken);
            return new UpdateResult(false, string.Format(ResUI.NowhereCoreCurrent, Version));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.ComponentModel.Win32Exception)
        {
            return new UpdateResult(true, new SemanticVersion(Version)) { Url = PackageUri.AbsoluteUri, Msg = string.Format(ResUI.NowhereCoreAvailable, Version) };
        }
    }

    /// <summary>Caller owns the staged package and should dispose it after install/cancel.</summary>
    public sealed class VerifiedPackage : IDisposable
    {
        public string Path { get; }
        internal VerifiedPackage(string path) => Path = path;
        public void Dispose()
        {
            if (File.Exists(Path)) { File.Delete(Path); }
        }
    }

    /// <summary>Stage and authenticate the package while the existing proxy can still serve downloads.</summary>
    public static async Task<VerifiedPackage> DownloadVerifiedPackageAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2rayN-nowhere-{Guid.NewGuid():N}-{CurrentManifest.AssetName}");
        await DownloadPackageAsync(client, path, cancellationToken);
        return new VerifiedPackage(path);
    }

    /// <summary>Download only the pinned official asset to a caller-managed new file; delete partial data on failure.</summary>
    public static async Task DownloadPackageAsync(HttpClient client, string destination, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        var created = false;
        try
        {
            using var response = await client.GetAsync(PackageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxPackageBytes)
            {
                throw new InvalidDataException("Nowhere package exceeds the size limit.");
            }
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                created = true;
                var buffer = new byte[81920];
                long length = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    length += read;
                    if (length > MaxPackageBytes)
                    {
                        throw new InvalidDataException("Nowhere package exceeds the size limit.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                output.Position = 0;
                await ValidatePackageDigestAsync(output, cancellationToken);
                output.Position = 0;
                await ReadArchiveAsync(output, CurrentManifest, null, cancellationToken);
            }
        }
        catch
        {
            if (created && File.Exists(destination)) { File.Delete(destination); }
            throw;
        }
    }

    /// <summary>Convenience API for first installation when no managed core is running.</summary>
    public static async Task DownloadAndInstallAsync(HttpClient client, string managedDirectory, CancellationToken cancellationToken = default)
    {
        using var package = await DownloadVerifiedPackageAsync(client, cancellationToken);
        await InstallAsync(package.Path, managedDirectory, cancellationToken);
    }

    private static async Task ValidatePackageDigestAsync(Stream package, CancellationToken cancellationToken)
    {
        var hash = await SHA256.HashDataAsync(package, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(PackageSha256)))
        {
            throw new InvalidDataException("Nowhere package SHA-256 does not match the trusted release manifest.");
        }
    }

    /// <summary>Install a local copy only after it matches the embedded trusted release digest.</summary>
    public static async Task InstallAsync(string packagePath, string managedDirectory, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        await InstallGate.WaitAsync(cancellationToken);
        string? staging = null;
        try
        {
            await using var package = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (package.Length > MaxPackageBytes)
            {
                throw new InvalidDataException("Nowhere package exceeds the size limit.");
            }
            await ValidatePackageDigestAsync(package, cancellationToken);
            package.Position = 0;
            var directory = Path.GetFullPath(managedDirectory);
            Directory.CreateDirectory(directory);
            staging = Path.Combine(directory, $".nowhere-stage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var candidate = Path.Combine(staging, ExecutableName);
            await ReadArchiveAsync(package, CurrentManifest, candidate, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            await ValidateExecutableAsync(candidate, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(directory, ExecutableName);
            if (File.Exists(target))
            {
                // Same-volume replacement is atomic. Failure (including a running core's file lock)
                // leaves the existing target intact. The previous binary remains available to roll back.
                File.Replace(candidate, target, Path.Combine(directory, ExecutableName + ".previous"));
            }
            else
            {
                File.Move(candidate, target);
            }
        }
        finally
        {
            try
            {
                if (staging != null && Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            finally
            {
                InstallGate.Release();
            }
        }
    }

    /// <summary>Only a single regular root executable is allowed; never extract arbitrary archive paths.</summary>
    public static async Task ReadArchiveAsync(Stream package, ReleaseManifest manifest, string? destination, CancellationToken token = default)
    {
        if (manifest.AssetName.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            ValidateArchiveLayout(archive);
            if (destination != null)
            {
                await using var input = archive.Entries[0].Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, token);
            }
            return;
        }
        using var gzip = new GZipStream(package, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip, leaveOpen: true);
        var entry = await reader.GetNextEntryAsync(copyData: false, token);
        if (entry == null || entry.Name != manifest.ExecutableName
            || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            || entry.Length is <= 0 or > MaxPackageBytes || entry.DataStream == null)
        {
            throw new InvalidDataException("Nowhere archive must contain exactly one regular root executable.");
        }
        if (destination != null)
        {
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await entry.DataStream.CopyToAsync(output, token);
        }
        if (await reader.GetNextEntryAsync(copyData: false, token) != null)
        {
            throw new InvalidDataException("Nowhere archive contains unexpected additional entries.");
        }
    }

    /// <summary>Strict allowlist also rejects traversal, duplicate paths, symlinks and archive bombs.</summary>
    public static void ValidateArchiveLayout(ZipArchive archive)
    {
        if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "nowhere.exe")
        {
            throw new InvalidDataException("Nowhere archive must contain exactly nowhere.exe at its root.");
        }
        var entry = archive.Entries[0];
        var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
        if (entry.Length is <= 0 or > MaxPackageBytes || (unixType != 0 && unixType != 0x8000)
            || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Nowhere archive contains an unsupported file type or size.");
        }
    }

    public static async Task RollbackAsync(string managedDirectory, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            var target = Path.Combine(Path.GetFullPath(managedDirectory), ExecutableName);
            var previous = target + ".previous";
            if (!File.Exists(previous))
            {
                throw new FileNotFoundException("No previous Nowhere core is available.");
            }
            await ValidateExecutableAsync(previous, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                File.Replace(previous, target, null);
            }
            else
            {
                File.Move(previous, target);
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    public static async Task ValidateExecutableAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] digest;
        await using (var executable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            digest = await SHA256.HashDataAsync(executable, cancellationToken);
        }
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(ExecutableSha256)))
        {
            throw new InvalidDataException("Nowhere executable SHA-256 does not match the trusted release manifest.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--version");
        using var process = Process.Start(info) ?? throw new IOException("Unable to validate the Nowhere executable.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = (await stdout).Trim();
            await stderr;
            if (process.ExitCode != 0 || output != $"nowhere-v{Version} {CurrentManifest.VersionPlatform}")
            {
                throw new InvalidDataException("Nowhere executable version does not match the trusted release manifest.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(killTimeout.Token);
            }
        }
    }

    private static void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Nowhere installation supports Windows x64 and native macOS Apple Silicon.");
        }
    }
}
