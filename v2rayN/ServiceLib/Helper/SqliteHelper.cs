using System.Collections;

namespace ServiceLib.Helper;

public sealed class SQLiteHelper
{
    private static readonly Lazy<SQLiteHelper> _instance = new(() => new());
    public static SQLiteHelper Instance => _instance.Value;
    private readonly string _connstr;
    private readonly SemaphoreSlim _nowhereBackupGate = new(1, 1);
    private SQLiteConnection _db;
    private SQLiteAsyncConnection _dbAsync;
    private readonly string _configDB = "guiNDB.db";

    public SQLiteHelper(string? databasePath = null)
    {
        _connstr = databasePath ?? Utils.GetConfigPath(_configDB);
        _db = new SQLiteConnection(_connstr, false);
        _dbAsync = new SQLiteAsyncConnection(_connstr, false);
    }

    public CreateTableResult CreateTable<T>()
    {
        return _db.CreateTable<T>();
    }

    public async Task<int> InsertAllAsync(IEnumerable models)
    {
        var snapshot = models.Cast<object>().ToList();
        await EnsurePreNowhereBackupAsync(snapshot);
        return await _dbAsync.InsertAllAsync(snapshot, runInTransaction: true).ConfigureAwait(false);
    }

    public async Task ReplaceSubscriptionProfilesAsync(string subid, IReadOnlyList<ProfileItem> profiles)
    {
        await EnsurePreNowhereBackupAsync(profiles.Cast<object>());
        await _dbAsync.RunInTransactionAsync(connection =>
        {
            connection.Execute("DELETE FROM ProfileItem WHERE Subid = ? AND IsSub = ?", subid, true);
            foreach (var profile in profiles)
            {
                connection.InsertOrReplace(profile);
            }
        });
    }

    public async Task<int> InsertAsync(object model)
    {
        await EnsurePreNowhereBackupAsync([model]);
        return await _dbAsync.InsertAsync(model);
    }

    public async Task<int> ReplaceAsync(object model)
    {
        await EnsurePreNowhereBackupAsync([model]);
        return await _dbAsync.InsertOrReplaceAsync(model);
    }

    public async Task<int> UpdateAsync(object model)
    {
        await EnsurePreNowhereBackupAsync([model]);
        return await _dbAsync.UpdateAsync(model);
    }

    public async Task<int> UpdateAllAsync(IEnumerable models)
    {
        var snapshot = models.Cast<object>().ToList();
        await EnsurePreNowhereBackupAsync(snapshot);
        return await _dbAsync.UpdateAllAsync(snapshot, runInTransaction: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Snapshot the complete live database through SQLite's online backup API before the first
    /// Nowhere write. Never copy a live database file: committed data may still be in its WAL.
    /// A failed backup aborts the mutation; subsequent writes preserve the original backup.
    /// </summary>
    private async Task EnsurePreNowhereBackupAsync(IEnumerable<object> models)
    {
        if (!models.OfType<ProfileItem>().Any(item => item.ConfigType == EConfigType.Nowhere))
        {
            return;
        }
        await _nowhereBackupGate.WaitAsync();
        string? temporary = null;
        try
        {
            var target = _connstr + ".pre-nowhere.bak";
            if (File.Exists(target))
            {
                return;
            }
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await _dbAsync.BackupAsync(temporary);
            File.Move(temporary, target);
        }
        finally
        {
            try
            {
                if (temporary != null && File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                _nowhereBackupGate.Release();
            }
        }
    }

    public async Task<int> DeleteAsync(object model)
    {
        return await _dbAsync.DeleteAsync(model);
    }

    public async Task<int> DeleteAllAsync<T>()
    {
        return await _dbAsync.DeleteAllAsync<T>();
    }

    public async Task<int> ExecuteAsync(string sql)
    {
        return await _dbAsync.ExecuteAsync(sql);
    }

    public async Task<List<T>> QueryAsync<T>(string sql) where T : new()
    {
        return await _dbAsync.QueryAsync<T>(sql);
    }

    public AsyncTableQuery<T> TableAsync<T>() where T : new()
    {
        return _dbAsync.Table<T>();
    }

    public async Task DisposeDbConnectionAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                _db?.Close();
                _db?.Dispose();
            }
            finally
            {
                _db = null;
            }

            try
            {
                var conn = _dbAsync?.GetConnection();
                conn?.Close();
                conn?.Dispose();
            }
            finally
            {
                _dbAsync = null;
            }
        });
    }
}
