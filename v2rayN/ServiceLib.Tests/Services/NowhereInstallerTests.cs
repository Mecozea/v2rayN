using System.IO.Compression;
using System.Formats.Tar;

namespace ServiceLib.Tests.Services;

public class NowhereInstallerTests
{
    [Test]
    [Arguments("../nowhere.exe")]
    [Arguments("/nowhere.exe")]
    [Arguments("C:/nowhere.exe")]
    [Arguments("sub/nowhere.exe")]
    [Arguments("..\\nowhere.exe")]
    [Arguments("NOWHERE.EXE")]
    public async Task ArchiveLayout_RejectsNonRootPaths(string name)
    {
        using var stream = CreateArchive(name);
        using var archive = new ZipArchive(stream);
        var rejected = false;
        try { NowhereInstaller.ValidateArchiveLayout(archive); }
        catch (InvalidDataException) { rejected = true; }
        await rejected.Should().BeTrue();
    }

    [Test]
    public async Task ArchiveLayout_RejectsSymlinksAndDuplicateFiles()
    {
        foreach (var symbolic in new[] { false, true })
        {
            using var stream = CreateArchive("nowhere.exe", duplicate: !symbolic, symlink: symbolic);
            using var archive = new ZipArchive(stream);
            var rejected = false;
            try { NowhereInstaller.ValidateArchiveLayout(archive); }
            catch (InvalidDataException) { rejected = true; }
            await rejected.Should().BeTrue();
        }
    }

