using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Serialization.Json.Converters;
using Database = SocketJack.Net.Database.Database;

namespace SocketJack.Net.Database {

    /// <summary>
    /// MSSQL Server Emulator implementing TDS (Tabular Data Stream) protocol.
    /// <para>
    /// Can run standalone (inherits <see cref="TcpServer"/>) or be registered
    /// as a protocol handler on <see cref="MutableTcpServer"/> via
    /// <see cref="TdsProtocolHandler"/>.
    /// </para>
    /// <para>
    /// All databases, tables and rows live in memory and are persisted to
    /// <see cref="DataPath"/> as JSON. Use <see cref="ImportFromMssql"/> to
    /// import schema and data from an existing SQL Server instance.
    /// </para>
    /// </summary>
    public partial class DataServer : TcpServer {

        #region Properties

        /// <summary>
        /// Server instance name (e.g., "MSSQLSERVER")
        /// </summary>
        public string InstanceName { get; set; } = "MSSQLSERVER";

        /// <summary>
        /// Server version to report to clients (e.g., "17.00.4025" for SQL Server 2025)
        /// </summary>
        public string ServerVersion { get; set; } = "17.00.4025.3";

        /// <summary>
        /// Default username for SQL authentication
        /// </summary>
        public string Username { get; set; } = "sa";

        /// <summary>
        /// Default password for SQL authentication
        /// </summary>
        public string Password { get; set; } = "";

        /// <summary>
        /// Default database name
        /// </summary>
        public string DefaultDatabase { get; set; } = "db";

        /// <summary>
        /// Database that stores the SQL Admin IP whitelist table. When blank,
        /// <see cref="DefaultDatabase"/> is used.
        /// </summary>
        public string SqlAdminIpWhitelistDatabaseName { get; set; } = "";

        /// <summary>
        /// Table used to persist SQL/Admin IP whitelist entries.
        /// </summary>
        public const string SqlAdminIpWhitelistTableName = "SqlAdminIpWhitelist";

        /// <summary>
        /// Server name to report to clients
        /// </summary>
        public string ServerName { get; set; } = Environment.MachineName;

        /// <summary>
        /// Enable Windows authentication (currently defaults to SQL authentication)
        /// </summary>
        public bool AllowWindowsAuth { get; set; } = false;

        /// <summary>
        /// Enable SQL Server authentication
        /// </summary>
        public bool AllowSqlAuth { get; set; } = true;

        /// <summary>
        /// TDS encryption mode negotiated during Pre-Login.
        /// <list type="bullet">
        /// <item><c>0x00</c> – <b>ENCRYPT_OFF</b>: Encrypt the login packet only, then revert to plaintext.</item>
        /// <item><c>0x01</c> – <b>ENCRYPT_ON</b>: Encrypt all traffic after the TLS handshake.</item>
        /// <item><c>0x02</c> – <b>ENCRYPT_NOT_SUP</b>: No encryption at all (clients requiring encryption will refuse).</item>
        /// <item><c>0x03</c> – <b>ENCRYPT_REQ</b>: Server requires full encryption.</item>
        /// </list>
        /// </summary>
        public byte EncryptionMode { get; set; } = 0x00; // ENCRYPT_OFF (login-only TLS, then plaintext)

        /// <summary>
        /// X.509 certificate used for TDS encryption (TLS handshake).
        /// When <see langword="null"/> and <see cref="EncryptionMode"/> is not <c>0x02</c>,
        /// a self-signed certificate is generated automatically on first use.
        /// </summary>
        public X509Certificate2 Certificate {
            get {
                if (_certificate == null && EncryptionMode != 0x02)
                    _certificate = GenerateSelfSignedCertificate();
                return _certificate;
            }
            set { _certificate = value; }
        }
        private X509Certificate2 _certificate;

        /// <summary>
        /// Generates a self-signed X.509 certificate suitable for TDS encryption.
        /// The certificate is valid for 10 years and uses a 2048-bit RSA key.
        /// </summary>
        public static X509Certificate2 GenerateSelfSignedCertificate(string subjectName = null) {
            subjectName = subjectName ?? "CN=" + Environment.MachineName;
            using (var rsa = RSA.Create(2048)) {
                var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // Server Authentication
                var sanBuilder = new SubjectAlternativeNameBuilder();
                sanBuilder.AddDnsName(Environment.MachineName);
                sanBuilder.AddDnsName("localhost");
                request.CertificateExtensions.Add(sanBuilder.Build());
                var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(10));
                // Export and re-import so the private key is stored in a way
                // compatible with SslStream on all platforms.
                // Use MachineKeySet (not EphemeralKeySet) for .NET Framework compatibility.
                var pfx = cert.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
                return X509CertificateLoader.LoadPkcs12(
                    pfx,
                    null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet
                );
#else
                return new X509Certificate2(
                    pfx,
                    (string)null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet
                );
#endif
            }
        }

