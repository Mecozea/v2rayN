using AmazTool;
using System.IO.Compression;

namespace ServiceLib.Tests.Services;

public class AppUpdateTransactionTests
{
    private static string Exe => OperatingSystem.IsWindows() ? "v2rayN.exe" : "v2rayN";

    [Test]
    public async Task UpdateFailure_RestoresOldFilesAndRemovesNewFiles()
    {
        var target = NewDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(target, Exe), "old-app");
            await File.WriteAllTextAsync(Path.Combine(target, "old.txt"), "old-content");
            var zip = MakeZip(target, [("root/" + Exe, "new-app"), ("root/new.txt", "new-file"), ("root/old.txt", "changed")]);
            using var transaction = AppUpdateTransaction.Prepare(zip, target);
            var stopped = false;
            var failed = false;
            try { transaction.Apply(() => stopped = true, path => { if (path == "old.txt") { throw new IOException("Injected write failure after third replacement."); } }); }
            catch (IOException) { failed = true; }
            await failed.Should().BeTrue();
            await stopped.Should().BeTrue();
            await transaction.RecoveryRequired.Should().BeFalse();
            await (await File.ReadAllTextAsync(Path.Combine(target, Exe))).Should().BeEqualTo("old-app");
            await (await File.ReadAllTextAsync(Path.Combine(target, "old.txt"))).Should().BeEqualTo("old-content");
            await File.Exists(Path.Combine(target, "new.txt")).Should().BeFalse();
        }
        finally { Directory.Delete(target, true); }
    }

    [Test]
    public async Task SuccessfulUpdate_PreservesManagedCoreAndUnixExecuteBits()
    {
        var target = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(target, "bin"));
            await File.WriteAllTextAsync(Path.Combine(target, "bin", "nowhere"), "retain-core");
            var zip = MakeZip(target, [("root/" + Exe, "new-app"), ("root/bin/nowhere", "do-not-overwrite"), ("root/added", "new")]);
            using var transaction = AppUpdateTransaction.Prepare(zip, target);
            transaction.Apply(() => { });
            await (await File.ReadAllTextAsync(Path.Combine(target, Exe))).Should().BeEqualTo("new-app");
            await (await File.ReadAllTextAsync(Path.Combine(target, "bin", "nowhere"))).Should().BeEqualTo("retain-core");
            if (!OperatingSystem.IsWindows())
            {
                await ((File.GetUnixFileMode(Path.Combine(target, Exe)) & UnixFileMode.UserExecute) != 0).Should().BeTrue();
            }
        }
        finally { Directory.Delete(target, true); }
    }

    [Test]
    [Arguments("root/../escape")]
    [Arguments("root/sub/../../escape")]
    [Arguments("root/C:/escape")]
    [Arguments("root/sub\\escape")]
    [Arguments("other/file")]
    [Arguments("root/" )]
    public async Task InvalidArchive_IsRejectedBeforeStop(string badName)
    {
        var target = NewDirectory();
        try
        {
            var zip = MakeZip(target, [("root/" + Exe, "app"), (badName, "unsafe")]);
            var rejected = false;
            try { using var transaction = AppUpdateTransaction.Prepare(zip, target); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { rejected = true; }
            await rejected.Should().BeTrue();
            await File.Exists(Path.Combine(target, Exe)).Should().BeFalse();
        }
        finally { Directory.Delete(target, true); }
    }

    [Test]
    public async Task DuplicateAndSymlinkEntries_AreRejected()
    {
        foreach (var symbolic in new[] { true, false })
        {
            var target = NewDirectory();
            try
            {
                var zip = MakeZip(target, [("root/" + Exe, "app"), ("root/" + Exe, "duplicate")], symbolic);
                var rejected = false;
                try { using var transaction = AppUpdateTransaction.Prepare(zip, target); }
                catch (InvalidDataException) { rejected = true; }
                await rejected.Should().BeTrue();
            }
            finally { Directory.Delete(target, true); }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BundleUpdate_ReplacesCompleteSealedBundleOrRestoresItOnFailure(bool fail)
    {
        var parent = NewDirectory();
        var app = Path.Combine(parent, "v2rayN.app");
        var macos = Path.Combine(app, "Contents", "MacOS");
        Directory.CreateDirectory(Path.Combine(macos, "bin"));
        Directory.CreateDirectory(Path.Combine(app, "Contents", "_CodeSignature"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(macos, "v2rayN"), "old-app");
            await File.WriteAllTextAsync(Path.Combine(macos, "obsolete.dll"), "obsolete");
            await File.WriteAllTextAsync(Path.Combine(macos, "bin", "nowhere"), "old-core");
            await File.WriteAllTextAsync(Path.Combine(app, "Contents", "Info.plist"), "old-info");
            await File.WriteAllTextAsync(Path.Combine(app, "Contents", "_CodeSignature", "CodeResources"), "old-seal");
            var zip = MakeZip(parent,
            [
                ("v2rayN.app/Contents/MacOS/v2rayN", "new-app"),
                ("v2rayN.app/Contents/MacOS/bin/nowhere", "new-core"),
                ("v2rayN.app/Contents/Info.plist", "new-info"),
                ("v2rayN.app/Contents/_CodeSignature/CodeResources", "new-seal")
            ]);
            var validated = false;
            // Explicit test validator: synthetic text fixtures cannot carry real Mach-O code signatures.
            using (var transaction = AppUpdateTransaction.Prepare(zip, macos, staged =>
            {
                if (!staged.EndsWith(".app", StringComparison.Ordinal)) { throw new InvalidDataException("Missing staged bundle extension."); }
                validated = true;
            }))
            {
                await validated.Should().BeTrue();
                await transaction.IsBundle.Should().BeTrue();
                await transaction.TargetDirectory.Should().BeEqualTo(app);
                // Prevalidation never adds temporary files inside the signed bundle.
                await Directory.GetDirectories(app, ".v2rayn-update-*").Length.Should().BeEqualTo(0);
                try { transaction.Apply(() => { }, _ => { if (fail) { throw new IOException("Injected bundle activation failure."); } }); }
                catch (IOException) when (fail) { }
                await transaction.RecoveryRequired.Should().BeFalse();
            }
            await (await File.ReadAllTextAsync(Path.Combine(macos, "v2rayN"))).Should().BeEqualTo(fail ? "old-app" : "new-app");
            await (await File.ReadAllTextAsync(Path.Combine(macos, "bin", "nowhere"))).Should().BeEqualTo(fail ? "old-core" : "new-core");
            await (await File.ReadAllTextAsync(Path.Combine(app, "Contents", "Info.plist"))).Should().BeEqualTo(fail ? "old-info" : "new-info");
            await (await File.ReadAllTextAsync(Path.Combine(app, "Contents", "_CodeSignature", "CodeResources"))).Should().BeEqualTo(fail ? "old-seal" : "new-seal");
            await File.Exists(Path.Combine(macos, "obsolete.dll")).Should().BeEqualTo(fail);
            await Directory.GetDirectories(parent, ".v2rayn-update-*").Length.Should().BeEqualTo(0);
        }
        finally { Directory.Delete(parent, true); }
    }

    [Test]
    public async Task BundleSignatureFailure_RejectsBeforeChangingInstalledBundle()
    {
        var parent = NewDirectory();
        var macos = Path.Combine(parent, "v2rayN.app", "Contents", "MacOS");
        Directory.CreateDirectory(macos);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(macos, "v2rayN"), "old-app");
            var zip = MakeZip(parent, [("v2rayN.app/Contents/MacOS/v2rayN", "new-app"), ("v2rayN.app/Contents/Info.plist", "info")]);
            var rejected = false;
            try
            {
                using var transaction = AppUpdateTransaction.Prepare(zip, macos,
                    _ => throw new InvalidDataException("Injected signature verification failure."));
            }
            catch (InvalidDataException) { rejected = true; }
            await rejected.Should().BeTrue();
            await (await File.ReadAllTextAsync(Path.Combine(macos, "v2rayN"))).Should().BeEqualTo("old-app");
            await Directory.GetDirectories(parent, ".v2rayn-update-*").Length.Should().BeEqualTo(0);
        }
        finally { Directory.Delete(parent, true); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "v2rayn-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string MakeZip(string target, (string Name, string Content)[] files, bool symbolic = false)
    {
        var path = Path.Combine(target, "test.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            var entry = archive.CreateEntry(name);
            entry.ExternalAttributes = (symbolic ? 0xa1ed : 0x81ed) << 16;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }
}
