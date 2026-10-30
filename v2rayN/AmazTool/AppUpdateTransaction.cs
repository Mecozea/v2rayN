using System.IO.Compression;
using System.Diagnostics;

namespace AmazTool;

/// <summary>Validated staging and exception rollback. Does not promise recovery from power loss.</summary>
public sealed class AppUpdateTransaction : IDisposable
{
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private readonly string _target;
    private readonly string _work;
    private readonly bool _bundle;
    private readonly Action<string>? _bundleValidator;
    private string StagingDirectory => Path.Combine(_work, _bundle ? "staged.app" : "staged");
    public string TargetDirectory => _target;
    public bool IsBundle => _bundle;
    private readonly List<string> _files = [];
    private bool _preserveRecovery;
    public bool RecoveryRequired => _preserveRecovery;

    private AppUpdateTransaction(string target, Action<string>? bundleValidator)
    {
        _bundleValidator = bundleValidator;
        var installation = new DirectoryInfo(Path.GetFullPath(target));
        _bundle = installation.Name == "MacOS" && installation.Parent?.Name == "Contents"
            && installation.Parent.Parent?.Name.EndsWith(".app", StringComparison.Ordinal) == true;
        _target = _bundle ? installation.Parent!.Parent!.FullName : installation.FullName;
        RejectLinks(_target);
        // Bundle staging must be a sibling: touching its resources invalidates the signed seal.
        _work = Path.Combine(_bundle ? Path.GetDirectoryName(_target)! : _target, ".v2rayn-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StagingDirectory);
    }

    public static AppUpdateTransaction Prepare(string packagePath, string targetDirectory, Action<string>? bundleValidator = null)
    {
        var transaction = new AppUpdateTransaction(targetDirectory, bundleValidator);
        try { transaction.Extract(packagePath); return transaction; }
        catch { transaction.Dispose(); throw; }
    }

    private void Extract(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count is 0 or > 20000) { throw new InvalidDataException("Invalid update entry count."); }
        string? root = null;
        long expanded = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Contains('\\') || name.StartsWith('/')) { throw new InvalidDataException("Invalid update path."); }
            var directory = name.EndsWith('/');
            var parts = name.TrimEnd('/').Split('/');
            if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.Contains(':')
                || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32)
                || p.StartsWith(".v2rayn-update-", StringComparison.OrdinalIgnoreCase)))
            { throw new InvalidDataException("Invalid update path segment."); }
            root ??= parts[0];
            if (root != parts[0] || (!directory && parts.Length < 2)) { throw new InvalidDataException("Update must contain exactly one root directory."); }
            var type = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || (type != 0 && type != (directory ? 0x4000 : 0x8000)))
            { throw new InvalidDataException("Update links and special files are forbidden."); }
            var relative = string.Join(Path.DirectorySeparatorChar, parts.Skip(1));
            if (relative.Length == 0) { continue; }
            if (!paths.Add(relative)) { throw new InvalidDataException("Duplicate update path."); }
            if (directory) { continue; }
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) { throw new InvalidDataException("Update exceeds expanded size limit."); }
            var staged = Path.Combine(StagingDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            entry.ExtractToFile(staged);
            if (!OperatingSystem.IsWindows())
            {
                var mode = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1ff);
                // Never preserve setuid/setgid/sticky bits. A package must explicitly preserve executable bits.
                File.SetUnixFileMode(staged, mode == 0 ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead : mode);
            }
            _files.Add(relative);
        }
        if (_bundle && root != Path.GetFileName(_target)) { throw new InvalidDataException("Update bundle name does not match the installed application."); }
        var executable = _bundle ? Path.Combine("Contents", "MacOS", "v2rayN") : OperatingSystem.IsWindows() ? "v2rayN.exe" : "v2rayN";
        if (_bundle && !_files.Contains(Path.Combine("Contents", "Info.plist"), StringComparer.Ordinal))
        { throw new InvalidDataException("Bundle update is missing Contents/Info.plist."); }
        if (!_files.Contains(executable, StringComparer.Ordinal)) { throw new InvalidDataException("Update is missing its root application executable."); }
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(Path.Combine(StagingDirectory, executable)) & UnixFileMode.UserExecute) == 0)
        { throw new InvalidDataException("Update application executable lacks execute permission."); }
        if (_bundle) { (_bundleValidator ?? ValidateBundleSignature)(StagingDirectory); }
        // Validate every target before the caller stops the application.
        foreach (var relative in _files)
        {
            var destination = Path.Combine(_target, relative);
            RejectLinks(destination);
            if (Directory.Exists(destination)) { throw new InvalidDataException("Update file collides with a directory."); }
        }
    }

    public void Apply(Action stopOwnedApplication, Action<string>? afterInstall = null)
    {
        if (_bundle) { ApplyBundle(stopOwnedApplication, afterInstall); return; }
        var journal = new List<(string Relative, bool HadPrevious)>();
        stopOwnedApplication();
        try
        {
            foreach (var relative in _files)
            {
                var destination = Path.Combine(_target, relative);
                RejectLinks(destination);
                var hadPrevious = File.Exists(destination);
                // Managed cores are updated by their own authenticated installer, never by the GUI archive.
                if (hadPrevious && relative.Split(Path.DirectorySeparatorChar)[0].Equals("bin", StringComparison.OrdinalIgnoreCase)) { continue; }
                var backup = Path.Combine(_work, "backup", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (hadPrevious)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(destination, backup);
                }
                journal.Add((relative, hadPrevious));
                File.Move(Path.Combine(StagingDirectory, relative), destination);
                afterInstall?.Invoke(relative);
            }
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var entry in journal.AsEnumerable().Reverse())
            {
                try
                {
                    var destination = Path.Combine(_target, entry.Relative);
                    File.Delete(destination);
                    if (entry.HadPrevious) { File.Move(Path.Combine(_work, "backup", entry.Relative), destination); }
                }
                catch (Exception rollback) { failures.Add(rollback); }
            }
            if (failures.Count > 1)
            {
                _preserveRecovery = true;
                throw new AggregateException($"Update rollback requires manual recovery; backups retained at {_work}.", failures);
            }
            throw;
        }
    }

    private static void ValidateBundleSignature(string stagedBundle)
    {
        if (!OperatingSystem.IsMacOS()) { return; }
        var info = new ProcessStartInfo("/usr/bin/codesign")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "--verify", "--deep", "--strict", stagedBundle }) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info) ?? throw new IOException("Cannot start macOS bundle signature verification.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
            throw new IOException("macOS bundle signature verification timed out.");
        }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException("macOS update bundle failed codesign verification: " + stderr.GetAwaiter().GetResult());
        }
    }

    private void ApplyBundle(Action stopOwnedApplication, Action<string>? afterInstall)
    {
        var staged = StagingDirectory;
        var backup = Path.Combine(_work, "previous.app");
        stopOwnedApplication();
        var backedUp = false;
        var installed = false;
        try
        {
            RejectLinks(_target);
            Directory.Move(_target, backup);
            backedUp = true;
            Directory.Move(staged, _target);
            installed = true;
            // Entire bundle (Info.plist, signature, internal cores and obsolete files) is one update unit.
            afterInstall?.Invoke("bundle");
        }
        catch (Exception failure)
        {
            try
            {
                if (installed) { Directory.Delete(_target, recursive: true); }
                if (backedUp) { Directory.Move(backup, _target); }
            }
            catch (Exception rollback)
            {
                _preserveRecovery = true;
                throw new AggregateException($"Bundle rollback requires manual recovery; backups retained at {_work}.", failure, rollback);
            }
            throw;
        }
    }

    private void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("Update target must not contain symbolic links or junctions."); }
            if (string.Equals(current, _target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) { break; }
        }
    }

    public void Dispose()
    {
        if (!_preserveRecovery && Directory.Exists(_work)) { Directory.Delete(_work, recursive: true); }
    }
}