        /// <summary>
        /// Collection of registered users with their passwords
        /// </summary>
        public ConcurrentDictionary<string, string> Users { get; set; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// When enabled, SQL logins are accepted only from loopback or addresses
        /// listed in <see cref="AllowedSqlLoginIpAddresses"/>.
        /// </summary>
        public bool EnforceSqlLoginIpAllowList { get; set; } = false;

        /// <summary>
        /// Allows local SQL admin/bootstrap access even when the SQL login IP
        /// allowlist is enforced.
        /// </summary>
        public bool AllowLoopbackSqlLogin { get; set; } = true;

        /// <summary>
        /// Remote IP addresses allowed to authenticate to SQL when
        /// <see cref="EnforceSqlLoginIpAllowList"/> is enabled. Values are also
        /// mirrored into <see cref="SqlAdminIpWhitelistTableName"/> when added
        /// through <see cref="AllowSqlLoginIpAddress"/>.
        /// </summary>
        public ConcurrentDictionary<string, byte> AllowedSqlLoginIpAddresses { get; set; } = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Databases available on this server
        /// </summary>
        public ConcurrentDictionary<string, Database> Databases { get; set; } = new ConcurrentDictionary<string, Database>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Active sessions mapped by connection ID
        /// </summary>
        internal ConcurrentDictionary<Guid, SqlSession> Sessions = new ConcurrentDictionary<Guid, SqlSession>();

        /// <summary>
        /// Path used to persist database state.
        /// If the path resolves to a file (or has a file extension), legacy JSON
        /// persistence is used. Otherwise persistence is split across a folder.
        /// </summary>
        public string DataPath { get; set; } = "dataserver.json";

        /// <summary>
        /// When <see langword="true"/>, every dirty data mutation (table/row add/remove)
        /// persists changes to <see cref="DataPath"/> before returning by default.
        /// </summary>
        public bool AutoSave { get; set; } = true;

        /// <summary>
        /// Wait for durable storage before acknowledging a mutation. Disable only
        /// for workloads that explicitly accept losing pending changes on a crash.
        /// </summary>
        public bool DurableAutoSave { get; set; } = true;

        /// <summary>
        /// Indicates whether a mutation has been scheduled but not yet persisted.
        /// </summary>
        public bool HasUnsavedChanges => Volatile.Read(ref _dirtyVersion) != Volatile.Read(ref _savedVersion);

        /// <summary>
        /// SHA-256 hash of the last successfully persisted logical database snapshot.
        /// </summary>
        public string LastPersistedHash => _lastPersistedHash ?? "";

        /// <summary>
        /// Enables the database key/value cache optimizer. When enabled, simple
        /// equality lookups are cached in memory and hot key metadata is persisted
        /// so optimized cache indexes can be warmed on the next load.
        /// </summary>
        public bool EnableCacheOptimizing { get; set; } = true;

        /// <summary>
        /// File path used to persist optimized cache metadata. When
        /// <see langword="null"/>, the path is derived from <see cref="DataPath"/>.
        /// </summary>
        public string CacheMetadataPath { get; set; }

        /// <summary>
        /// Maximum number of hot key/value lookups to keep in the optimized cache
        /// metadata and warm on startup.
        /// </summary>
        public int CacheOptimizationMaxKeys { get; set; } = 1024;

        /// <summary>
        /// Maximum batching delay when DurableAutoSave is disabled. Further edits
        /// do not postpone the scheduled write. Failed saves are retried every five seconds.
        /// </summary>
        public int AutoSaveDebounceMs { get; set; } = 500;

        private readonly SemaphoreSlim _persistenceLock = new SemaphoreSlim(1, 1);
        private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();
        private Timer _debounceTimer;
        private readonly object _saveTimerGate = new object();
        private Task _backgroundSaveTask = Task.CompletedTask;
        private bool _persistenceDisposed;
        private Exception _loadFailure;
        private const string DurablePayloadHeader = "SJDP1:";
        private readonly DatabaseCacheOptimizer _cacheOptimizer = new DatabaseCacheOptimizer();
        private int _dirtyVersion;
        private int _savedVersion;
        private string _lastPersistedHash = "";

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new TypeConverter() }
        };
        private static readonly byte[] _dataEncryptionSecret = Encoding.UTF8.GetBytes(GetDataServerMachineSecret());
        private const string EncryptedPayloadHeader = "SJEP1:";
        private const int EncryptionSaltSize = 16;
        private const int EncryptionNonceSize = 12;
        private const int EncryptionTagSize = 16;
        private const int EncryptionKeySize = 32;
        private const int EncryptionDerivationIterations = 120000;
        private const int PasswordHashIterations = 210000;
        private const int PasswordSaltSize = 16;
        private const int PasswordHashSize = 32;
        private const string PasswordHashPrefix = "SJPH1$";
        private static readonly string EncryptionPurpose = "DataServerPersistence";
        private const string SplitStorageManifestFile = "_manifest.json";
        private const string SplitStorageTablesFolder = "tables";
        private const string SplitStorageCacheMetadataFile = "_cachemeta.json";

        /// <summary>
        /// Set this to <see langword="false"/> to keep persisted files in plain JSON.
        /// Default is <see langword="true"/> so data is not readable at a glance.
        /// </summary>
        public bool EnablePayloadEncryption { get; set; } = true;

        #endregion

        #region Events

        public event QueryExecutingEventHandler QueryExecuting;
        public delegate void QueryExecutingEventHandler(SqlSession session, string query, ref QueryResult result);

        public event AuthenticationEventHandler Authentication;
        public delegate void AuthenticationEventHandler(string username, string password, ref bool authenticated);

        /// <summary>
        /// Raised when a TDS client connects (standalone mode only).
        /// </summary>
        public event Action<SqlSession> TdsClientConnected;

        /// <summary>
        /// Raised when a TDS client disconnects (standalone mode only).
        /// </summary>
        public event Action<SqlSession> TdsClientDisconnected;

        #endregion

        #region Constructors

        /// <summary>
        /// Initialize a new MSSQL Server Emulator (standalone mode — owns the TCP listener).
        /// </summary>
        /// <param name="Port">Port to listen on (default MSSQL port is 1433)</param>
        /// <param name="Name">Server name for logging</param>
        /// <param name="loadFromDisk">Load persisted state during construction.</param>
        public DataServer(int Port = 1433, string Name = "DataServer", bool loadFromDisk = true) : base(Port, Name) {
            InitDefaults(loadFromDisk);

            // Hook into client events for standalone mode
            ClientConnected += DataServer_ClientConnected;
            ClientDisconnected += DataServer_ClientDisconnected;
        }

        /// <summary>
        /// Creates a <see cref="DataServer"/> in hosted mode (no TCP listener).
        /// <para>
        /// The <see cref="MutableTcpServer"/> owns the listener; this instance
        /// only provides the in-memory database engine, persistence, and import
        /// capabilities. No port is bound, no standalone client event hooks are
        /// installed.
        /// </para>
        /// </summary>
        /// <param name="Name">Server name for logging.</param>
        /// <param name="hosted">Must be <see langword="true"/>. Distinguishes this from the standalone constructor.</param>
        /// <param name="loadFromDisk">Load persisted state during construction.</param>
        internal DataServer(string Name, bool hosted, bool loadFromDisk = true) : base(0, Name) {
            InitDefaults(loadFromDisk);
        }

        private void InitDefaults(bool loadFromDisk) {
            RawTcpMode = true;
            SuppressConnectionTest = true;
            Users.TryAdd(Username, NormalizeStoredPassword(Password));

            // Ensure default databases exist
            Databases.TryAdd("master", new Database("master"));
            Databases.TryAdd("db", new Database("db"));

            if (loadFromDisk)
                Load();

            NormalizeAllStoredPasswords();
            ApplyDatabaseSqlAdminCredentials();
            if (Users.TryGetValue(Username, out var storedDefaultPassword))
                Password = IsPasswordHash(storedDefaultPassword) ? "" : storedDefaultPassword;

            // Sync Users dictionary into the db.Users table so every configured user is visible
            SyncUsersTable();
        }

        public bool RequiresSaPasswordSetup {
            get {
                if (Users.TryGetValue("sa", out var storedPassword))
                    return string.IsNullOrEmpty(storedPassword);
                return string.IsNullOrEmpty(Password);
            }
        }

        public void SecureStoredPasswords() {
            NormalizeAllStoredPasswords();
            SyncUsersTable();
            ScheduleSave();
        }

        public bool IsSqlAdminAccount(string username) {
            if (string.IsNullOrWhiteSpace(username))
                return false;
            return string.Equals(username, Username, StringComparison.OrdinalIgnoreCase)
                || string.Equals(username, "sa", StringComparison.OrdinalIgnoreCase);
        }

        public void SetSqlAdminAccount(string username, string password) {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("SQL admin username is required.", nameof(username));
            if (password == null)
                throw new ArgumentNullException(nameof(password));

            Username = username.Trim();
            Password = password;
            Users[Username] = HashPassword(password);
            if (!string.Equals(Username, "sa", StringComparison.OrdinalIgnoreCase)
                && Users.TryGetValue("sa", out var saPassword)
                && string.IsNullOrEmpty(saPassword)) {
                Users.TryRemove("sa", out _);
            }
            SyncUsersTable();
            ScheduleSave();
        }

        private static bool IsPasswordHash(string value) {
            return !string.IsNullOrEmpty(value) && value.StartsWith(PasswordHashPrefix, StringComparison.Ordinal);
        }

        private static string NormalizeStoredPassword(string password) {
            if (string.IsNullOrEmpty(password) || IsPasswordHash(password))
                return password ?? "";
            return HashPassword(password);
        }

        private void NormalizeAllStoredPasswords() {
            foreach (var kvp in Users.ToArray())
                Users[kvp.Key] = NormalizeStoredPassword(kvp.Value);

            foreach (var database in Databases.Values) {
                if (database != null && database.SqlAdminPassword != null)
                    database.SqlAdminPassword = NormalizeStoredPassword(database.SqlAdminPassword);
            }
        }

        private static string HashPassword(string password) {
            if (string.IsNullOrEmpty(password))
                return "";

            byte[] salt = new byte[PasswordSaltSize];
            RandomNumberGenerator.Fill(salt);
            byte[] hash = DerivePasswordHash(password, salt, PasswordHashIterations, PasswordHashSize);
            return PasswordHashPrefix + PasswordHashIterations.ToString() + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(hash);
        }

        private static bool VerifyPassword(string password, string storedPassword) {
            storedPassword = storedPassword ?? "";
            if (!IsPasswordHash(storedPassword))
                return string.Equals(password ?? "", storedPassword, StringComparison.Ordinal);

            string[] parts = storedPassword.Split('$');
            if (parts.Length != 4 || !int.TryParse(parts[1], out int iterations))
                return false;

            try {
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] expected = Convert.FromBase64String(parts[3]);
                byte[] actual = DerivePasswordHash(password ?? "", salt, iterations, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            } catch {
                return false;
            }
        }

        private static byte[] DerivePasswordHash(string password, byte[] salt, int iterations, int outputLength) {
#if NET6_0_OR_GREATER
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, outputLength);
#else
            return DerivePbkdf2(Encoding.UTF8.GetBytes(password ?? ""), salt, iterations, outputLength);
