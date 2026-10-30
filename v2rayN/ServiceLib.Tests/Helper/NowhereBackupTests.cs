namespace ServiceLib.Tests.Helper;

public class NowhereBackupTests
{
    [Test]
    public async Task FirstNowhereWrite_BacksUpLiveWalSnapshot_AndPreservesOriginalBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nowhere-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "profiles.db");
        var db = new SQLiteHelper(database);
        try
        {
            db.CreateTable<ProfileItem>();
            using (var setup = new SQLiteConnection(database, false))
            {
                setup.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
            }
            await db.InsertAsync(new ProfileItem { IndexId = "before", ConfigType = EConfigType.VLESS, Remarks = "preserve-me" });
            await File.Exists(database + ".pre-nowhere.bak").Should().BeFalse();
            await db.InsertAsync(new ProfileItem { IndexId = "first-nowhere", ConfigType = EConfigType.Nowhere });
            var backupPath = database + ".pre-nowhere.bak";
            using (var backup = new SQLiteConnection(backupPath, false))
            {
                var rows = backup.Table<ProfileItem>().ToList();
                await rows.Count.Should().BeEqualTo(1);
                await rows[0].IndexId.Should().BeEqualTo("before");
                await rows[0].Remarks.Should().BeEqualTo("preserve-me");
            }
            var firstHash = SHA256.HashData(await File.ReadAllBytesAsync(backupPath));
            await db.InsertAsync(new ProfileItem { IndexId = "second-nowhere", ConfigType = EConfigType.Nowhere });
            var secondHash = SHA256.HashData(await File.ReadAllBytesAsync(backupPath));
            await firstHash.SequenceEqual(secondHash).Should().BeTrue();
            await (await db.TableAsync<ProfileItem>().CountAsync()).Should().BeEqualTo(3);
        }
        finally
        {
            await db.DisposeDbConnectionAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
