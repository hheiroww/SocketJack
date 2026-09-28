using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.Database {

    /// <summary>
    /// Lightweight TDS client for connecting to SQL Server or DataServer instances.
    /// Implements the TDS (Tabular Data Stream) protocol for SQL communication.
    /// </summary>
    public class DataClient : IDisposable {

        #region Constants

        // TDS Packet Types
        private const byte TDS_SQL_BATCH = 0x01;
        private const byte TDS_PRE_LOGIN = 0x12;
        private const byte TDS_TABULAR_RESULT = 0x04;
        private const byte TDS_LOGIN7 = 0x10;
        private const byte TDS_ATTENTION = 0x06;
        private const byte TDS_RPC = 0x03;

        // TDS Token Types
        private const byte TOKEN_COLMETADATA = 0x81;
        private const byte TOKEN_ROW = 0xD1;
        private const byte TOKEN_NBCROW = 0xD2;
        private const byte TOKEN_DONE = 0xFD;
        private const byte TOKEN_DONEPROC = 0xFE;
        private const byte TOKEN_DONEINPROC = 0xFF;
        private const byte TOKEN_ERROR = 0xAA;
        private const byte TOKEN_INFO = 0xAB;
        private const byte TOKEN_LOGINACK = 0xAD;
        private const byte TOKEN_ENVCHANGE = 0xE3;
        private const byte TOKEN_RETURNSTATUS = 0x79;
        private const byte TOKEN_RETURNVALUE = 0xAC;
        private const byte TOKEN_FEATUREEXTACK = 0xAE;
        private const byte TOKEN_ORDER = 0xA9;

        // Encryption Modes
        private const byte ENCRYPT_OFF = 0x00;
        private const byte ENCRYPT_ON = 0x01;
        private const byte ENCRYPT_NOT_SUP = 0x02;
        private const byte ENCRYPT_REQ = 0x03;

        // TDS Versions
        private const uint TDS_VERSION_74 = 0x74000004; // SQL Server 2012+

        #endregion

        #region Properties

        /// <summary>Server hostname or IP address.</summary>
        public string Server { get; set; } = "localhost";

        /// <summary>Server port (default 1433).</summary>
        public int Port { get; set; } = 1433;

        /// <summary>Database name to connect to.</summary>
        public string Database { get; set; } = "master";

        /// <summary>SQL authentication username.</summary>
        public string Username { get; set; } = "sa";

        /// <summary>SQL authentication password.</summary>
        public string Password { get; set; } = "";

        /// <summary>Application name sent to server.</summary>
        public string ApplicationName { get; set; } = "SocketJack.DataClient";

        /// <summary>Connection timeout in seconds.</summary>
        public int ConnectionTimeout { get; set; } = 30;

        /// <summary>Command timeout in seconds.</summary>
        public int CommandTimeout { get; set; } = 30;

        /// <summary>Packet size for TDS communication.</summary>
        public int PacketSize { get; set; } = 4096;

        /// <summary>Whether to encrypt the connection.</summary>
        public bool Encrypt { get; set; } = false;

        /// <summary>Whether to trust the server certificate without validation.</summary>
        public bool TrustServerCertificate { get; set; } = true;

        /// <summary>Current connection state.</summary>
        public ConnectionState State { get; private set; } = ConnectionState.Closed;

        /// <summary>Server version string after successful login.</summary>
        public string ServerVersion { get; private set; }

        /// <summary>Server process ID (SPID) assigned to this connection.</summary>
        public int ServerProcessId { get; private set; }

        /// <summary>Last error message from the server.</summary>
        public string LastError { get; private set; }

        /// <summary>Event raised when an info message is received from the server.</summary>
        public event EventHandler<SqlInfoEventArgs> InfoMessage;

        #endregion

        #region Private Fields

        private System.Net.Sockets.TcpClient _tcpClient;
        private System.Net.Sockets.NetworkStream _networkStream;
        private SslStream _sslStream;
        private Stream _stream;
        private byte _negotiatedEncryption;
        private byte _packetId;
        private readonly object _sendLock = new object();
        private readonly object _receiveLock = new object();
        private List<ColumnMetadata> _currentColumns;
        private bool _disposed;

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new DataClient instance with default settings.
        /// </summary>
        public DataClient() { }

        /// <summary>
        /// Creates a new DataClient instance with a connection string.
        /// </summary>
        /// <param name="connectionString">SQL Server style connection string.</param>
        public DataClient(string connectionString) {
            ParseConnectionString(connectionString);
        }

        /// <summary>
        /// Creates a new DataClient instance with explicit parameters.
        /// </summary>
        public DataClient(string server, string database, string username, string password) {
            Server = server;
            Database = database;
            Username = username;
            Password = password;
        }

        #endregion

        #region Connection Methods

        /// <summary>
        /// Opens a connection to the SQL Server.
        /// </summary>
        public void Open() {
            if (State == ConnectionState.Open)
                throw new InvalidOperationException("Connection is already open.");

            try {
                State = ConnectionState.Connecting;

                // Parse server:port if combined
                var serverParts = Server.Split(',');
                var host = serverParts[0];
                var port = serverParts.Length > 1 ? int.Parse(serverParts[1]) : Port;

                // Establish TCP connection
                _tcpClient = new System.Net.Sockets.TcpClient();
                var connectTask = _tcpClient.ConnectAsync(host, port);
                if (!connectTask.Wait(TimeSpan.FromSeconds(ConnectionTimeout))) {
                    throw new TimeoutException($"Connection to {host}:{port} timed out.");
                }
                _tcpClient.NoDelay = true;
                _networkStream = _tcpClient.GetStream();
                _stream = _networkStream;

                // TDS Pre-Login
                PerformPreLogin();

                // TLS Handshake if encryption negotiated
                if (_negotiatedEncryption != ENCRYPT_NOT_SUP) {
                    PerformTlsHandshake(host);
                }

                // Login7 packet
                PerformLogin();

                State = ConnectionState.Open;
            } catch (Exception ex) {
                State = ConnectionState.Broken;
                LastError = ex.Message;
                Close();
                throw;
            }
        }

        /// <summary>
        /// Opens a connection to the SQL Server asynchronously.
        /// </summary>
        public async Task OpenAsync(CancellationToken cancellationToken = default) {
            if (State == ConnectionState.Open)
                throw new InvalidOperationException("Connection is already open.");

            try {
                State = ConnectionState.Connecting;

                var serverParts = Server.Split(',');
                var host = serverParts[0];
                var port = serverParts.Length > 1 ? int.Parse(serverParts[1]) : Port;

                _tcpClient = new System.Net.Sockets.TcpClient();
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
                    cts.CancelAfter(TimeSpan.FromSeconds(ConnectionTimeout));
                    await _tcpClient.ConnectAsync(host, port).ConfigureAwait(false);
                }
                _tcpClient.NoDelay = true;
                _networkStream = _tcpClient.GetStream();
                _stream = _networkStream;

                PerformPreLogin();

                if (_negotiatedEncryption != ENCRYPT_NOT_SUP) {
                    await PerformTlsHandshakeAsync(host, cancellationToken).ConfigureAwait(false);
                }

                PerformLogin();

                State = ConnectionState.Open;
            } catch (Exception ex) {
                State = ConnectionState.Broken;
                LastError = ex.Message;
                Close();
                throw;
            }
        }

        /// <summary>
        /// Closes the connection and releases resources.
        /// </summary>
        public void Close() {
            State = ConnectionState.Closed;
            _sslStream?.Dispose();
            _networkStream?.Dispose();
            _tcpClient?.Dispose();
            _sslStream = null;
            _networkStream = null;
            _tcpClient = null;
            _stream = null;
        }

        #endregion

        #region Query Execution

        /// <summary>
        /// Executes a SQL query and returns the results.
        /// </summary>
        public QueryResult ExecuteQuery(string sql) {
            EnsureOpen();
            SendSqlBatch(sql);
            return ReadResponse();
        }

        /// <summary>
        /// Executes a SQL query asynchronously and returns the results.
        /// </summary>
        public async Task<QueryResult> ExecuteQueryAsync(string sql, CancellationToken cancellationToken = default) {
            EnsureOpen();
            await SendSqlBatchAsync(sql, cancellationToken).ConfigureAwait(false);
            return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes a SQL command that doesn't return results.
        /// </summary>
        public int ExecuteNonQuery(string sql) {
            var result = ExecuteQuery(sql);
            return (int)result.RowsAffected;
        }

        /// <summary>
        /// Executes a SQL command asynchronously that doesn't return results.
        /// </summary>
        public async Task<int> ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken = default) {
            var result = await ExecuteQueryAsync(sql, cancellationToken).ConfigureAwait(false);
            return (int)result.RowsAffected;
        }

        /// <summary>
        /// Executes a SQL query and returns the first column of the first row.
        /// </summary>
        public object ExecuteScalar(string sql) {
            var result = ExecuteQuery(sql);
            if (result.HasResultSet && result.Rows.Count > 0 && result.Rows[0].Length > 0)
                return result.Rows[0][0];
            return null;
        }

        /// <summary>
        /// Executes a SQL query asynchronously and returns the first column of the first row.
        /// </summary>
        public async Task<object> ExecuteScalarAsync(string sql, CancellationToken cancellationToken = default) {
            var result = await ExecuteQueryAsync(sql, cancellationToken).ConfigureAwait(false);
            if (result.HasResultSet && result.Rows.Count > 0 && result.Rows[0].Length > 0)
                return result.Rows[0][0];
            return null;
        }

        /// <summary>
        /// Creates a DataTable from query results.
        /// </summary>
        public DataTable ExecuteDataTable(string sql) {
            var result = ExecuteQuery(sql);
            return ToDataTable(result);
        }

        /// <summary>
        /// Executes a stored procedure.
        /// </summary>
        public QueryResult ExecuteStoredProcedure(string procedureName, params SqlParameter[] parameters) {
            EnsureOpen();
            SendRpcRequest(procedureName, parameters);
            return ReadResponse();
        }

        #endregion

        #region TDS Protocol Implementation

        private void PerformPreLogin() {
            // Build Pre-Login request
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    // Option tokens: VERSION(0), ENCRYPTION(1), INSTOPT(2), THREADID(3), MARS(4), TERMINATOR(0xFF)
                    // Each option: token(1) + offset(2) + length(2) = 5 bytes per option
                    // 5 options * 5 bytes + 1 terminator = 26 bytes header

                    int dataOffset = 26;

                    // VERSION (token 0x00)
                    writer.Write((byte)0x00);
                    writer.Write((byte)(dataOffset >> 8)); writer.Write((byte)(dataOffset & 0xFF));
                    writer.Write((byte)0x00); writer.Write((byte)0x06);

                    // ENCRYPTION (token 0x01)
                    writer.Write((byte)0x01);
                    writer.Write((byte)((dataOffset + 6) >> 8)); writer.Write((byte)((dataOffset + 6) & 0xFF));
                    writer.Write((byte)0x00); writer.Write((byte)0x01);

                    // INSTOPT (token 0x02)
                    writer.Write((byte)0x02);
                    writer.Write((byte)((dataOffset + 7) >> 8)); writer.Write((byte)((dataOffset + 7) & 0xFF));
                    writer.Write((byte)0x00); writer.Write((byte)0x01);

                    // THREADID (token 0x03)
                    writer.Write((byte)0x03);
                    writer.Write((byte)((dataOffset + 8) >> 8)); writer.Write((byte)((dataOffset + 8) & 0xFF));
                    writer.Write((byte)0x00); writer.Write((byte)0x04);

                    // MARS (token 0x04)
                    writer.Write((byte)0x04);
                    writer.Write((byte)((dataOffset + 12) >> 8)); writer.Write((byte)((dataOffset + 12) & 0xFF));
                    writer.Write((byte)0x00); writer.Write((byte)0x01);

                    // TERMINATOR
                    writer.Write((byte)0xFF);

                    // VERSION data (6 bytes): client version
                    writer.Write((byte)11); // Major
                    writer.Write((byte)0);  // Minor
                    writer.Write((byte)0);  // Build high
                    writer.Write((byte)0);  // Build low
                    writer.Write((byte)0);  // Sub-build high
                    writer.Write((byte)0);  // Sub-build low

                    // ENCRYPTION data (1 byte)
                    writer.Write(Encrypt ? ENCRYPT_ON : ENCRYPT_OFF);

                    // INSTOPT data (1 byte)
                    writer.Write((byte)0x00);

                    // THREADID data (4 bytes)
                    writer.Write(Thread.CurrentThread.ManagedThreadId);

                    // MARS data (1 byte)
                    writer.Write((byte)0x00);
                }

                SendTdsPacket(TDS_PRE_LOGIN, ms.ToArray());
            }

            // Read Pre-Login response
            var response = ReceiveTdsPacket();
            if (response.PacketType != TDS_TABULAR_RESULT)
                throw new InvalidOperationException("Unexpected Pre-Login response type: 0x" + response.PacketType.ToString("X2"));

            // Parse encryption negotiation from response
            _negotiatedEncryption = ParsePreLoginResponse(response.Data);
        }

        private byte ParsePreLoginResponse(byte[] data) {
            int pos = 0;
            while (pos < data.Length && data[pos] != 0xFF) {
                byte token = data[pos];
                ushort offset = (ushort)((data[pos + 1] << 8) | data[pos + 2]);
                ushort length = (ushort)((data[pos + 3] << 8) | data[pos + 4]);

                if (token == 0x01 && length >= 1 && offset < data.Length) // ENCRYPTION
                    return data[offset];

                pos += 5;
            }
            return ENCRYPT_OFF;
        }

        private void PerformTlsHandshake(string host) {
            var wrapper = new TdsWrapperStream(_networkStream);
            _sslStream = new SslStream(wrapper, true, ValidateServerCertificate);
            _sslStream.AuthenticateAsClient(host, null, SslProtocols.Tls12, false);
            wrapper.Passthrough = true;
            _stream = _sslStream;
        }

        private async Task PerformTlsHandshakeAsync(string host, CancellationToken cancellationToken) {
            var wrapper = new TdsWrapperStream(_networkStream);
            _sslStream = new SslStream(wrapper, true, ValidateServerCertificate);
            using (cancellationToken.Register(() => _tcpClient?.Dispose())) {
                await _sslStream.AuthenticateAsClientAsync(host, null, SslProtocols.Tls12, false).ConfigureAwait(false);
            }
            wrapper.Passthrough = true;
            _stream = _sslStream;
        }

        private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) {
            if (TrustServerCertificate)
                return true;
            return errors == SslPolicyErrors.None;
        }

        private void PerformLogin() {
            // Build Login7 packet per [MS-TDS] 2.2.6.4
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    var clientName = Environment.MachineName;
                    var appName = ApplicationName;
                    var serverName = Server.Split(',')[0];
                    var libraryName = "SocketJack";
                    var database = Database;
                    var username = Username;
                    var password = EncryptPassword(Password);

                    // LOGIN7 fixed portion is 94 bytes; no feature extension is advertised.
                    int fixedLength = 94;
                    int offset = fixedLength;

                    // Length placeholder (will update at end)
                    writer.Write((uint)0);

                    // TDS Version
                    writer.Write(TDS_VERSION_74);

                    // Packet Size
                    writer.Write((uint)PacketSize);

                    // Client Program Version
                    writer.Write((uint)0x00000001);

                    // Client Process ID
                    writer.Write((uint)System.Diagnostics.Process.GetCurrentProcess().Id);

                    // Connection ID
                    writer.Write((uint)0);

                    // Option Flags 1: SET_LANG_ON | USE_DB_NOTIFY | INIT_DB_FATAL
                    writer.Write((byte)0xE0);

                    // Option Flags 2: ODBC
                    writer.Write((byte)0x03);

                    // Type Flags
                    writer.Write((byte)0x00);

                    // Option Flags 3: UNKNOWN_COLLATION_HANDLING | EXTENSION (bit 4)
                    writer.Write((byte)0x00);

                    // Client Timezone
                    writer.Write((int)TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes);

                    // Client LCID
                    writer.Write((uint)0x0409);

                    // Variable portion offsets and lengths
                    // HostName
                    writer.Write((ushort)offset);
                    writer.Write((ushort)clientName.Length);
                    offset += clientName.Length * 2;

                    // UserName
                    writer.Write((ushort)offset);
                    writer.Write((ushort)username.Length);
                    offset += username.Length * 2;

                    // Password
                    writer.Write((ushort)offset);
                    writer.Write((ushort)(password.Length / 2));
                    offset += password.Length;

                    // AppName
                    writer.Write((ushort)offset);
                    writer.Write((ushort)appName.Length);
                    offset += appName.Length * 2;

                    // ServerName
                    writer.Write((ushort)offset);
                    writer.Write((ushort)serverName.Length);
                    offset += serverName.Length * 2;

                    // Extension (unused)
                    writer.Write((ushort)0);
                    writer.Write((ushort)0);

                    // CltIntName (library name)
                    writer.Write((ushort)offset);
                    writer.Write((ushort)libraryName.Length);
                    offset += libraryName.Length * 2;

                    // Language (empty)
                    writer.Write((ushort)offset);
                    writer.Write((ushort)0);

                    // Database
                    writer.Write((ushort)offset);
                    writer.Write((ushort)database.Length);
                    offset += database.Length * 2;

                    // ClientID (MAC address - 6 bytes, use zeros)
                    writer.Write(new byte[6]);

                    // SSPI (empty)
                    writer.Write((ushort)offset);
                    writer.Write((ushort)0);

                    // AttachDBFile (empty)
                    writer.Write((ushort)offset);
                    writer.Write((ushort)0);

                    // ChangePassword (empty)
                    writer.Write((ushort)offset);
                    writer.Write((ushort)0);

                    // SSPI Long (4-byte length for >64K SSPI)
                    writer.Write((uint)0);

                    // Variable data
                    writer.Write(Encoding.Unicode.GetBytes(clientName));
                    writer.Write(Encoding.Unicode.GetBytes(username));
                    writer.Write(password);
                    writer.Write(Encoding.Unicode.GetBytes(appName));
                    writer.Write(Encoding.Unicode.GetBytes(serverName));
                    writer.Write(Encoding.Unicode.GetBytes(libraryName));
                    writer.Write(Encoding.Unicode.GetBytes(database));

                    // Update length
                    var data = ms.ToArray();
                    var length = data.Length;
                    data[0] = (byte)(length & 0xFF);
                    data[1] = (byte)((length >> 8) & 0xFF);
                    data[2] = (byte)((length >> 16) & 0xFF);
                    data[3] = (byte)((length >> 24) & 0xFF);

                    SendTdsPacket(TDS_LOGIN7, data);
                }
            }

            // Read login response
            var result = ReadResponse();

            // Check for errors
            if (!string.IsNullOrEmpty(LastError))
                throw new Exception("Login failed: " + LastError);

            // ENCRYPT_OFF: switch back to plaintext after login
            if (_negotiatedEncryption == ENCRYPT_OFF) {
                _stream = _networkStream;
            }
        }

        private byte[] EncryptPassword(string password) {
            // LOGIN7 obfuscation: swap nibbles, then XOR with 0xA5.
            var bytes = Encoding.Unicode.GetBytes(password);
            for (int i = 0; i < bytes.Length; i++) {
                bytes[i] = (byte)(((bytes[i] << 4) | (bytes[i] >> 4)) ^ 0xA5);
            }
            return bytes;
        }

        private void SendSqlBatch(string sql) {
            var data = Encoding.Unicode.GetBytes(sql);
            SendTdsPacket(TDS_SQL_BATCH, data);
        }

        private async Task SendSqlBatchAsync(string sql, CancellationToken cancellationToken) {
            var data = Encoding.Unicode.GetBytes(sql);
            await SendTdsPacketAsync(TDS_SQL_BATCH, data, cancellationToken).ConfigureAwait(false);
        }

        private void SendRpcRequest(string procedureName, SqlParameter[] parameters) {
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    // ALL_HEADERS (none)
                    writer.Write((uint)0);

                    // Procedure name length and name
                    var nameBytes = Encoding.Unicode.GetBytes(procedureName);
                    writer.Write((ushort)(nameBytes.Length / 2));
                    writer.Write(nameBytes);

                    // Option flags
                    writer.Write((ushort)0);

                    // Parameters
                    if (parameters != null) {
                        foreach (var param in parameters) {
                            WriteParameter(writer, param);
                        }
                    }
                }
                SendTdsPacket(TDS_RPC, ms.ToArray());
            }
        }

        private void WriteParameter(BinaryWriter writer, SqlParameter param) {
            // Parameter name (B_VARCHAR)
            var nameBytes = Encoding.Unicode.GetBytes(param.Name ?? "");
            writer.Write((byte)(nameBytes.Length / 2));
            writer.Write(nameBytes);

            // Status flags
            byte flags = 0;
            if (param.Direction == ParameterDirection.Output || param.Direction == ParameterDirection.InputOutput)
                flags |= 0x01;
            writer.Write(flags);

            // Type info and value (simplified - NVARCHAR only for now)
            writer.Write((byte)0xE7); // NVARCHAR
            writer.Write((ushort)8000); // Max length

            // Collation
            writer.Write((byte)0x09);
            writer.Write((byte)0x04);
            writer.Write((byte)0x00);
            writer.Write((byte)0xD0);
            writer.Write((byte)0x34);

            // Value
            var valueStr = param.Value?.ToString() ?? "";
            var valueBytes = Encoding.Unicode.GetBytes(valueStr);
            writer.Write((ushort)valueBytes.Length);
            writer.Write(valueBytes);
        }

        #endregion

        #region Packet I/O

        private void SendTdsPacket(byte packetType, byte[] data) {
            lock (_sendLock) {
                using (var ms = new MemoryStream()) {
                    using (var writer = new BinaryWriter(ms)) {
                        _packetId = (byte)((_packetId % 255) + 1);

                        writer.Write(packetType);
                        writer.Write((byte)0x01); // Status: EOM
                        ushort length = (ushort)(data.Length + 8);
                        writer.Write((byte)(length >> 8));
                        writer.Write((byte)(length & 0xFF));
                        writer.Write((ushort)0); // SPID
                        writer.Write(_packetId);
                        writer.Write((byte)0); // Window
                        writer.Write(data);
                    }

                    var packet = ms.ToArray();
                    _stream.Write(packet, 0, packet.Length);
                    _stream.Flush();
                }
            }
        }

        private async Task SendTdsPacketAsync(byte packetType, byte[] data, CancellationToken cancellationToken) {
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    _packetId = (byte)((_packetId % 255) + 1);

                    writer.Write(packetType);
                    writer.Write((byte)0x01);
                    ushort length = (ushort)(data.Length + 8);
                    writer.Write((byte)(length >> 8));
                    writer.Write((byte)(length & 0xFF));
                    writer.Write((ushort)0);
                    writer.Write(_packetId);
                    writer.Write((byte)0);
                    writer.Write(data);
                }

                var packet = ms.ToArray();
                await _stream.WriteAsync(packet, 0, packet.Length, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private TdsPacket ReceiveTdsPacket() {
            lock (_receiveLock) {
                using (var message = new MemoryStream()) {
                    byte packetType = 0, status;
                    do {
                        var header = new byte[8]; ReadExact(header,0,8);
                        int length = (header[2] << 8) | header[3];
                        if (length < 8 || message.Length + length - 8 > 16 * 1024 * 1024) throw new InvalidDataException("Invalid or oversized TDS message.");
                        if (packetType != 0 && packetType != header[0]) throw new InvalidDataException("TDS packet type changed within message.");
                        packetType=header[0]; status=header[1];
                        var data=new byte[length-8]; ReadExact(data,0,data.Length); message.Write(data,0,data.Length);
                    } while ((status & 1) == 0);
                    return new TdsPacket { PacketType=packetType, Status=status, Data=message.ToArray() };
                }
            }
        }

        private void ReadExact(byte[] buffer, int offset, int count) {
            int totalRead = 0;
            while (totalRead < count) {
                int read = _stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0)
                    throw new EndOfStreamException("Connection closed by server.");
                totalRead += read;
            }
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken) {
            var buffer = new byte[count];
            int totalRead = 0;
            while (totalRead < count) {
                int read = await _stream.ReadAsync(buffer, totalRead, count - totalRead, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Connection closed by server.");
                totalRead += read;
            }
            return buffer;
        }

        #endregion

        #region Response Parsing

        private QueryResult ReadResponse() {
            var result = new QueryResult();
            _currentColumns = new List<ColumnMetadata>();
            LastError = null;

            while (true) {
                var packet = ReceiveTdsPacket();
                bool done = ParseTokenStream(packet.Data, result);
                if (done) break;
            }

            if (_currentColumns.Count > 0) {
                result.HasResultSet = true;
                result.Columns = _currentColumns.ConvertAll(c => c.Name);
            }

            if (LastError != null) throw new SqlExecutionException(LastError);
            return result;
        }

        private async Task<QueryResult> ReadResponseAsync(CancellationToken cancellationToken) {
            var result = new QueryResult();
            _currentColumns = new List<ColumnMetadata>();
            LastError = null;

            while (true) {
                var header = await ReadExactAsync(8, cancellationToken).ConfigureAwait(false);
                ushort length = (ushort)((header[2] << 8) | header[3]);
                var data = await ReadExactAsync(length - 8, cancellationToken).ConfigureAwait(false);

                bool done = ParseTokenStream(data, result);
                if (done) break;
            }

            if (_currentColumns.Count > 0) {
                result.HasResultSet = true;
                result.Columns = _currentColumns.ConvertAll(c => c.Name);
            }

            return result;
        }

        private bool ParseTokenStream(byte[] data, QueryResult result) {
            int pos = 0;
            while (pos < data.Length) {
                byte token = data[pos++];

                switch (token) {
                    case TOKEN_COLMETADATA:
                        pos = ParseColMetadata(data, pos);
                        break;

                    case TOKEN_ROW:
                        pos = ParseRow(data, pos, result);
                        break;

                    case TOKEN_NBCROW:
                        pos = ParseNbcRow(data, pos, result);
                        break;

                    case TOKEN_DONE:
                    case TOKEN_DONEPROC:
                    case TOKEN_DONEINPROC:
                        ushort status = BitConverter.ToUInt16(data, pos);
                        pos += 2;
                        pos += 2; // CurCmd
                        long rowCount = BitConverter.ToInt64(data, pos);
                        pos += 8;
                        result.RowsAffected = rowCount;
                        if ((status & 0x01) == 0) // no DONE_MORE: final completion
                            return true;
                        break;

                    case TOKEN_ERROR:
                        pos = ParseError(data, pos);
                        break;

                    case TOKEN_INFO:
                        pos = ParseInfo(data, pos);
                        break;

                    case TOKEN_LOGINACK:
                        pos = ParseLoginAck(data, pos);
                        break;

                    case TOKEN_ENVCHANGE:
                        pos = ParseEnvChange(data, pos);
                        break;

                    case TOKEN_FEATUREEXTACK:
                        pos = ParseFeatureExtAck(data, pos);
                        break;

                    case TOKEN_RETURNSTATUS:
                        pos += 4;
                        break;

                    case TOKEN_ORDER:
                        ushort orderLen = BitConverter.ToUInt16(data, pos);
                        pos += 2 + orderLen;
                        break;

                    default:
                        // Try to skip unknown token with length prefix
                        if (pos + 1 < data.Length) {
                            ushort len = BitConverter.ToUInt16(data, pos);
                            pos += 2 + len;
                        } else {
                            pos = data.Length;
                        }
                        break;
                }
            }
            return false;
        }

        private int ParseColMetadata(byte[] data, int pos) {
            ushort columnCount = BitConverter.ToUInt16(data, pos);
            pos += 2;

            if (columnCount == 0xFFFF) // No metadata
                return pos;

            _currentColumns.Clear();

            for (int i = 0; i < columnCount; i++) {
                var col = new ColumnMetadata();

                col.UserType = BitConverter.ToUInt32(data, pos);
                pos += 4;

                col.Flags = BitConverter.ToUInt16(data, pos);
                pos += 2;

                col.Type = data[pos++];

                // Parse type-specific metadata
                pos = ParseTypeInfo(data, pos, col);

                // Column name
                byte nameLen = data[pos++];
                col.Name = Encoding.Unicode.GetString(data, pos, nameLen * 2);
                pos += nameLen * 2;

                _currentColumns.Add(col);
            }

            return pos;
        }

        private int ParseTypeInfo(byte[] data, int pos, ColumnMetadata col) {
            using (var stream = new MemoryStream(data)) using (var reader = new BinaryReader(stream)) {
                // The caller has already consumed the type byte.
                stream.Position = pos - 1;
                var type = TdsValueCodec.ReadType(reader);
                col.Type = type.Token;
                col.MaxLength = type.MaxLength != 0 ? type.MaxLength : type.Size;
                col.Precision = type.Precision; col.Scale = type.Scale;
                return (int)stream.Position;
            }
        }

        private int ParseRow(byte[] data, int pos, QueryResult result) {
            var row = new object[_currentColumns.Count];

            for (int i = 0; i < _currentColumns.Count; i++) {
                var col = _currentColumns[i];
                pos = ParseColumnValue(data, pos, col, out row[i]);
            }

            result.Rows.Add(row);
            return pos;
        }

        private int ParseNbcRow(byte[] data, int pos, QueryResult result) {
            // Null bitmap compressed row
            int nullBitmapLen = (_currentColumns.Count + 7) / 8;
            var nullBitmap = new byte[nullBitmapLen];
            Array.Copy(data, pos, nullBitmap, 0, nullBitmapLen);
            pos += nullBitmapLen;

            var row = new object[_currentColumns.Count];

            for (int i = 0; i < _currentColumns.Count; i++) {
                bool isNull = (nullBitmap[i / 8] & (1 << (i % 8))) != 0;
                if (isNull) {
                    row[i] = DBNull.Value;
                } else {
                    var col = _currentColumns[i];
                    pos = ParseColumnValue(data, pos, col, out row[i]);
                }
            }

            result.Rows.Add(row);
            return pos;
        }

        private int ParseColumnValue(byte[] data, int pos, ColumnMetadata col, out object value) {
            using (var stream = new MemoryStream(data)) using (var reader = new BinaryReader(stream)) {
                stream.Position = pos;
                value = TdsValueCodec.ReadValue(reader, new TdsValueCodec.ValueType { Token=col.Type, Size=(byte)col.MaxLength, MaxLength=col.MaxLength, Precision=col.Precision, Scale=col.Scale });
                return (int)stream.Position;
            }
        }

        private int ParseError(byte[] data, int pos) {
            ushort length = BitConverter.ToUInt16(data, pos);
            pos += 2;
            int endPos = pos + length - 2;

            int number = BitConverter.ToInt32(data, pos);
            pos += 4;
            byte state = data[pos++];
            byte severity = data[pos++];

            ushort msgLen = BitConverter.ToUInt16(data, pos);
            pos += 2;
            string message = Encoding.Unicode.GetString(data, pos, msgLen * 2);
            pos += msgLen * 2;

            LastError = $"Error {number}: {message}";
            return endPos + 2;
        }

        private int ParseInfo(byte[] data, int pos) {
            ushort length = BitConverter.ToUInt16(data, pos);
            pos += 2;
            int endPos = pos + length - 2;

            int number = BitConverter.ToInt32(data, pos);
            pos += 4;
            byte state = data[pos++];
            byte severity = data[pos++];

            ushort msgLen = BitConverter.ToUInt16(data, pos);
            pos += 2;
            string message = Encoding.Unicode.GetString(data, pos, msgLen * 2);

            InfoMessage?.Invoke(this, new SqlInfoEventArgs { Number = number, Message = message, Severity = severity });
            return endPos + 2;
        }

        private int ParseLoginAck(byte[] data, int pos) {
            ushort length = BitConverter.ToUInt16(data, pos);
            pos += 2;
            int endPos = pos + length - 2;

            pos++; // Interface
            pos += 4; // TDS Version

            byte progNameLen = data[pos++];
            string progName = Encoding.Unicode.GetString(data, pos, progNameLen * 2);
            pos += progNameLen * 2;

            byte major = data[pos++];
            byte minor = data[pos++];
            ushort build = (ushort)((data[pos] << 8) | data[pos + 1]);
            pos += 2;

            ServerVersion = $"{major}.{minor}.{build}";
            return endPos + 2;
        }

        private int ParseEnvChange(byte[] data, int pos) {
            ushort length = BitConverter.ToUInt16(data, pos);
            pos += 2;
            int endPos = pos + length - 2;

            byte type = data[pos++];
            // Skip the rest - just environment changes like database context

            return endPos + 2;
        }

        private int ParseFeatureExtAck(byte[] data, int pos) {
            while (pos < data.Length && data[pos] != 0xFF) {
                byte featureId = data[pos++];
                uint featureDataLen = BitConverter.ToUInt32(data, pos);
                pos += 4;
                pos += (int)featureDataLen;
            }
            if (pos < data.Length && data[pos] == 0xFF)
                pos++;
            return pos;
        }

        #endregion

        #region Utility Methods

        private void EnsureOpen() {
            if (State != ConnectionState.Open)
                throw new InvalidOperationException("Connection is not open.");
        }

        private void ParseConnectionString(string connectionString) {
            var parts = connectionString.Split(';');
            foreach (var part in parts) {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;

                var key = kv[0].Trim().ToLowerInvariant();
                var value = kv[1].Trim();

                switch (key) {
                    case "server":
                    case "data source":
                        Server = value;
                        break;
                    case "database":
                    case "initial catalog":
                        Database = value;
                        break;
                    case "user id":
                    case "uid":
                        Username = value;
                        break;
                    case "password":
                    case "pwd":
                        Password = value;
                        break;
                    case "application name":
                        ApplicationName = value;
                        break;
                    case "connect timeout":
                    case "connection timeout":
                        ConnectionTimeout = int.Parse(value);
                        break;
                    case "encrypt":
                        Encrypt = value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                  value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                                  value.Equals("mandatory", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "trustservercertificate":
                        TrustServerCertificate = value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                                  value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "packet size":
                        PacketSize = int.Parse(value);
                        break;
                }
            }
        }

        private DataTable ToDataTable(QueryResult result) {
            var table = new DataTable();

            if (!result.HasResultSet)
                return table;

            foreach (var col in result.Columns)
                table.Columns.Add(col);

            foreach (var row in result.Rows) {
                var dataRow = table.NewRow();
                for (int i = 0; i < row.Length && i < table.Columns.Count; i++)
                    dataRow[i] = row[i] ?? DBNull.Value;
                table.Rows.Add(dataRow);
            }

            return table;
        }

        public void Dispose() {
            if (!_disposed) {
                Close();
                _disposed = true;
            }
        }

        #endregion

        #region Nested Types

        private class TdsPacket {
            public byte PacketType { get; set; }
            public byte Status { get; set; }
            public byte[] Data { get; set; }
        }

        private class ColumnMetadata {
            public uint UserType { get; set; }
            public ushort Flags { get; set; }
            public byte Type { get; set; }
            public ushort MaxLength { get; set; }
            public byte Precision { get; set; }
            public byte Scale { get; set; }
            public byte[] Collation { get; set; }
            public string Name { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Event arguments for SQL info messages.
    /// </summary>
    public class SqlInfoEventArgs : EventArgs {
        public int Number { get; set; }
        public string Message { get; set; }
        public byte Severity { get; set; }
    }

    /// <summary>
    /// Represents a SQL parameter for stored procedure calls.
    /// </summary>
    public class SqlParameter {
        public string Name { get; set; }
        public object Value { get; set; }
        public ParameterDirection Direction { get; set; } = ParameterDirection.Input;

        public SqlParameter() { }
        public SqlParameter(string name, object value) {
            Name = name;
            Value = value;
        }
    }
}
