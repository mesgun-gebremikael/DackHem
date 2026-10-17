using Microsoft.Data.Sqlite;

namespace DackHem.Api.Services;

// Creates consistent SQLite snapshots without copying a live database file directly.
// Local snapshots are not a substitute for off-server backups.
public sealed class SqliteBackupService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SqliteBackupService> _logger;

    public SqliteBackupService(IConfiguration configuration, ILogger<SqliteBackupService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup initialize the database tables first.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try { CreateBackup(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Local SQLite backup failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void CreateBackup()
    {
        var configuredPath = _configuration["Database:Path"];
        var databasePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredPath) ? "dackhem.db" : configuredPath);
        if (!File.Exists(databasePath))
        {
            _logger.LogWarning("SQLite backup skipped: database file not found.");
            return;
        }

        var parent = Path.GetDirectoryName(databasePath)!;
        var backupDirectory = Path.Combine(parent, "dackhem-backups");
        Directory.CreateDirectory(backupDirectory);

        var name = "dackhem-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".db";
        var finalPath = Path.Combine(backupDirectory, name);
        var temporaryPath = finalPath + ".tmp";
        try
        {
            using (var source = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            using (var destination = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = temporaryPath }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
                // SQLite connection pooling may keep the temporary file handle open
                // after Dispose. Clear the pools before renaming the snapshot.
                SqliteConnection.ClearAllPools();
            }
            SqliteConnection.ClearAllPools();
            File.Move(temporaryPath, finalPath);
            _logger.LogInformation("Local SQLite backup created.");

            // Keep the newest 14 successful snapshots.
            var oldFiles = Directory.GetFiles(backupDirectory, "dackhem-*.db")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(14);
            foreach (var oldFile in oldFiles) File.Delete(oldFile);
        }
        finally
        {
            // Never mask the original failure with a second IOException during cleanup.
            SqliteConnection.ClearAllPools();
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException ex) { _logger.LogWarning(ex, "Temporary SQLite backup file is still locked."); }
            }
        }
    }
}
