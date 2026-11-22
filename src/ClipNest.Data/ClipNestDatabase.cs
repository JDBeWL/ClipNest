using Dapper;
using Microsoft.Data.Sqlite;

namespace ClipNest.Data;

public sealed class ClipNestDatabase
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    static ClipNestDatabase()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public ClipNestDatabase(string databasePath)
    {
        _databasePath = databasePath;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await EnableWalAsync(connection);
            await connection.ExecuteAsync("PRAGMA foreign_keys=ON;");
            return connection;
        }
        catch (SqliteException ex)
        {
            await connection.DisposeAsync();
            throw new InvalidOperationException(
                $"无法打开数据库：{_databasePath}。请确认目录可写，或设置 CLIPNEST_HOME 指向可写目录。",
                ex);
        }
    }

    private static async Task EnableWalAsync(SqliteConnection connection)
    {
        try
        {
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 14)
        {
            // WAL 需要额外的 -wal/-shm 文件；受限目录下退回传统日志模式，功能不受影响。
            await connection.ExecuteAsync("PRAGMA journal_mode=DELETE;");
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var version = await connection.ExecuteScalarAsync<int>("PRAGMA user_version;");

        if (version < 1)
        {
            await using var first = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration001.Sql, transaction: first);
            await connection.ExecuteAsync("PRAGMA user_version = 1;", transaction: first);
            await first.CommitAsync(ct);
            version = 1;
        }

        if (version < 2)
        {
            await using var second = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration002.Sql, transaction: second);
            await connection.ExecuteAsync("PRAGMA user_version = 2;", transaction: second);
            await second.CommitAsync(ct);
            version = 2;
        }

        if (version < 3)
        {
            await using var third = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration003.Sql, transaction: third);
            await connection.ExecuteAsync("PRAGMA user_version = 3;", transaction: third);
            await third.CommitAsync(ct);
            version = 3;
        }

        if (version < 4)
        {
            await using var fourth = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration004.Sql, transaction: fourth);
            await connection.ExecuteAsync("PRAGMA user_version = 4;", transaction: fourth);
            await fourth.CommitAsync(ct);
            version = 4;
        }

        if (version < 5)
        {
            await using var fifth = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration005.Sql, transaction: fifth);
            await connection.ExecuteAsync("PRAGMA user_version = 5;", transaction: fifth);
            await fifth.CommitAsync(ct);
            version = 5;
        }

        if (version < 6)
        {
            await using var sixth = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration006.Sql, transaction: sixth);
            await connection.ExecuteAsync("PRAGMA user_version = 6;", transaction: sixth);
            await sixth.CommitAsync(ct);
            version = 6;
        }

        if (version < 7)
        {
            await using var seventh = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration007.Sql, transaction: seventh);
            await connection.ExecuteAsync("PRAGMA user_version = 7;", transaction: seventh);
            await seventh.CommitAsync(ct);
            version = 7;
        }

        if (version < 8)
        {
            await using var eighth = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration008.Sql, transaction: eighth);
            await connection.ExecuteAsync("PRAGMA user_version = 8;", transaction: eighth);
            await eighth.CommitAsync(ct);
            version = 8;
        }

        if (version < 9)
        {
            await using var ninth = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration009.Sql, transaction: ninth);
            await connection.ExecuteAsync("PRAGMA user_version = 9;", transaction: ninth);
            await ninth.CommitAsync(ct);
            version = 9;
        }

        if (version < 10)
        {
            await using var tenth = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration010.Sql, transaction: tenth);
            await connection.ExecuteAsync("PRAGMA user_version = 10;", transaction: tenth);
            await tenth.CommitAsync(ct);
            version = 10;
        }

        if (version < 11)
        {
            await using var eleventh = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration011.Sql, transaction: eleventh);
            await connection.ExecuteAsync("PRAGMA user_version = 11;", transaction: eleventh);
            await eleventh.CommitAsync(ct);
        }

        if (version < 12)
        {
            await using var twelfth = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(Migration012.Sql, transaction: twelfth);
            await connection.ExecuteAsync("PRAGMA user_version = 12;", transaction: twelfth);
            await twelfth.CommitAsync(ct);
        }
    }
}