    [Test]
    public async Task UntrustedPackage_CannotReplaceExistingCore()
    {
        if (!NowhereInstaller.IsSupported) { return; }
        var directory = Path.Combine(Path.GetTempPath(), "nowhere-install-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, NowhereInstaller.ExecutableName);
            await File.WriteAllTextAsync(target, "previous-valid-core");
            var package = Path.Combine(directory, "untrusted.zip");
            using (var archive = CreateArchive("nowhere.exe"))
            {
                await File.WriteAllBytesAsync(package, archive.ToArray());
            }
            var rejected = false;
            try { await NowhereInstaller.InstallAsync(package, directory); }
            catch (InvalidDataException) { rejected = true; }
            await rejected.Should().BeTrue();
            var retained = await File.ReadAllTextAsync(target);
            await retained.Should().BeEqualTo("previous-valid-core");
            await Directory.GetDirectories(directory).Length.Should().BeEqualTo(0);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task CancelledInstall_CannotCreateManagedDirectory()
    {
        if (!NowhereInstaller.IsSupported) { return; }
        var directory = Path.Combine(Path.GetTempPath(), "nowhere-install-test-" + Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var rejected = false;
        try { await NowhereInstaller.InstallAsync("not-read.zip", directory, cancel.Token); }
        catch (OperationCanceledException) { rejected = true; }
        await rejected.Should().BeTrue();
        await Directory.Exists(directory).Should().BeFalse();
    }

    [Test]
    public async Task Download_RejectsUntrustedBytes_AndRemovesPartialPackage()
    {
        if (!NowhereInstaller.IsSupported) { return; }
        var destination = Path.Combine(Path.GetTempPath(), "nowhere-untrusted-" + Guid.NewGuid().ToString("N") + ".zip");
        using var client = new HttpClient(new PackageHandler());
        var rejected = false;
        try { await NowhereInstaller.DownloadPackageAsync(client, destination); }
        catch (InvalidDataException) { rejected = true; }
        await rejected.Should().BeTrue();
        await File.Exists(destination).Should().BeFalse();
    }

    [Test]
    public async Task Download_DoesNotOverwriteOrDeleteExistingDestination()
    {
        if (!NowhereInstaller.IsSupported) { return; }
        var destination = Path.Combine(Path.GetTempPath(), "nowhere-existing-" + Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllTextAsync(destination, "preserve");
        try
        {
            using var client = new HttpClient(new PackageHandler());
            var rejected = false;
            try { await NowhereInstaller.DownloadPackageAsync(client, destination); }
            catch (IOException) { rejected = true; }
            await rejected.Should().BeTrue();
            var content = await File.ReadAllTextAsync(destination);
            await content.Should().BeEqualTo("preserve");
        }
        finally { File.Delete(destination); }
    }

    [Test]
    public async Task Preflight_RejectsModifiedExecutableBeforeExecutingIt()
    {
        var executable = Path.Combine(Path.GetTempPath(), "nowhere-modified-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(executable, "untrusted, not executable");
        try
        {
            var rejected = false;
            try { await NowhereInstaller.ValidateExecutableAsync(executable); }
            catch (InvalidDataException) { rejected = true; }
            await rejected.Should().BeTrue();
        }
        finally { File.Delete(executable); }
    }

    [Test]
    public async Task Rollback_RejectsUntrustedBackup_WithoutReplacingTarget()
    {
        if (!NowhereInstaller.IsSupported) { return; }
        var directory = Path.Combine(Path.GetTempPath(), "nowhere-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, NowhereInstaller.ExecutableName);
            await File.WriteAllTextAsync(target, "preserve-current");
            await File.WriteAllTextAsync(target + ".previous", "untrusted-backup");
            var rejected = false;
            try { await NowhereInstaller.RollbackAsync(directory); }
            catch (InvalidDataException) { rejected = true; }
            await rejected.Should().BeTrue();
            var remaining = await File.ReadAllTextAsync(target);
            await remaining.Should().BeEqualTo("preserve-current");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    [Arguments("nowhere", false, false)]
    [Arguments("../nowhere", false, true)]
    [Arguments("/nowhere", false, true)]
    [Arguments("nowhere", true, true)]
    public async Task MacArchive_EnforcesSingleRootExecutable(string name, bool duplicate, bool reject)
    {
        using var package = new MemoryStream();
        using (var gzip = new GZipStream(package, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream([1, 2, 3]) });
            if (duplicate)
            {
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "other") { DataStream = new MemoryStream([4]) });
            }
        }
        package.Position = 0;
        var rejected = false;
        try { await NowhereInstaller.ReadArchiveAsync(package, NowhereInstaller.MacArm64, null); }
        catch (InvalidDataException) { rejected = true; }
        await rejected.Should().BeEqualTo(reject);
    }

    [Test]
    public async Task MacArchive_RejectsSymlink()
    {
        using var package = new MemoryStream();
        using (var gzip = new GZipStream(package, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            writer.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "nowhere") { LinkName = "/bin/sh" });
        }
        package.Position = 0;
        var rejected = false;
        try { await NowhereInstaller.ReadArchiveAsync(package, NowhereInstaller.MacArm64, null); }
        catch (InvalidDataException) { rejected = true; }
        await rejected.Should().BeTrue();
    }

    [Test]
    public async Task OfficialMacPackage_MatchesPinnedManifestAndLayout()
    {
        var path = Environment.GetEnvironmentVariable("NOWHERE_MAC_PACKAGE");
        if (string.IsNullOrEmpty(path)) { return; }
        var destination = Path.Combine(Path.GetTempPath(), "nowhere-mac-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var package = File.OpenRead(path);
            var packageHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(package));
            await packageHash.Should().BeEqualTo(NowhereInstaller.MacArm64.PackageSha256);
            package.Position = 0;
            await NowhereInstaller.ReadArchiveAsync(package, NowhereInstaller.MacArm64, destination);
            await using var executable = File.OpenRead(destination);
            var executableHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(executable));
            await executableHash.Should().BeEqualTo(NowhereInstaller.MacArm64.ExecutableSha256);
        }
        finally { File.Delete(destination); }
    }

    private sealed class PackageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri != NowhereInstaller.PackageUri)
            {
                throw new InvalidOperationException("Download source escaped the pinned official asset.");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }

    private static MemoryStream CreateArchive(string name, bool duplicate = false, bool symlink = false)
    {
        var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(name);
            if (symlink) { entry.ExternalAttributes = 0xa000 << 16; }
            using (var output = entry.Open()) { output.WriteByte(1); }
            if (duplicate)
            {
                using var output = archive.CreateEntry(name).Open();
                output.WriteByte(2);
            }
        }
        result.Position = 0;
        return result;
    }
}
