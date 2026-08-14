using Microsoft.Data.Sqlite;

namespace AIXWhatsAppLocal;

/// <summary>
/// Local SQLite database for media deduplication and tracking.
/// Stored at %LocalAppData%\AIXWhatsAppLocal\local.db
/// </summary>
public sealed class MediaDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly object Lock = new();

    public MediaDatabase(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        Initialize();
    }

    private void Initialize()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS media (
                media_id TEXT PRIMARY KEY,
                chat_id TEXT NOT NULL,
                customer_name TEXT NOT NULL,
                phone TEXT NOT NULL,
                received_at TEXT NOT NULL,
                sha256 TEXT NOT NULL UNIQUE,
                local_path TEXT NOT NULL,
                order_folder TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_media_sha256 ON media(sha256);
            CREATE INDEX IF NOT EXISTS idx_media_chat_id ON media(chat_id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Check if an image with this SHA-256 hash was already saved.
    /// </summary>
    public bool IsDuplicate(string sha256)
    {
        lock (Lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM media WHERE sha256 = $sha256";
            cmd.Parameters.AddWithValue("$sha256", sha256);
            return (long)cmd.ExecuteScalar()! > 0;
        }
    }

    /// <summary>
    /// Insert a new media record. Uses INSERT OR IGNORE for safety.
    /// </summary>
    public void InsertMedia(string mediaId, string chatId, string customerName, string phone,
        string receivedAt, string sha256, string localPath, string orderFolder)
    {
        lock (Lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO media
                (media_id, chat_id, customer_name, phone, received_at, sha256, local_path, order_folder)
                VALUES ($media_id, $chat_id, $customer_name, $phone, $received_at, $sha256, $local_path, $order_folder)
                """;
            cmd.Parameters.AddWithValue("$media_id", mediaId);
            cmd.Parameters.AddWithValue("$chat_id", chatId);
            cmd.Parameters.AddWithValue("$customer_name", customerName);
            cmd.Parameters.AddWithValue("$phone", phone);
            cmd.Parameters.AddWithValue("$received_at", receivedAt);
            cmd.Parameters.AddWithValue("$sha256", sha256);
            cmd.Parameters.AddWithValue("$local_path", localPath);
            cmd.Parameters.AddWithValue("$order_folder", orderFolder);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Get the order folder base path (without count suffix) for a chat.
    /// Returns null if no media has been saved for this chat yet.
    /// </summary>
    public string? GetOrderFolderBase(string chatId)
    {
        lock (Lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT order_folder FROM media WHERE chat_id = $chat_id ORDER BY received_at ASC LIMIT 1";
            cmd.Parameters.AddWithValue("$chat_id", chatId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? reader.GetString(0) : null;
        }
    }

    /// <summary>
    /// Get the total number of saved (unique) media for a chat.
    /// </summary>
    public int GetSavedCount(string chatId)
    {
        lock (Lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM media WHERE chat_id = $chat_id";
            cmd.Parameters.AddWithValue("$chat_id", chatId);
            return (int)(long)cmd.ExecuteScalar()!;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}