#endif
        }

        private static byte[] DerivePbkdf2(byte[] seed, byte[] salt, int iterations, int outputLength) {
#if NET6_0_OR_GREATER
            return Rfc2898DeriveBytes.Pbkdf2(seed, salt, iterations, HashAlgorithmName.SHA256, outputLength);
#else
            if (seed == null)
                throw new ArgumentNullException(nameof(seed));
            if (salt == null)
                throw new ArgumentNullException(nameof(salt));
            if (iterations <= 0)
                throw new ArgumentOutOfRangeException(nameof(iterations));
            if (outputLength < 0)
                throw new ArgumentOutOfRangeException(nameof(outputLength));
            if (outputLength == 0)
                return Array.Empty<byte>();

            byte[] result = new byte[outputLength];
            byte[] blockInput = new byte[salt.Length + 4];
            Buffer.BlockCopy(salt, 0, blockInput, 0, salt.Length);

            using var hmac = new HMACSHA256(seed);
            byte[] block = new byte[hmac.HashSize / 8];
            int offset = 0;
            uint blockIndex = 1;

            while (offset < outputLength) {
                blockInput[salt.Length] = (byte)(blockIndex >> 24);
                blockInput[salt.Length + 1] = (byte)(blockIndex >> 16);
                blockInput[salt.Length + 2] = (byte)(blockIndex >> 8);
                blockInput[salt.Length + 3] = (byte)blockIndex;

                byte[] iterationHash = hmac.ComputeHash(blockInput);
                Buffer.BlockCopy(iterationHash, 0, block, 0, block.Length);

                for (int i = 1; i < iterations; i++) {
                    iterationHash = hmac.ComputeHash(iterationHash);
                    for (int j = 0; j < block.Length; j++)
                        block[j] ^= iterationHash[j];
                }

                int bytesToCopy = Math.Min(block.Length, outputLength - offset);
                Buffer.BlockCopy(block, 0, result, offset, bytesToCopy);
                offset += bytesToCopy;
                blockIndex++;
            }

            return result;
#endif
        }

        private void ApplyDatabaseSqlAdminCredentials() {
            foreach (var database in Databases.Values) {
                if (database == null || !database.HasSqlAdminCredentials)
                    continue;
                Username = database.SqlAdminUsername.Trim();
                string storedPassword = NormalizeStoredPassword(database.SqlAdminPassword ?? "");
                Password = IsPasswordHash(storedPassword) ? "" : storedPassword;
                Users[Username] = storedPassword;
                if (!string.Equals(Username, "sa", StringComparison.OrdinalIgnoreCase)
                    && Users.TryGetValue("sa", out var saPassword)
                    && string.IsNullOrEmpty(saPassword)) {
                    Users.TryRemove("sa", out _);
                }
            }
        }

        private void SyncUsersTable() {
            if (Databases.TryGetValue("db", out var configDb)) {
                var usersTable = new Table("Users") {
                    Columns = new List<Column> {
                        new Column("Username", typeof(string), 255),
                        new Column("Password", typeof(string), 255)
                    }
                };
                // Start from existing rows if the table was already persisted
                if (configDb.Tables.TryGetValue("Users", out var existingTable)) {
                    foreach (var row in existingTable.Rows) {
                        var name = row.Length > 0 ? row[0]?.ToString() : null;
                        var password = NormalizeStoredPassword(row.Length > 1 ? row[1]?.ToString() ?? "" : "");
                        if (name != null) {
                            if (!string.Equals(Username, "sa", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(name, "sa", StringComparison.OrdinalIgnoreCase)
                                && string.IsNullOrEmpty(password)) {
                                continue;
                            }
                            if (!Users.TryGetValue(name, out var currentPassword)
                                || (string.IsNullOrEmpty(currentPassword) && !string.IsNullOrEmpty(password))) {
                                Users[name] = password;
                            }
                            usersTable.Rows.Add(new object[] { name, password });
                        }
                    }
                }
                // Add any users from the dictionary that aren't already in the table
                foreach (var kvp in Users) {
                    bool found = false;
                    for (int i = 0; i < usersTable.Rows.Count; i++) {
                        if (string.Equals(usersTable.Rows[i][0]?.ToString(), kvp.Key, StringComparison.OrdinalIgnoreCase)) {
                            usersTable.Rows[i] = new object[] { kvp.Key, kvp.Value };
                            found = true;
                            break;
                        }
                    }
                    if (!found) usersTable.Rows.Add(new object[] { kvp.Key, kvp.Value });
                }
                configDb.Tables["Users"] = usersTable;
            }

            if (Users.TryGetValue(Username, out var syncedDefaultPassword))
                Password = IsPasswordHash(syncedDefaultPassword) ? "" : syncedDefaultPassword;
        }

        #endregion

        #region Event Handlers (Standalone Mode)

        private void DataServer_ClientConnected(ConnectedEventArgs e) {
            e.Connection._Protocol = TcpProtocol.Tds;
            e.Connection.SuppressConnectionTest = true;
            var session = new SqlSession {
                ConnectionId = e.Connection.ID,
                CurrentDatabase = DefaultDatabase,
                ServerName = ServerName,
                ServerVersion = ServerVersion
            };

            Sessions.TryAdd(e.Connection.ID, session);
            TdsClientConnected?.Invoke(session);

            // Start handling TDS protocol
            Task.Run(() => TdsProtocolHandler.RunTdsLoop(this, e.Connection, session));
        }

        private void DataServer_ClientDisconnected(DisconnectedEventArgs e) {
            if (Sessions.TryRemove(e.Connection.ID, out var session))
                TdsClientDisconnected?.Invoke(session);
        }

        #endregion

        #region Internal Auth / Query Helpers

        internal bool Authenticate(string username, string password) {
            bool authenticated = false;
            if (AllowSqlAuth) {
                if (Users.TryGetValue(username, out string storedPassword)) {
                    authenticated = VerifyPassword(password, storedPassword);
                }
            }
            Authentication?.Invoke(username, password, ref authenticated);
            return authenticated;
        }

        private static readonly string[] SqlAdminIpWhitelistColumns = {
            "Id",
            "IpAddress",
            "Label",
            "Source",
            "Enabled",
            "CreatedUtc",
            "UpdatedUtc"
        };

        public void AllowSqlLoginIpAddress(params string[] clientIps) {
            if (clientIps == null)
                return;

            Table whitelist = null;
            bool tableChanged = false;
            foreach (string clientIp in clientIps) {
                string normalized = NormalizeSqlLoginIpAddress(clientIp);
                if (!string.IsNullOrWhiteSpace(normalized)) {
                    AllowedSqlLoginIpAddresses[normalized] = 1;
                    whitelist = whitelist ?? EnsureSqlAdminIpWhitelistTable();
                    tableChanged |= UpsertSqlAdminIpWhitelistRow(whitelist, normalized, "Configured SQL/Admin IP", "configuration", true);
                }
            }

            if (tableChanged)
                ScheduleSave();
        }

        public bool IsSqlLoginIpAllowed(string clientIp) {
            if (!EnforceSqlLoginIpAllowList)
                return true;

            string normalized = NormalizeSqlLoginIpAddress(clientIp);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            if (AllowLoopbackSqlLogin &&
                IPAddress.TryParse(normalized, out IPAddress address) &&
                IPAddress.IsLoopback(address))
                return true;

            return IsSqlLoginIpExplicitlyAllowed(normalized);
        }

        public bool IsSqlLoginIpExplicitlyAllowed(string clientIp) {
            string normalized = NormalizeSqlLoginIpAddress(clientIp);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            bool? tableDecision = TryGetSqlAdminIpWhitelistDecision(normalized);
            if (tableDecision.HasValue)
                return tableDecision.Value;

            return AllowedSqlLoginIpAddresses.ContainsKey(normalized);
        }

        public Table EnsureSqlAdminIpWhitelistTable() {
            string databaseName = GetSqlAdminIpWhitelistDatabaseName();
            var database = Databases.GetOrAdd(databaseName, name => new Database(name));
            var table = database.Tables.GetOrAdd(SqlAdminIpWhitelistTableName, name => new Table(name));
            if (EnsureSqlAdminIpWhitelistColumns(table))
                ScheduleSave();
            return table;
        }

        private string GetSqlAdminIpWhitelistDatabaseName() {
            if (!string.IsNullOrWhiteSpace(SqlAdminIpWhitelistDatabaseName))
                return SqlAdminIpWhitelistDatabaseName.Trim();
            if (!string.IsNullOrWhiteSpace(DefaultDatabase))
                return DefaultDatabase.Trim();
            return "SocketJack";
        }

        private static bool EnsureSqlAdminIpWhitelistColumns(Table table) {
            if (table == null)
                return false;

            bool changed = false;
            foreach (string columnName in SqlAdminIpWhitelistColumns) {
                if (!table.Columns.Any(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase))) {
                    table.Columns.Add(new Column(columnName, typeof(string), -1));
                    changed = true;
                }
            }

            int columnCount = table.Columns.Count;
            for (int i = 0; i < table.Rows.Count; i++) {
                object[] row = table.Rows[i];
                if (row != null && row.Length >= columnCount)
                    continue;
                Array.Resize(ref row, columnCount);
                table.Rows[i] = row;
                changed = true;
            }

            return changed;
        }

        private static bool UpsertSqlAdminIpWhitelistRow(Table table, string normalizedIp, string label, string source, bool enabled) {
            if (table == null || string.IsNullOrWhiteSpace(normalizedIp))
                return false;

            lock (table) {
                EnsureSqlAdminIpWhitelistColumns(table);
                int ipIndex = FindTableColumnIndex(table, "IpAddress");
                int idIndex = FindTableColumnIndex(table, "Id");
                int labelIndex = FindTableColumnIndex(table, "Label");
                int sourceIndex = FindTableColumnIndex(table, "Source");
                int enabledIndex = FindTableColumnIndex(table, "Enabled");
                int createdIndex = FindTableColumnIndex(table, "CreatedUtc");
                int updatedIndex = FindTableColumnIndex(table, "UpdatedUtc");
                int rowIndex = FindSqlAdminIpWhitelistRow(table, normalizedIp, ipIndex);
                string now = DateTime.UtcNow.ToString("O");

                if (rowIndex < 0) {
                    var row = new object[table.Columns.Count];
                    row[idIndex] = "sqlip_" + Guid.NewGuid().ToString("N");
                    row[ipIndex] = normalizedIp;
                    row[labelIndex] = label ?? "";
                    row[sourceIndex] = source ?? "";
                    row[enabledIndex] = enabled ? "true" : "false";
                    row[createdIndex] = now;
                    row[updatedIndex] = now;
                    table.Rows.Add(row);
                    return true;
                }

                object[] existing = table.Rows[rowIndex];
                if (existing.Length < table.Columns.Count)
                    Array.Resize(ref existing, table.Columns.Count);
                bool changed = false;
                changed |= SetTableValue(existing, ipIndex, normalizedIp);
                if (string.IsNullOrWhiteSpace(GetTableValue(existing, labelIndex)))
                    changed |= SetTableValue(existing, labelIndex, label ?? "");
                if (string.IsNullOrWhiteSpace(GetTableValue(existing, sourceIndex)))
                    changed |= SetTableValue(existing, sourceIndex, source ?? "");
                changed |= SetTableValue(existing, enabledIndex, enabled ? "true" : "false");
                if (string.IsNullOrWhiteSpace(GetTableValue(existing, createdIndex)))
                    changed |= SetTableValue(existing, createdIndex, now);
                if (changed)
                    SetTableValue(existing, updatedIndex, now);
                table.Rows[rowIndex] = existing;
                return changed;
            }
        }

        private bool? TryGetSqlAdminIpWhitelistDecision(string normalizedIp) {
            string databaseName = GetSqlAdminIpWhitelistDatabaseName();
            if (!Databases.TryGetValue(databaseName, out var database) || database == null)
                return null;
            if (!database.Tables.TryGetValue(SqlAdminIpWhitelistTableName, out var table) || table == null)
                return null;

            int ipIndex = FindTableColumnIndex(table, "IpAddress");
            if (ipIndex < 0)
                return null;
            int enabledIndex = FindTableColumnIndex(table, "Enabled");
            int rowIndex = FindSqlAdminIpWhitelistRow(table, normalizedIp, ipIndex);
            if (rowIndex < 0)
                return null;

            string enabled = GetTableValue(table.Rows[rowIndex], enabledIndex);
            return string.IsNullOrWhiteSpace(enabled) || !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static int FindSqlAdminIpWhitelistRow(Table table, string normalizedIp, int ipIndex) {
            if (table == null || ipIndex < 0)
                return -1;
            for (int i = 0; i < table.Rows.Count; i++) {
                string rowIp = NormalizeSqlLoginIpAddress(GetTableValue(table.Rows[i], ipIndex));
                if (string.Equals(rowIp, normalizedIp, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static int FindTableColumnIndex(Table table, string columnName) {
            if (table == null || string.IsNullOrWhiteSpace(columnName))
                return -1;
            return table.Columns.FindIndex(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetTableValue(object[] row, int index) {
            return row != null && index >= 0 && index < row.Length ? row[index]?.ToString() ?? "" : "";
        }

        private static bool SetTableValue(object[] row, int index, string value) {
            if (row == null || index < 0 || index >= row.Length)
                return false;
            value = value ?? "";
            if (string.Equals(row[index]?.ToString() ?? "", value, StringComparison.Ordinal))
                return false;
            row[index] = value;
            return true;
        }

        public static string NormalizeSqlLoginIpAddress(string clientIp) {
            if (string.IsNullOrWhiteSpace(clientIp))
                return "";

            clientIp = clientIp.Trim();
            int comma = clientIp.IndexOf(',');
            if (comma >= 0)
                clientIp = clientIp.Substring(0, comma).Trim();

            if (clientIp.StartsWith("[", StringComparison.Ordinal)) {
                int close = clientIp.IndexOf(']');
                if (close > 0)
                    clientIp = clientIp.Substring(1, close - 1);
            }

            if (IPAddress.TryParse(clientIp, out IPAddress parsed)) {
                if (parsed.IsIPv4MappedToIPv6)
                    parsed = parsed.MapToIPv4();
                return parsed.ToString();
            }

            int colon = clientIp.LastIndexOf(':');
            if (colon > 0 && clientIp.IndexOf(':') == colon) {
                string withoutPort = clientIp.Substring(0, colon);
                if (IPAddress.TryParse(withoutPort, out parsed)) {
                    if (parsed.IsIPv4MappedToIPv6)
                        parsed = parsed.MapToIPv4();
                    return parsed.ToString();
                }
            }

            return clientIp;
        }

        /// <summary>Shared synchronization boundary for embedded database mutations.</summary>
        public object SqlSyncRoot { get; } = new object();

        public IReadOnlyList<QueryResult> Execute(SqlSession session, SqlCommand command, CancellationToken cancellationToken = default) {
            if (session == null || !session.IsAuthenticated)
                throw new SqlExecutionException("Authentication is required.", "28000", 18456);
            if (command == null) throw new ArgumentNullException(nameof(command));
            cancellationToken.ThrowIfCancellationRequested();
            lock (SqlSyncRoot) {
                // Existing hooks remain supported. New hooks must set Handled for
                // successful zero-row commands; they must not inspect parameterized SQL.
                if (command.Parameters.Count == 0) {
                    var initial = new QueryResult();
                    var result = initial;
                    QueryExecuting?.Invoke(session, command.Text, ref result);
                    if (result != null && (result.Handled || !ReferenceEquals(initial, result) || result.HasResultSet || result.RowsAffected != 0))
                        return new[] { result };
                }
                return new ManagedSqlEngine(this, session, command, cancellationToken).Run();
            }
        }

        internal QueryResult ExecuteQuery(SqlSession session, string query) {
            var results = Execute(session, new SqlCommand(query));
            return results[results.Count - 1];
        }

        #endregion

        #region Persistence

        /// <summary>
        /// Saves the current server state (users, databases, tables, rows) to <see cref="DataPath"/>.
        /// </summary>
        public void Save() {
            SaveAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Asynchronously saves the current server state (users, databases, tables, rows) to <see cref="DataPath"/>.
        /// </summary>
        public async Task SaveAsync() {
            await SaveAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously saves the current server state (users, databases, tables, rows) to <see cref="DataPath"/>.
        /// </summary>
        public async Task SaveAsync(CancellationToken cancellationToken) {
            await SaveAsync(cancellationToken, onlyIfDirty: false).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes a path-bound, re-encrypted copy of the currently loaded database.
        /// Use this instead of copying encrypted persistence files to a new location.
        /// </summary>
        public async Task SaveReencryptedCopyAsync(string destinationPath, CancellationToken cancellationToken = default) {
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("A destination path is required.", nameof(destinationPath));

            bool lockTaken = false;
            string originalPath = DataPath;
            try {
                await _persistenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockTaken = true;
                DataPath = Path.GetFullPath(destinationPath);
                var snapshot = BuildSnapshot();
                await WritePersistedJsonAsync(DataPath, snapshot, cancellationToken).ConfigureAwait(false);
            } finally {
                DataPath = originalPath;
                if (lockTaken)
                    _persistenceLock.Release();
            }
        }

        /// <summary>
        /// Saves the current server state only when a mutation has marked it dirty.
        /// </summary>
        public void SaveIfDirty() {
            SaveIfDirtyAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Asynchronously saves the current server state only when a mutation has marked it dirty.
        /// </summary>
        public async Task SaveIfDirtyAsync() {
            await SaveIfDirtyAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously saves the current server state only when a mutation has marked it dirty.
        /// </summary>
        public async Task SaveIfDirtyAsync(CancellationToken cancellationToken) {
            await SaveAsync(cancellationToken, onlyIfDirty: true).ConfigureAwait(false);
        }

        private async Task SaveAsync(CancellationToken cancellationToken, bool onlyIfDirty) {
            if (string.IsNullOrWhiteSpace(DataPath))
                throw new InvalidOperationException("A DataPath is required to save database changes.");
            bool lockTaken = false;
            int saveVersion = Volatile.Read(ref _dirtyVersion);
            if (onlyIfDirty && saveVersion == Volatile.Read(ref _savedVersion))
                return;

            try {
                await _persistenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockTaken = true;
                if (_loadFailure != null)
                    throw new InvalidOperationException("Database loading failed. Reload successfully before saving to avoid overwriting existing data.", _loadFailure);

                saveVersion = Volatile.Read(ref _dirtyVersion);
                if (onlyIfDirty && saveVersion == Volatile.Read(ref _savedVersion))
                    return;

                var snapshot = BuildSnapshot();
                string snapshotHash = ComputePayloadHash(snapshot);

                if (IsSplitStoragePath())
                    await SaveSplitStorageAsync(snapshot, snapshotHash, cancellationToken).ConfigureAwait(false);
                else
                    await WritePersistedJsonAsync(DataPath, snapshot, cancellationToken).ConfigureAwait(false);

                MarkPersisted(saveVersion, snapshotHash);
                if (EnableCacheOptimizing) {
                    try { _cacheOptimizer.SaveMetadata(GetResolvedCacheMetadataPath(), CacheOptimizationMaxKeys); }
                    catch (Exception ex) { LogFormat("[{0}] Cache metadata save failed: {1}", new[] { Name, ex.Message }); }
                }
                LogFormat("[{0}] Data saved to {1} (sha256 {2})", new[] { Name, DataPath, ShortHash(snapshotHash) });
            } catch (Exception ex) {
                LogFormat("[{0}] Save failed: {1}", new[] { Name, ex.Message });
                throw;
            } finally {
                if (lockTaken)
                    _persistenceLock.Release();
            }
        }

        /// <summary>
        /// Loads server state from <see cref="DataPath"/> into memory.
        /// </summary>
        public void Load() {
            LoadAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Asynchronously loads server state from <see cref="DataPath"/> into memory.
        /// </summary>
        public async Task LoadAsync() {
            await LoadAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously loads server state from <see cref="DataPath"/> into memory.
        /// </summary>
        public async Task LoadAsync(CancellationToken cancellationToken) {
            if (string.IsNullOrWhiteSpace(DataPath)) return;
            bool lockTaken = false;
            try {
                await _persistenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockTaken = true;

                bool loaded = false;
                bool loadedFromLegacy = false;
                if (IsSplitStoragePath()) {
                    loaded = await LoadSplitStorageAsync(cancellationToken).ConfigureAwait(false);
                    if (!loaded) {
                        string legacyPath = GetLegacyStorageFallbackPath();
                        if (!string.IsNullOrWhiteSpace(legacyPath) && File.Exists(legacyPath)) {
                            loaded = await LoadLegacyFileAsync(cancellationToken, legacyPath).ConfigureAwait(false);
                            loadedFromLegacy = loaded;
                        }
                    }
                } else {
                    loaded = await LoadLegacyFileAsync(cancellationToken, DataPath).ConfigureAwait(false);
                }

                if (!loaded) {
                    _loadFailure = null;
                    return;
                }

                if (loadedFromLegacy && IsSplitStoragePath())
                    await MigrateFromLegacyToSplitAsync(cancellationToken).ConfigureAwait(false);

                MarkPersisted(Volatile.Read(ref _dirtyVersion), ComputePayloadHash(BuildSnapshot()));
                _loadFailure = null;
                try { ReloadOptimizedCache(); }
                catch (Exception ex) { LogFormat("[{0}] Cache metadata load failed: {1}", new[] { Name, ex.Message }); }
                LogFormat("[{0}] Data loaded from {1}", new[] { Name, DataPath });
            } catch (Exception ex) {
                if (lockTaken) _loadFailure = ex;
                LogFormat("[{0}] Load failed: {1}", new[] { Name, ex.Message });
                throw;
            } finally {
                if (lockTaken)
                    _persistenceLock.Release();
            }
        }

        private async Task<bool> LoadLegacyFileAsync(CancellationToken cancellationToken, string path) {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            DataServerSnapshot snapshot = await ReadPersistedJsonAsync<DataServerSnapshot>(path, cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
                return false;

            ApplySnapshotToMemory(snapshot, null);
            return true;
        }

        private async Task<bool> LoadSplitStorageAsync(CancellationToken cancellationToken) {
            string rootPath = GetSplitStorageRootPath();
            if (string.IsNullOrWhiteSpace(rootPath)) return false;
            if (!Directory.Exists(rootPath))
                return false;

            string manifestPath = GetSplitStorageManifestPath();
            if (!File.Exists(manifestPath))
                return false;

            DataServerManifest manifest = await ReadPersistedJsonAsync<DataServerManifest>(manifestPath, cancellationToken).ConfigureAwait(false);
            if (manifest == null)
                return false;

            DataServerSnapshot snapshot = new DataServerSnapshot {
                Users = manifest.Users != null
                    ? new Dictionary<string, string>(manifest.Users, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Databases = new Dictionary<string, DatabaseSnapshot>(StringComparer.OrdinalIgnoreCase)
            };

            string tablesDirectory = GetSplitStorageTablesPath();
            if (manifest.Databases != null) {
                foreach (var dbKvp in manifest.Databases) {
                    var dbSnapshot = new DatabaseSnapshot {
                        Name = dbKvp.Value?.Name,
                        Tables = new Dictionary<string, TableSnapshot>(StringComparer.OrdinalIgnoreCase)
                    };

                    if (dbKvp.Value?.Tables != null) {
                        foreach (var tableKvp in dbKvp.Value.Tables) {
                            if (string.IsNullOrWhiteSpace(tableKvp.Value) ||
                                Path.GetFileName(tableKvp.Value) != tableKvp.Value)
                                throw new InvalidDataException("Invalid database table file in manifest.");
                            string tableFile = Path.Combine(tablesDirectory, tableKvp.Value);
                            if (!File.Exists(tableFile))
                                throw new FileNotFoundException("A persisted database table is missing.", tableFile);

                            string expectedHash = null;
                            if (dbKvp.Value.TableHashes != null)
                                dbKvp.Value.TableHashes.TryGetValue(tableKvp.Key, out expectedHash);

                            TableSnapshot tableSnapshot = await ReadPersistedJsonAsync<TableSnapshot>(tableFile, cancellationToken, expectedHash).ConfigureAwait(false);
                            if (tableSnapshot == null) throw new InvalidDataException("A persisted database table is empty: " + tableFile);
                            dbSnapshot.Tables[tableKvp.Key] = tableSnapshot;
                        }
                    }

                    snapshot.Databases[dbKvp.Key] = dbSnapshot;
                }
            }

            ApplySnapshotToMemory(snapshot, manifest.Databases);
            return true;
        }

        private async Task MigrateFromLegacyToSplitAsync(CancellationToken cancellationToken) {
            string legacyPath = GetLegacyStorageFallbackPath();
            if (string.IsNullOrWhiteSpace(legacyPath) || !File.Exists(legacyPath))
                return;

            var snapshot = BuildSnapshot();
            await SaveSplitStorageAsync(snapshot, ComputePayloadHash(snapshot), cancellationToken).ConfigureAwait(false);
            try {
                File.Delete(legacyPath);
            } catch {
                // Keep legacy file if deletion fails; migration still continues
            }
        }

        private string GetLegacyStorageFallbackPath() {
            if (string.IsNullOrWhiteSpace(DataPath))
                return null;

            string fallback = Path.GetFullPath(DataPath + ".json");
            return File.Exists(fallback) ? fallback : null;
        }

        private DataServerSnapshot BuildSnapshot() {
            var snapshot = new DataServerSnapshot {
                Users = new Dictionary<string, string>(Users, StringComparer.OrdinalIgnoreCase),
                Databases = new Dictionary<string, DatabaseSnapshot>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (var dbKvp in Databases) {
                var dbSnap = new DatabaseSnapshot {
                    Name = dbKvp.Value?.Name,
                    OwnerUsername = dbKvp.Value?.OwnerUsername,
                    SqlAdminUsername = dbKvp.Value?.SqlAdminUsername,
                    SqlAdminPassword = dbKvp.Value?.SqlAdminPassword,
                    Tables = new Dictionary<string, TableSnapshot>(StringComparer.OrdinalIgnoreCase)
                };

                foreach (var tkvp in dbKvp.Value?.Tables ?? new ConcurrentDictionary<string, Table>()) {
                    var tblSnap = new TableSnapshot {
                        Name = tkvp.Value?.Name
                    };

                    if (tkvp.Value?.Columns != null) {
                        tblSnap.Columns.AddRange(tkvp.Value.Columns.Select(c => new ColumnSnapshot {
                            Name = c.Name,
                            DataTypeName = c.DataType?.FullName ?? typeof(string).FullName,
                            MaxLength = c.MaxLength
                        }));
                    }

                    if (tkvp.Value?.Rows != null) {
                        foreach (var row in tkvp.Value.Rows) {
                            if (row == null)
                                continue;
                            tblSnap.Rows.Add(row.Select(v => v?.ToString()).ToArray());
                        }
                    }

                    dbSnap.Tables[tkvp.Key] = tblSnap;
                }

                snapshot.Databases[dbKvp.Key] = dbSnap;
            }

            return snapshot;
        }

        private async Task<T> ReadPersistedJsonAsync<T>(string path, CancellationToken cancellationToken, string expectedPersistedHash = null) where T : class {
            string text = await ReadPersistedTextAsync(path, cancellationToken, expectedPersistedHash).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            return JsonSerializer.Deserialize<T>(text, _jsonOptions);
        }

        private async Task<string> ReadPersistedTextAsync(string path, CancellationToken cancellationToken, string expectedPersistedHash = null) {
            string text = await ReadVerifiedTextAsync(path, cancellationToken, expectedPersistedHash).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return "";

            if (!text.StartsWith(EncryptedPayloadHeader, StringComparison.Ordinal))
                return text;

            if (!TryDecryptPayload(text, out string decryptedText))
                return text;

            return decryptedText;
        }

        private async Task<string> WritePersistedJsonAsync<T>(string path, T payload, CancellationToken cancellationToken) {
            string text = JsonSerializer.Serialize(payload, _jsonOptions);
            if (EnablePayloadEncryption)
                text = EncryptPayload(text);

            return await WriteTextAtomicAsync(path, text, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> WriteTextAtomicAsync(string path, string text, CancellationToken cancellationToken) {
            // The checksum and payload must be committed in the same atomic replacement.
            // A separate sidecar can lag behind a successful write after a hard exit.
            text = DurablePayloadHeader + ComputeTextHash(text) + "\n" + text;
            string normalizedPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(normalizedPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string tempPath = normalizedPath + ".tmp";
            string backupPath = normalizedPath + ".bak";
            string hashPath = GetHashSidecarPath(normalizedPath);
            string backupHashPath = GetHashSidecarPath(backupPath);
            string expectedHash = ComputeTextHash(text);

            try {
                await WriteTextDurableAsync(tempPath, text, cancellationToken).ConfigureAwait(false);
                string tempHash = await ReadTextHashAsync(tempPath, cancellationToken).ConfigureAwait(false);
                if (!HashesEqual(tempHash, expectedHash))
                    throw new IOException("Temp persistence hash mismatch for " + normalizedPath + ".");

                if (File.Exists(normalizedPath)) {
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                    if (File.Exists(hashPath))
                        File.Copy(hashPath, backupHashPath, true);
                    else if (File.Exists(backupHashPath))
                        File.Delete(backupHashPath);

                    try {
                        File.Replace(tempPath, normalizedPath, backupPath, true);
                    } catch (PlatformNotSupportedException) {
                        await ReplaceFileWithBackupFallbackAsync(tempPath, normalizedPath, backupPath, cancellationToken).ConfigureAwait(false);
                    }
                } else {
                    File.Move(tempPath, normalizedPath);
                }

                string actualHash = await ReadTextHashAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
                if (!HashesEqual(actualHash, expectedHash))
                    throw new IOException("Persistence hash verification failed for " + normalizedPath + ".");

                return actualHash;
            } finally {
                try {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                } catch {
                    // A stale temp file is harmless; the verified target remains authoritative.
                }
            }
        }

        private async Task<string> ReadVerifiedTextAsync(string path, CancellationToken cancellationToken, string expectedPersistedHash = null) {
            string normalizedPath = Path.GetFullPath(path);
            if (!File.Exists(normalizedPath))
                return "";

            string text = await File.ReadAllTextAsync(normalizedPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            string actualHash = ComputeTextHash(text);
            string expectedHash = NormalizeHash(expectedPersistedHash);
            if (text.StartsWith(DurablePayloadHeader, StringComparison.Ordinal)) {
                if ((string.IsNullOrWhiteSpace(expectedHash) || HashesEqual(actualHash, expectedHash))
                    && TryReadDurablePayload(text, out string payload))
                    return payload;
                // Table hashes belong to one committed manifest; never substitute another generation.
                if (!string.IsNullOrWhiteSpace(expectedHash))
                    throw new IOException("Persisted table hash mismatch for " + normalizedPath + ".");
                string recovered = await TryReadVerifiedBackupTextAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
                if (recovered != null) return recovered;
                throw new IOException("Persisted data checksum mismatch for " + normalizedPath + ".");
            }
            if (string.IsNullOrWhiteSpace(expectedHash))
                expectedHash = await ReadHashSidecarAsync(normalizedPath, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(expectedHash) || HashesEqual(actualHash, expectedHash))
                return text;

            LogFormat("[{0}] Hash mismatch while loading {1}: expected {2}, got {3}", new[] {
                Name,
                normalizedPath,
                ShortHash(expectedHash),
                ShortHash(actualHash)
            });

            string backupText = await TryReadVerifiedBackupTextAsync(normalizedPath, cancellationToken, expectedPersistedHash).ConfigureAwait(false);
            if (backupText != null)
                return backupText;

            throw new IOException("Persisted data hash mismatch for " + normalizedPath + ".");
        }

        private static bool TryReadDurablePayload(string text, out string payload) {
            payload = null;
            int separator = text.IndexOf('\n');
            if (separator != DurablePayloadHeader.Length + 64) return false;
            string candidate = text.Substring(separator + 1);
            if (!HashesEqual(text.Substring(DurablePayloadHeader.Length, 64), ComputeTextHash(candidate))) return false;
            payload = candidate;
            return true;
        }

        private async Task<string> TryReadVerifiedBackupTextAsync(string normalizedPath, CancellationToken cancellationToken, string requiredHash = null) {
            string backupPath = normalizedPath + ".bak";
            if (!File.Exists(backupPath))
                return null;

            string backupText = await File.ReadAllTextAsync(backupPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            string backupActualHash = ComputeTextHash(backupText);
            if (!string.IsNullOrWhiteSpace(requiredHash) && !HashesEqual(backupActualHash, requiredHash)) return null;
            if (backupText.StartsWith(DurablePayloadHeader, StringComparison.Ordinal))
                return TryReadDurablePayload(backupText, out string payload) ? payload : null;
            string backupExpectedHash = await ReadHashSidecarAsync(backupPath, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(backupExpectedHash) && !HashesEqual(backupActualHash, backupExpectedHash)) {
                LogFormat("[{0}] Backup hash mismatch while loading {1}: expected {2}, got {3}", new[] {
                    Name,
                    backupPath,
                    ShortHash(backupExpectedHash),
                    ShortHash(backupActualHash)
                });
                return null;
            }

            LogFormat("[{0}] Recovered persisted data from verified backup {1}", new[] { Name, backupPath });
            return backupText;
        }

        private static async Task ReplaceFileWithBackupFallbackAsync(string tempPath, string normalizedPath, string backupPath, CancellationToken cancellationToken) {
            if (File.Exists(normalizedPath))
                File.Copy(normalizedPath, backupPath, true);

            string text = await File.ReadAllTextAsync(tempPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            await WriteTextDurableAsync(normalizedPath, text, cancellationToken).ConfigureAwait(false);
            File.Delete(tempPath);
        }

        private static async Task WriteTextDurableAsync(string path, string text, CancellationToken cancellationToken) {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough)) {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
        }

        private static async Task<string> ReadTextHashAsync(string path, CancellationToken cancellationToken) {
            string text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return ComputeTextHash(text);
        }

        private static async Task<string> ReadHashSidecarAsync(string path, CancellationToken cancellationToken) {
            string hashPath = GetHashSidecarPath(path);
            if (!File.Exists(hashPath))
                return "";

            string hash = await File.ReadAllTextAsync(hashPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return NormalizeHash(hash);
        }

        private static string GetHashSidecarPath(string path) {
            return path + ".sha256";
        }

        private static string ComputePayloadHash<T>(T payload) {
            return ComputeTextHash(JsonSerializer.Serialize(payload, _jsonOptions));
        }

        private static string ComputeTextHash(string text) {
            using (var sha = SHA256.Create()) {
                byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
                byte[] hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string NormalizeHash(string hash) {
            return (hash ?? "").Trim().Replace("-", "").ToLowerInvariant();
        }

        private static bool HashesEqual(string left, string right) {
            return string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string ShortHash(string hash) {
            string normalized = NormalizeHash(hash);
            return normalized.Length <= 16 ? normalized : normalized.Substring(0, 16);
        }

        private void MarkDirty() {
            Interlocked.Increment(ref _dirtyVersion);
        }

        private void MarkPersisted(int version, string snapshotHash) {
            Volatile.Write(ref _savedVersion, version);
            _lastPersistedHash = snapshotHash ?? "";
        }

        private async Task SaveSplitStorageAsync(DataServerSnapshot snapshot, string snapshotHash, CancellationToken cancellationToken) {
            if (snapshot == null) return;

            string rootPath = GetSplitStorageRootPath();
            if (string.IsNullOrWhiteSpace(rootPath))
                return;

            string manifestPath = GetSplitStorageManifestPath();
            string tablesPath = GetSplitStorageTablesPath();
            Directory.CreateDirectory(tablesPath);

            var manifest = new DataServerManifest {
                Version = 2,
                SavedUtc = DateTime.UtcNow.ToString("o"),
                SnapshotHash = snapshotHash,
                Users = snapshot.Users != null
                    ? new Dictionary<string, string>(snapshot.Users, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Databases = new Dictionary<string, DatabaseManifest>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (var dbKvp in Databases) {
                if (!snapshot.Databases.TryGetValue(dbKvp.Key, out var dbSnapshot))
                    continue;

                var dbManifest = new DatabaseManifest {
                    Name = dbKvp.Value?.Name ?? dbKvp.Key,
                    OwnerUsername = dbKvp.Value?.OwnerUsername,
                    SqlAdminUsername = dbKvp.Value?.SqlAdminUsername,
                    SqlAdminPassword = NormalizeStoredPassword(dbKvp.Value?.SqlAdminPassword)
                };

                foreach (var tableKvp in dbSnapshot.Tables) {
                    string fileName = GetSplitTableFileName(dbKvp.Key, tableKvp.Key, tableKvp.Value);
                    string tablePath = Path.Combine(tablesPath, fileName);

                    dbManifest.Tables[tableKvp.Key] = fileName;
                    string tableHash = await WritePersistedJsonAsync(tablePath, tableKvp.Value, cancellationToken).ConfigureAwait(false);
                    dbManifest.TableHashes[tableKvp.Key] = tableHash;
                }

                manifest.Databases[dbKvp.Key] = dbManifest;
            }

            await WritePersistedJsonAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        }

        private void ApplySnapshotToMemory(DataServerSnapshot snapshot, Dictionary<string, DatabaseManifest> manifestLookup) {
            if (snapshot.Users != null) {
                foreach (var kvp in snapshot.Users)
                    Users[kvp.Key] = NormalizeStoredPassword(kvp.Value);
            }

            if (snapshot.Databases != null) {
                foreach (var dbKvp in snapshot.Databases) {
                    var runtimeDb = Databases.GetOrAdd(dbKvp.Key, _ => new Database(dbKvp.Value?.Name ?? dbKvp.Key));
                    runtimeDb.OwnerUsername = dbKvp.Value?.OwnerUsername;
                    runtimeDb.SqlAdminUsername = dbKvp.Value?.SqlAdminUsername;
                    runtimeDb.SqlAdminPassword = NormalizeStoredPassword(dbKvp.Value?.SqlAdminPassword);
                    if (dbKvp.Value?.Tables == null)
                        continue;

                    if (manifestLookup != null
                        && manifestLookup.TryGetValue(dbKvp.Key, out var dbManifest)) {
                        runtimeDb.OwnerUsername = dbManifest.OwnerUsername;
                        runtimeDb.SqlAdminUsername = dbManifest.SqlAdminUsername;
                        runtimeDb.SqlAdminPassword = NormalizeStoredPassword(dbManifest.SqlAdminPassword);
                    }

                    foreach (var tableKvp in dbKvp.Value.Tables) {
                        var table = new Table(tableKvp.Value.Name ?? tableKvp.Key);
                        if (tableKvp.Value.Columns != null) {
                            table.Columns = tableKvp.Value.Columns.Select(c => new Column(
                                c.Name,
                                Type.GetType(c.DataTypeName) ?? typeof(string),
                                c.MaxLength
                            )).ToList();
                        }

                        if (tableKvp.Value.Rows != null) {
                            table.Rows = tableKvp.Value.Rows
                                .Select(r => r?.Select(v => (object)v).ToArray())
                                .ToList();
                        }

                        runtimeDb.Tables[tableKvp.Key] = table;
                    }
                }
            }
        }

        private string GetSplitStorageRootPath() {
            if (string.IsNullOrWhiteSpace(DataPath))
                return null;

            return Path.GetFullPath(DataPath.Trim());
        }

        private string GetSplitStorageManifestPath() {
            string rootPath = GetSplitStorageRootPath();
            if (string.IsNullOrWhiteSpace(rootPath))
                return null;
            return Path.Combine(rootPath, SplitStorageManifestFile);
        }

        private string GetSplitStorageTablesPath() {
            string rootPath = GetSplitStorageRootPath();
            return string.IsNullOrWhiteSpace(rootPath) ? null : Path.Combine(rootPath, SplitStorageTablesFolder);
        }

        private bool IsSplitStoragePath() {
            return !IsLegacyStoragePath();
        }

        private bool IsLegacyStoragePath() {
            if (string.IsNullOrWhiteSpace(DataPath))
                return true;

            if (File.Exists(DataPath))
                return true;

            string extension = Path.GetExtension(DataPath);
            return !string.IsNullOrWhiteSpace(extension);
        }

        private static string GetSplitTableFileName(string databaseName, string tableName, TableSnapshot tableSnapshot) {
            string safeDatabase = SanitizePathComponent(databaseName);
            string safeTable = SanitizePathComponent(tableName);
            string hashSeed = string.Join("::", new[] { databaseName ?? "", tableName ?? "" , tableSnapshot?.Name ?? "" });
            byte[] hash;
            using (var sha = SHA256.Create()) {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(hashSeed));
            }
            string suffix = BitConverter.ToString(hash).Replace("-", "").Substring(0, Math.Min(16, hash.Length * 2));
            return safeDatabase + "_" + safeTable + "_" + suffix + ".json";
        }

        private static string SanitizePathComponent(string value) {
            const int maxLength = 64;
            string safe = string.IsNullOrWhiteSpace(value) ? "item" : value;
            var invalid = Path.GetInvalidFileNameChars();
            var chars = safe.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            safe = new string(chars);
            if (safe.Length > maxLength)
                safe = safe.Substring(0, maxLength);
            return safe.Replace(' ', '_');
        }

        private string EncryptPayload(string plainText) {
            string normalized = plainText ?? "";
            byte[] plainBytes = Encoding.UTF8.GetBytes(normalized);
            byte[] salt = new byte[EncryptionSaltSize];
            _rng.GetBytes(salt);

            byte[] nonce = new byte[EncryptionNonceSize];
            _rng.GetBytes(nonce);

            byte[] key = DeriveEncryptionKey(salt, DataPath);
            byte[] tag = new byte[EncryptionTagSize];
            byte[] cipherBytes = new byte[plainBytes.Length];

            #pragma warning disable SYSLIB0053
            using (var aesgcm = new AesGcm(key)) {
                aesgcm.Encrypt(nonce, plainBytes, cipherBytes, tag);
            }
            #pragma warning restore SYSLIB0053

            byte[] payload = new byte[salt.Length + nonce.Length + tag.Length + cipherBytes.Length];
            int offset = 0;
            Buffer.BlockCopy(salt, 0, payload, offset, salt.Length);
            offset += salt.Length;
            Buffer.BlockCopy(nonce, 0, payload, offset, nonce.Length);
            offset += nonce.Length;
            Buffer.BlockCopy(tag, 0, payload, offset, tag.Length);
            offset += tag.Length;
            Buffer.BlockCopy(cipherBytes, 0, payload, offset, cipherBytes.Length);

            return EncryptedPayloadHeader + Convert.ToBase64String(payload);
        }

        private bool TryDecryptPayload(string payload, out string plainText) {
            plainText = null;
            if (string.IsNullOrWhiteSpace(payload))
                return false;
            if (!payload.StartsWith(EncryptedPayloadHeader, StringComparison.Ordinal))
                return false;

            string b64 = payload.Substring(EncryptedPayloadHeader.Length);
            if (string.IsNullOrWhiteSpace(b64))
                return false;

            byte[] allBytes;
            try {
                allBytes = Convert.FromBase64String(b64);
            } catch {
                return false;
            }

            if (allBytes.Length < EncryptionSaltSize + EncryptionNonceSize + EncryptionTagSize)
                return false;

            int offset = 0;
            byte[] salt = new byte[EncryptionSaltSize];
            Buffer.BlockCopy(allBytes, offset, salt, 0, EncryptionSaltSize);
            offset += EncryptionSaltSize;

            byte[] nonce = new byte[EncryptionNonceSize];
            Buffer.BlockCopy(allBytes, offset, nonce, 0, EncryptionNonceSize);
            offset += EncryptionNonceSize;

            byte[] tag = new byte[EncryptionTagSize];
            Buffer.BlockCopy(allBytes, offset, tag, 0, EncryptionTagSize);
            offset += EncryptionTagSize;

            int cipherLength = allBytes.Length - offset;
            if (cipherLength < 0)
                return false;

            byte[] cipherBytes = new byte[cipherLength];
            Buffer.BlockCopy(allBytes, offset, cipherBytes, 0, cipherLength);

            byte[] key = DeriveEncryptionKey(salt, DataPath);
            byte[] plainBytes = new byte[cipherBytes.Length];
            try {
            #pragma warning disable SYSLIB0053
            using (var aesgcm = new AesGcm(key)) {
                aesgcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
            }
            #pragma warning restore SYSLIB0053
            } catch {
                return false;
            }

            plainText = Encoding.UTF8.GetString(plainBytes);
            return true;
        }

        private byte[] DeriveEncryptionKey(byte[] salt, string purpose) {
            string context = (purpose ?? "") + "|" + EncryptionPurpose;
            byte[] contextBytes = Encoding.UTF8.GetBytes(context);
            byte[] rawSeed = new byte[_dataEncryptionSecret.Length + contextBytes.Length];
            Buffer.BlockCopy(_dataEncryptionSecret, 0, rawSeed, 0, _dataEncryptionSecret.Length);
            Buffer.BlockCopy(contextBytes, 0, rawSeed, _dataEncryptionSecret.Length, contextBytes.Length);

            return DerivePbkdf2(rawSeed, salt, EncryptionDerivationIterations, EncryptionKeySize);
        }

        private static string GetDataServerMachineSecret() {
            string explicitSecret = Environment.GetEnvironmentVariable("SOCKETJACK_DATASERVER_ENCRYPTION_SECRET");
            if (!string.IsNullOrWhiteSpace(explicitSecret))
                return explicitSecret;

            string machineName = Environment.MachineName ?? "";
            string userName = Environment.UserName ?? "";
            string userDomain = Environment.UserDomainName ?? "";
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "";
            string machineSecretSeed = string.Join("|", new[] { machineName, userName, userDomain, home });
            using (var sha = SHA256.Create()) {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(machineSecretSeed));
                return Convert.ToBase64String(hash);
            }
        }

        /// <summary>
        /// Schedules a debounced save. Multiple rapid calls within
        /// <see cref="AutoSaveDebounceMs"/> collapse into a single write.
        /// </summary>
        public void ScheduleSave() {
            MarkDirty();

            if (EnableCacheOptimizing)
                _cacheOptimizer.InvalidateAll();

            if (!AutoSave || string.IsNullOrWhiteSpace(DataPath)) return;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => _ = SaveIfDirtyAsync(), null, Math.Max(0, AutoSaveDebounceMs), Timeout.Infinite);
        }

        /// <summary>
        /// Reloads optimized cache metadata and warms hot key/value indexes.
        /// </summary>
        public void ReloadOptimizedCache() {
            if (!EnableCacheOptimizing) return;
            _cacheOptimizer.LoadMetadata(GetResolvedCacheMetadataPath());
            _cacheOptimizer.Warm(Databases, CacheOptimizationMaxKeys);
        }

        /// <summary>
        /// Clears all warmed optimized cache indexes. Metadata is kept so the
        /// cache can be warmed again after the next load or lookup.
        /// </summary>
        public void ClearOptimizedCache() {
            _cacheOptimizer.InvalidateAll();
        }

        internal bool TryGetCachedRowIndexes(
            string databaseName,
            string tableName,
            Table table,
            string whereClause,
            out List<int> rowIndexes) {

            rowIndexes = null;
            if (!EnableCacheOptimizing)
                return false;

            return _cacheOptimizer.TryGetRowIndexes(
                databaseName,
                tableName,
                table,
                whereClause,
                CacheOptimizationMaxKeys,
                out rowIndexes);
        }

        private string GetResolvedCacheMetadataPath() {
            if (!string.IsNullOrWhiteSpace(CacheMetadataPath))
                return CacheMetadataPath;

            if (IsSplitStoragePath())
                return GetSplitStorageCacheMetadataPath();

            if (string.IsNullOrWhiteSpace(DataPath))
                return null;

            return Path.ChangeExtension(DataPath, ".cachemeta.json");
        }

        private string GetSplitStorageCacheMetadataPath() {
            string rootPath = GetSplitStorageRootPath();
            if (string.IsNullOrWhiteSpace(rootPath))
                return null;
            return Path.Combine(rootPath, SplitStorageCacheMetadataFile);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                _debounceTimer?.Dispose();
                if (AutoSave)
                    SaveIfDirty();

                _rng?.Dispose();
                _persistenceLock.Dispose();
            }

            base.Dispose(disposing);
        }

        #endregion

        #region Import From MSSQL

        /// <summary>
        /// Imports schema and data from an existing MSSQL database into this
        /// DataServer's in-memory store.
        /// <para>
        /// Pass any <see cref="DbConnection"/> (e.g. <c>Microsoft.Data.SqlClient.SqlConnection</c>).
        /// The connection should already be opened or the method will open it.
        /// </para>
        /// <example>
        /// <code>
        /// // Install Microsoft.Data.SqlClient in your application project:
        /// //   dotnet add package Microsoft.Data.SqlClient
        ///
        /// using Microsoft.Data.SqlClient;
        ///
        /// var conn = new SqlConnection("Server=.;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True;");
        /// server.ImportFromMssql(conn);
        /// // or import only specific tables:
        /// server.ImportFromMssql(conn, tableFilter: new[] { "Users", "Orders" });
        /// </code>
        /// </example>
        /// </summary>
        /// <param name="connection">An open (or openable) <see cref="DbConnection"/> to the source MSSQL server.</param>
        /// <param name="databaseName">
        /// Override the target database name in the DataServer. When <see langword="null"/>,
        /// the source connection's <see cref="DbConnection.Database"/> name is used.
        /// </param>
        /// <param name="tableFilter">
        /// Optional list of table names to import. When <see langword="null"/> or empty,
        /// all user tables are imported.
        /// </param>
        /// <param name="importData">
        /// When <see langword="true"/> (default), all rows are imported.
        /// Set to <see langword="false"/> to import only schema (columns).
        /// </param>
        /// <param name="maxRowsPerTable">
        /// Maximum number of rows to import per table. 0 = unlimited.
        /// </param>
        public void ImportFromMssql(
            DbConnection connection,
            string databaseName = null,
            string[] tableFilter = null,
            bool importData = true,
            int maxRowsPerTable = 0) {

            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            if (connection.State != ConnectionState.Open)
                connection.Open();

            string dbName = databaseName ?? connection.Database ?? "Imported";
            var db = Databases.GetOrAdd(dbName, _ => new Database(dbName));

            LogFormat("[{0}] Starting import from MSSQL database '{1}'...", new[] { Name, dbName });

            // --- Discover tables ---
            var tableNames = new List<string>();
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = @"
                    SELECT TABLE_SCHEMA, TABLE_NAME
                    FROM INFORMATION_SCHEMA.TABLES
                    WHERE TABLE_TYPE = 'BASE TABLE'
                    ORDER BY TABLE_SCHEMA, TABLE_NAME";

                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        string schema = reader.GetString(0);
                        string tableName = reader.GetString(1);
                        string fullName = schema + "." + tableName;

                        if (tableFilter != null && tableFilter.Length > 0) {
                            bool match = tableFilter.Any(f =>
                                string.Equals(f, tableName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(f, fullName, StringComparison.OrdinalIgnoreCase));
                            if (!match) continue;
                        }

                        tableNames.Add(fullName);
                    }
                }
            }

            int totalTables = tableNames.Count;
            int imported = 0;

            foreach (string fullTableName in tableNames) {
                imported++;
                var table = new Table(fullTableName);

                // --- Import columns ---
                string[] parts = fullTableName.Split('.');
                string schemaName = parts[0];
                string tblName = parts.Length > 1 ? parts[1] : parts[0];

                using (var cmd = connection.CreateCommand()) {
                    cmd.CommandText = string.Format(@"
                        SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH
                        FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_SCHEMA = '{0}' AND TABLE_NAME = '{1}'
                        ORDER BY ORDINAL_POSITION",
                        schemaName.Replace("'", "''"),
                        tblName.Replace("'", "''"));

                    using (var reader = cmd.ExecuteReader()) {
                        while (reader.Read()) {
                            string colName = reader.GetString(0);
                            string dataType = reader.GetString(1);
                            int maxLen = reader.IsDBNull(2) ? -1 : Convert.ToInt32(reader.GetValue(2));
                            table.Columns.Add(new Column(colName, MapSqlType(dataType), maxLen));
                        }
                    }
                }

                // --- Import rows ---
                if (importData) {
                    using (var cmd = connection.CreateCommand()) {
                        string quotedName = "[" + schemaName + "].[" + tblName + "]";
                        cmd.CommandText = maxRowsPerTable > 0
                            ? string.Format("SELECT TOP {0} * FROM {1}", maxRowsPerTable, quotedName)
                            : string.Format("SELECT * FROM {0}", quotedName);

                        try {
                            using (var reader = cmd.ExecuteReader()) {
                                int fieldCount = reader.FieldCount;
                                while (reader.Read()) {
                                    var row = new object[fieldCount];
                                    for (int i = 0; i < fieldCount; i++) {
                                        row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                                    }
                                    table.Rows.Add(row);
                                }
                            }
                        } catch (Exception ex) {
                            LogFormat("[{0}] Warning: could not read data from {1}: {2}",
                                new[] { Name, fullTableName, ex.Message });
                        }
                    }
                }

                db.Tables[fullTableName] = table;
                LogFormat("[{0}] Imported table {1}/{2}: {3} ({4} columns, {5} rows)",
                    new[] { Name, imported.ToString(), totalTables.ToString(),
                            fullTableName, table.Columns.Count.ToString(), table.Rows.Count.ToString() });
            }

            LogFormat("[{0}] Import complete. {1} tables imported into database '{2}'.",
                new[] { Name, totalTables.ToString(), dbName });

            if (EnableCacheOptimizing)
                _cacheOptimizer.InvalidateAll();

            // Auto-save after import
            if (AutoSave) ScheduleSave();
        }

        /// <summary>
        /// Maps a SQL Server data type name to a .NET <see cref="Type"/>.
        /// </summary>
        private static Type MapSqlType(string sqlType) {
            switch ((sqlType ?? "").ToLowerInvariant()) {
                case "bit": return typeof(bool);
                case "tinyint": return typeof(byte);
                case "smallint": return typeof(short);
                case "int": return typeof(int);
                case "bigint": return typeof(long);
                case "real": return typeof(float);
                case "float": return typeof(double);
                case "decimal":
                case "numeric":
                case "money":
                case "smallmoney": return typeof(decimal);
                case "date":
                case "datetime":
                case "datetime2":
                case "smalldatetime": return typeof(DateTime);
                case "datetimeoffset": return typeof(DateTimeOffset);
                case "time": return typeof(TimeSpan);
                case "uniqueidentifier": return typeof(Guid);
                case "binary":
                case "varbinary":
                case "image":
                case "timestamp":
                case "rowversion": return typeof(byte[]);
                default: return typeof(string);
            }
        }

        #endregion
    }
}
