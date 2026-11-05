using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using SqliteConnection = SocketJack.Multiplayer.Web.NativeSqliteConnection;
using SqliteDataReader = SocketJack.Multiplayer.Web.NativeSqliteReader;
using SocketJack.Multiplayer.Chat;

namespace SocketJack.Multiplayer.Web
{
    /// <summary>Single-process SQLite account/social persistence. Keep its directory outside the website and source tree.</summary>
    public sealed class SqliteAccountStore : IAccountStore, ISocialStore, IFederationStore, IDisposable
    {
        private readonly object sync = new object();
        private readonly SqliteConnection connection;
        private readonly int maxAccounts;
        private bool disposed;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = false, MaxDepth = 32 };
        public SqliteAccountStore(string path, int maxAccounts = 100000)
        {
            if (string.IsNullOrWhiteSpace(path) || maxAccounts < 1 || maxAccounts > 10000000) throw new ArgumentException("A database path and bounded account capacity are required.");
            this.maxAccounts = maxAccounts;
            string fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            connection = new SqliteConnection(fullPath);
            connection.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;" +
                "CREATE TABLE IF NOT EXISTS sj_accounts(id TEXT PRIMARY KEY,username TEXT NOT NULL UNIQUE,display_name TEXT NOT NULL,role TEXT NOT NULL,salt BLOB NOT NULL,password_hash BLOB NOT NULL,iterations INTEGER NOT NULL,banned INTEGER NOT NULL DEFAULT 0);" +
                "CREATE TABLE IF NOT EXISTS sj_sessions(token_hash TEXT PRIMARY KEY,account_id TEXT NOT NULL REFERENCES sj_accounts(id) ON DELETE CASCADE,expires_utc TEXT NOT NULL);" +
                "CREATE INDEX IF NOT EXISTS sj_sessions_account ON sj_sessions(account_id);" +
                "CREATE INDEX IF NOT EXISTS sj_sessions_expiry ON sj_sessions(expires_utc);" +
                "CREATE TABLE IF NOT EXISTS sj_social(id INTEGER PRIMARY KEY CHECK(id=1),payload TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS sj_nodes(node_id TEXT PRIMARY KEY,name TEXT NOT NULL,key_hash TEXT NOT NULL,enabled INTEGER NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS sj_node_credentials(hub_uri TEXT NOT NULL,node_id TEXT NOT NULL,node_key TEXT NOT NULL,PRIMARY KEY(hub_uri,node_id));");
        }
        public bool HasAdministrator { get { lock (sync) return Convert.ToInt64(Scalar("SELECT COUNT(*) FROM sj_accounts WHERE role='admin';"), CultureInfo.InvariantCulture) > 0; } }
        public StoredAccount FindByUsername(string username) { lock (sync) return ReadAccount("SELECT a.* FROM sj_accounts a WHERE username=$value;", username); }
        public StoredAccount FindById(string id) { lock (sync) return ReadAccount("SELECT a.* FROM sj_accounts a WHERE id=$value;", id); }
        public bool TryCreate(StoredAccount value, bool requireFirstAdministrator = false)
        {
            if (value?.Account == null) throw new ArgumentNullException(nameof(value));
            lock (sync)
            {
                ThrowIfDisposed(); using (var transaction = connection.BeginTransaction())
                {
                    using (var count = connection.CreateCommand()) { count.Transaction = transaction; count.CommandText = "SELECT COUNT(*) FROM sj_accounts;"; if ((long)count.ExecuteScalar() >= maxAccounts) throw new WebAccountException("This server has reached its account capacity.", 503); }
                    if (requireFirstAdministrator)
                        using (var first = connection.CreateCommand()) { first.Transaction = transaction; first.CommandText = "SELECT COUNT(*) FROM sj_accounts WHERE role='admin';"; if ((long)first.ExecuteScalar() != 0) return false; }
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction; command.CommandText = "INSERT OR IGNORE INTO sj_accounts(id,username,display_name,role,salt,password_hash,iterations,banned) VALUES($id,$username,$name,$role,$salt,$hash,$iterations,0);";
                        command.Parameters.AddWithValue("$id", value.Account.Id); command.Parameters.AddWithValue("$username", value.Account.Username); command.Parameters.AddWithValue("$name", value.Account.DisplayName); command.Parameters.AddWithValue("$role", value.Account.Role); command.Parameters.AddWithValue("$salt", value.Salt); command.Parameters.AddWithValue("$hash", value.PasswordHash); command.Parameters.AddWithValue("$iterations", value.Iterations);
                        bool inserted = command.ExecuteNonQuery() == 1; transaction.Commit(); return inserted;
                    }
                }
            }
        }
        public void PutSession(string hash, string accountId, DateTime expiry)
        {
            lock (sync)
            {
                // Expired sessions and excessive sessions per account do not grow indefinitely.
                Execute("DELETE FROM sj_sessions WHERE expires_utc < $now;", ("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
                Execute("DELETE FROM sj_sessions WHERE account_id=$id AND token_hash NOT IN (SELECT token_hash FROM sj_sessions WHERE account_id=$id ORDER BY expires_utc DESC LIMIT 15);", ("$id", accountId));
                Execute("INSERT INTO sj_sessions(token_hash,account_id,expires_utc) VALUES($hash,$id,$expiry);", ("$hash", hash), ("$id", accountId), ("$expiry", expiry.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
            }
        }
        public StoredAccount FindSession(string hash, DateTime now)
        {
            lock (sync)
            {
                ThrowIfDisposed(); using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT a.* FROM sj_accounts a JOIN sj_sessions s ON a.id=s.account_id WHERE s.token_hash=$hash AND s.expires_utc>$now;";
                    command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    using (var reader = command.ExecuteReader()) return reader.Read() ? Read(reader) : null;
                }
            }
        }
        public void RevokeSession(string hash) { lock (sync) Execute("DELETE FROM sj_sessions WHERE token_hash=$hash;", ("$hash", hash)); }
        public void RevokeAccountSessions(string id) { lock (sync) Execute("DELETE FROM sj_sessions WHERE account_id=$id;", ("$id", id)); }
        public void SetBanned(string id, bool banned) { lock (sync) { Execute("UPDATE sj_accounts SET banned=$banned WHERE id=$id;", ("$banned", banned ? 1 : 0), ("$id", id)); if (banned) RevokeAccountSessions(id); } }
        public void ReplacePassword(string id, byte[] salt, byte[] hash, int iterations)
        { lock (sync) Execute("UPDATE sj_accounts SET salt=$salt,password_hash=$hash,iterations=$iterations WHERE id=$id;", ("$salt", salt), ("$hash", hash), ("$iterations", iterations), ("$id", id)); }
        public SocialSnapshot Load()
        {
            lock (sync)
            {
                string text = Scalar("SELECT payload FROM sj_social WHERE id=1;") as string;
                if (text == null) return new SocialSnapshot(); if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("Stored social state exceeds its supported bound.");
                return JsonSerializer.Deserialize<SocialSnapshot>(text, Json) ?? throw new InvalidDataException("Stored social state is invalid.");
            }
        }
        public void Save(SocialSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot)); string text = JsonSerializer.Serialize(snapshot, Json);
            if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("Social state exceeds its storage bound.");
            lock (sync) Execute("INSERT INTO sj_social(id,payload) VALUES(1,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload;", ("$payload", text));
        }
        public FederationNode FindNode(string id)
        {
            lock (sync)
            {
                ThrowIfDisposed(); using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT node_id,name,key_hash,enabled FROM sj_nodes WHERE node_id=$id;"; command.Parameters.AddWithValue("$id", id);
                    using (var reader = command.ExecuteReader()) return reader.Read() ? new FederationNode { Id = reader.GetString(0), Name = reader.GetString(1), KeyHash = reader.GetString(2), Enabled = reader.GetInt32(3) != 0 } : null;
                }
            }
        }
        public bool TryAddNode(FederationNode node)
        {
            lock (sync)
            {
                if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM sj_nodes;"), CultureInfo.InvariantCulture) >= 1024) throw new WebAccountException("Linked-server capacity reached.", 503);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT OR IGNORE INTO sj_nodes(node_id,name,key_hash,enabled) VALUES($id,$name,$key,1);";
                    command.Parameters.AddWithValue("$id", node.Id); command.Parameters.AddWithValue("$name", node.Name); command.Parameters.AddWithValue("$key", node.KeyHash); return command.ExecuteNonQuery() == 1;
                }
            }
        }
        public void RevokeNode(string id) { lock (sync) Execute("UPDATE sj_nodes SET enabled=0 WHERE node_id=$id;", ("$id", id)); }
        public string ReadNodeKey(Uri hub, string nodeId)
        {
            lock (sync)
            {
                using (var command = connection.CreateCommand()) { command.CommandText = "SELECT node_key FROM sj_node_credentials WHERE hub_uri=$hub AND node_id=$id;"; command.Parameters.AddWithValue("$hub", hub.AbsoluteUri); command.Parameters.AddWithValue("$id", nodeId); return command.ExecuteScalar() as string; }
            }
        }
        public void SaveNodeKey(Uri hub, string nodeId, string nodeKey)
        { lock (sync) Execute("INSERT INTO sj_node_credentials(hub_uri,node_id,node_key) VALUES($hub,$id,$key) ON CONFLICT(hub_uri,node_id) DO UPDATE SET node_key=$key;", ("$hub", hub.AbsoluteUri), ("$id", nodeId), ("$key", nodeKey)); }
        private StoredAccount ReadAccount(string query, string value)
        {
            ThrowIfDisposed(); if (value == null || value.Length > 128) return null;
            using (var command = connection.CreateCommand()) { command.CommandText = query; command.Parameters.AddWithValue("$value", value); using (var reader = command.ExecuteReader()) return reader.Read() ? Read(reader) : null; }
        }
        private static StoredAccount Read(SqliteDataReader reader) => new StoredAccount
        {
            Account = new WebAccount { Id = reader.GetString(0), Username = reader.GetString(1), DisplayName = reader.GetString(2), Role = reader.GetString(3) },
            Salt = (byte[])reader.GetValue(4), PasswordHash = (byte[])reader.GetValue(5), Iterations = reader.GetInt32(6), IsBanned = reader.GetInt32(7) != 0
        };
        private object Scalar(string query) { ThrowIfDisposed(); using (var command = connection.CreateCommand()) { command.CommandText = query; return command.ExecuteScalar(); } }
        private void Execute(string query, params (string Name, object Value)[] values)
        {
            ThrowIfDisposed(); using (var command = connection.CreateCommand()) { command.CommandText = query; foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value); command.ExecuteNonQuery(); }
        }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(SqliteAccountStore)); }
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; connection.Dispose(); } }
    }
}
