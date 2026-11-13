using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using SocketJack.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {

    public enum FtpSecurityMode {
        None,
        ExplicitTls,
        ImplicitTls
    }

    public enum FtpDataConnectionMode {
        Passive,
        ExtendedPassive,
        Active,
        ExtendedActive
    }

    public enum FtpTransferType {
        Ascii,
        Binary
    }

    public sealed class FtpProgress {
        public string RemotePath { get; set; }
        public long BytesTransferred { get; set; }
        public long? TotalBytes { get; set; }
    }

    public sealed class FtpListItem {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTimeOffset? Modified { get; set; }
        public string Raw { get; set; }
    }

    public sealed class FtpReply {
        public int Code { get; set; }
        public string Message { get; set; }
        public List<string> Lines { get; } = new List<string>();

        public bool PositivePreliminary => Code >= 100 && Code < 200;
        public bool PositiveCompletion => Code >= 200 && Code < 300;
        public bool PositiveIntermediate => Code >= 300 && Code < 400;

        public override string ToString() {
            return Code.ToString(CultureInfo.InvariantCulture) + " " + Message;
        }
    }

    public sealed class FtpClient : IDisposable {
        private System.Net.Sockets.TcpClient _tcp;
        private Stream _controlStream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private string _host;
        private int _port;
        private FtpSecurityMode _securityMode;
        private bool _disposed;
        private readonly Encoding _encoding;

        public bool IsConnected => _tcp != null && _tcp.Connected;
        public FtpDataConnectionMode DataConnectionMode { get; set; } = FtpDataConnectionMode.Passive;
        public FtpTransferType TransferType { get; private set; } = FtpTransferType.Binary;
        public bool ValidateServerCertificate { get; set; } = true;
        public SslProtocols SslProtocols { get; set; } = SslProtocols.Tls12;

        public FtpClient() : this(new UTF8Encoding(false)) { }

        public FtpClient(Encoding encoding) {
            _encoding = encoding ?? new UTF8Encoding(false);
        }

        public async Task ConnectAsync(string host, int port = 21, FtpSecurityMode securityMode = FtpSecurityMode.None, CancellationToken cancellationToken = default) {
            ThrowIfDisposed();
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _securityMode = securityMode;

            _tcp = new System.Net.Sockets.TcpClient();
            await _tcp.ConnectAsync(host, port).ConfigureAwait(false);
            _controlStream = _tcp.GetStream();
            if (securityMode == FtpSecurityMode.ImplicitTls)
                await UpgradeControlToTlsAsync(cancellationToken).ConfigureAwait(false);
            CreateTextPipes();
            await ExpectAsync(await ReadReplyAsync(cancellationToken).ConfigureAwait(false), 220).ConfigureAwait(false);
        }

        public async Task LoginAsync(string userName = "anonymous", string password = "anonymous@", string account = null, CancellationToken cancellationToken = default) {
            if (_securityMode == FtpSecurityMode.ExplicitTls) {
                var auth = await SendCommandAsync("AUTH TLS", cancellationToken).ConfigureAwait(false);
                if (!auth.PositiveCompletion)
                    throw new InvalidOperationException("FTP server rejected AUTH TLS: " + auth);
                await UpgradeControlToTlsAsync(cancellationToken).ConfigureAwait(false);
                CreateTextPipes();
                await SendCommandAsync("PBSZ 0", cancellationToken).ConfigureAwait(false);
                await SendCommandAsync("PROT P", cancellationToken).ConfigureAwait(false);
            }

            var user = await SendCommandAsync("USER " + userName, cancellationToken).ConfigureAwait(false);
            if (user.PositiveIntermediate) {
                var pass = await SendCommandAsync("PASS " + (password ?? string.Empty), cancellationToken).ConfigureAwait(false);
                if (pass.PositiveIntermediate && account != null)
                    await ExpectAsync(await SendCommandAsync("ACCT " + account, cancellationToken).ConfigureAwait(false), 230).ConfigureAwait(false);
                else if (!pass.PositiveCompletion)
                    throw new InvalidOperationException("FTP login failed: " + pass);
            } else if (!user.PositiveCompletion) {
                throw new InvalidOperationException("FTP login failed: " + user);
            }

            await SetTransferTypeAsync(FtpTransferType.Binary, cancellationToken).ConfigureAwait(false);
        }

        public async Task<FtpReply> SendCommandAsync(string command, CancellationToken cancellationToken = default) {
            ThrowIfDisposed();
            await _writer.WriteLineAsync(command).ConfigureAwait(false);
            return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<FtpReply> QuoteAsync(string command, CancellationToken cancellationToken = default) {
            return SendCommandAsync(command, cancellationToken);
        }

        public async Task SetTransferTypeAsync(FtpTransferType type, CancellationToken cancellationToken = default) {
            await ExpectAsync(await SendCommandAsync(type == FtpTransferType.Binary ? "TYPE I" : "TYPE A", cancellationToken).ConfigureAwait(false), 200).ConfigureAwait(false);
            TransferType = type;
        }

        public async Task<string> PrintWorkingDirectoryAsync(CancellationToken cancellationToken = default) {
            var reply = await ExpectAsync(await SendCommandAsync("PWD", cancellationToken).ConfigureAwait(false), 257).ConfigureAwait(false);
            var msg = reply.Message ?? string.Empty;
            int first = msg.IndexOf('"');
            int second = first >= 0 ? msg.IndexOf('"', first + 1) : -1;
            return first >= 0 && second > first ? msg.Substring(first + 1, second - first - 1).Replace("\"\"", "\"") : msg;
        }

        public Task ChangeDirectoryAsync(string path, CancellationToken cancellationToken = default) {
            return ExpectCompletionAsync("CWD " + path, cancellationToken);
        }

        public Task ChangeToParentDirectoryAsync(CancellationToken cancellationToken = default) {
            return ExpectCompletionAsync("CDUP", cancellationToken);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) {
            return ExpectCompletionAsync("MKD " + path, cancellationToken);
        }

        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default) {
            return ExpectCompletionAsync("RMD " + path, cancellationToken);
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default) {
            return ExpectCompletionAsync("DELE " + path, cancellationToken);
        }

        public async Task RenameAsync(string from, string to, CancellationToken cancellationToken = default) {
            await ExpectAsync(await SendCommandAsync("RNFR " + from, cancellationToken).ConfigureAwait(false), 350).ConfigureAwait(false);
            await ExpectCompletionAsync("RNTO " + to, cancellationToken).ConfigureAwait(false);
        }

        public async Task<long> GetFileSizeAsync(string path, CancellationToken cancellationToken = default) {
            var reply = await ExpectAsync(await SendCommandAsync("SIZE " + path, cancellationToken).ConfigureAwait(false), 213).ConfigureAwait(false);
            return long.Parse(reply.Message.Trim(), CultureInfo.InvariantCulture);
        }

        public async Task<DateTimeOffset> GetModifiedTimeAsync(string path, CancellationToken cancellationToken = default) {
            var reply = await ExpectAsync(await SendCommandAsync("MDTM " + path, cancellationToken).ConfigureAwait(false), 213).ConfigureAwait(false);
            return ParseFtpTimestamp(reply.Message.Trim());
        }

        public Task SetModifiedTimeAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default) {
            return ExpectCompletionAsync("MFMT " + modified.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " " + path, cancellationToken);
        }

        public Task SetPermissionsAsync(string path, string mode, CancellationToken cancellationToken = default) {
            return ExpectCompletionAsync("SITE CHMOD " + mode + " " + path, cancellationToken);
        }

        public async Task<IReadOnlyList<FtpListItem>> ListDirectoryAsync(string path = null, bool machineReadable = true, CancellationToken cancellationToken = default) {
            string command = machineReadable ? "MLSD" : "LIST";
            if (!string.IsNullOrWhiteSpace(path))
                command += " " + path;
            var text = await ReadTextDataAsync(command, cancellationToken).ConfigureAwait(false);
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Select(line => machineReadable ? ParseMlsd(line, path) : ParseList(line, path)).ToArray();
        }

        public async Task<IReadOnlyList<string>> NameListAsync(string path = null, CancellationToken cancellationToken = default) {
            var text = await ReadTextDataAsync(string.IsNullOrWhiteSpace(path) ? "NLST" : "NLST " + path, cancellationToken).ConfigureAwait(false);
            return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        }

        public async Task<string> ReadTextDataAsync(string command, CancellationToken cancellationToken = default) {
            using (var ms = new MemoryStream()) {
                await DownloadToStreamAsync(command, ms, cancellationToken).ConfigureAwait(false);
                return _encoding.GetString(ms.ToArray());
            }
        }

        public async Task DownloadFileAsync(string remotePath, string localPath, long restartOffset = 0, IProgress<FtpProgress> progress = null, CancellationToken cancellationToken = default) {
            using (var output = new FileStream(localPath, restartOffset > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.Read)) {
                if (restartOffset > 0)
                    output.Position = restartOffset;
                await DownloadFileAsync(remotePath, output, restartOffset, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task DownloadFileAsync(string remotePath, Stream destination, long restartOffset = 0, IProgress<FtpProgress> progress = null, CancellationToken cancellationToken = default) {
            return DownloadToStreamAsync("RETR " + remotePath, destination, cancellationToken, remotePath, restartOffset, progress);
        }

        public async Task UploadFileAsync(string localPath, string remotePath, bool append = false, long restartOffset = 0, IProgress<FtpProgress> progress = null, CancellationToken cancellationToken = default) {
            using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (restartOffset > 0)
                    input.Position = restartOffset;
                await UploadFileAsync(input, remotePath, append, restartOffset, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task UploadFileAsync(Stream source, string remotePath, bool append = false, long restartOffset = 0, IProgress<FtpProgress> progress = null, CancellationToken cancellationToken = default) {
            return UploadFromStreamAsync(append ? "APPE " + remotePath : "STOR " + remotePath, source, remotePath, restartOffset, progress, cancellationToken);
        }

        public async Task<string> UploadUniqueAsync(Stream source, string remoteDirectory = null, IProgress<FtpProgress> progress = null, CancellationToken cancellationToken = default) {
            string command = string.IsNullOrWhiteSpace(remoteDirectory) ? "STOU" : "STOU " + remoteDirectory;
            await UploadFromStreamAsync(command, source, remoteDirectory, 0, progress, cancellationToken).ConfigureAwait(false);
            return null;
        }

        public Task NoOpAsync(CancellationToken cancellationToken = default) {
            return ExpectCompletionAsync("NOOP", cancellationToken);
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default) {
            if (IsConnected) {
                try { await SendCommandAsync("QUIT", cancellationToken).ConfigureAwait(false); } catch { }
            }
            Dispose();
        }

        private async Task DownloadToStreamAsync(string command, Stream destination, CancellationToken cancellationToken, string remotePath = null, long restartOffset = 0, IProgress<FtpProgress> progress = null) {
            if (restartOffset > 0)
                await ExpectAsync(await SendCommandAsync("REST " + restartOffset.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false), 350).ConfigureAwait(false);
            using (var data = await OpenDataStreamAsync(command, cancellationToken).ConfigureAwait(false)) {
                await CopyWithProgressAsync(data, destination, remotePath, null, restartOffset, progress, cancellationToken).ConfigureAwait(false);
            }
            await ExpectTransferCompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task UploadFromStreamAsync(string command, Stream source, string remotePath, long restartOffset, IProgress<FtpProgress> progress, CancellationToken cancellationToken) {
            if (restartOffset > 0)
                await ExpectAsync(await SendCommandAsync("REST " + restartOffset.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false), 350).ConfigureAwait(false);
            using (var data = await OpenDataStreamAsync(command, cancellationToken).ConfigureAwait(false)) {
                await CopyWithProgressAsync(source, data, remotePath, source.CanSeek ? (long?)(source.Length - source.Position) : null, restartOffset, progress, cancellationToken).ConfigureAwait(false);
            }
            await ExpectTransferCompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<Stream> OpenDataStreamAsync(string command, CancellationToken cancellationToken) {
            switch (DataConnectionMode) {
                case FtpDataConnectionMode.Active:
                case FtpDataConnectionMode.ExtendedActive:
                    return await OpenActiveDataStreamAsync(command, cancellationToken).ConfigureAwait(false);
                case FtpDataConnectionMode.ExtendedPassive:
                    return await OpenPassiveDataStreamAsync(command, true, cancellationToken).ConfigureAwait(false);
                default:
                    return await OpenPassiveDataStreamAsync(command, false, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<Stream> OpenPassiveDataStreamAsync(string command, bool extended, CancellationToken cancellationToken) {
            var reply = await SendCommandAsync(extended ? "EPSV" : "PASV", cancellationToken).ConfigureAwait(false);
            if (!reply.PositiveCompletion && extended)
                reply = await SendCommandAsync("PASV", cancellationToken).ConfigureAwait(false);
            if (!reply.PositiveCompletion)
                throw new InvalidOperationException("FTP passive mode failed: " + reply);

            var endpoint = extended ? ParseEpsv(reply.Message) : ParsePasv(reply.Message);
            var dataTcp = new System.Net.Sockets.TcpClient();
            await dataTcp.ConnectAsync(endpoint.Address, endpoint.Port).ConfigureAwait(false);
            var preliminary = await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
            if (!preliminary.PositivePreliminary && !preliminary.PositiveCompletion)
                throw new InvalidOperationException("FTP transfer failed: " + preliminary);
            return dataTcp.GetStream();
        }

        private async Task<Stream> OpenActiveDataStreamAsync(string command, CancellationToken cancellationToken) {
            var local = (IPEndPoint)_tcp.Client.LocalEndPoint;
            var listener = new TcpListener(local.Address, 0);
            listener.Start(1);
            try {
                var ep = (IPEndPoint)listener.LocalEndpoint;
                string activeCommand;
                if (DataConnectionMode == FtpDataConnectionMode.ExtendedActive) {
                    activeCommand = "EPRT |1|" + ep.Address + "|" + ep.Port + "|";
                } else {
                    var bytes = ep.Address.GetAddressBytes();
                    activeCommand = "PORT " + string.Join(",", bytes.Select(b => b.ToString(CultureInfo.InvariantCulture)).Concat(new[] { (ep.Port / 256).ToString(CultureInfo.InvariantCulture), (ep.Port % 256).ToString(CultureInfo.InvariantCulture) }));
                }
                await ExpectCompletionAsync(activeCommand, cancellationToken).ConfigureAwait(false);
                var preliminary = await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
                if (!preliminary.PositivePreliminary && !preliminary.PositiveCompletion)
                    throw new InvalidOperationException("FTP transfer failed: " + preliminary);
                var accepted = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                return accepted.GetStream();
            } finally {
                listener.Stop();
            }
        }

        private async Task<FtpReply> ReadReplyAsync(CancellationToken cancellationToken) {
            string first = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (first == null)
                throw new IOException("FTP control connection closed.");
            if (first.Length < 3 || !int.TryParse(first.Substring(0, 3), out int code))
                throw new InvalidDataException("Invalid FTP reply: " + first);

            var reply = new FtpReply { Code = code, Message = first.Length > 4 ? first.Substring(4) : string.Empty };
            reply.Lines.Add(first);
            if (first.Length > 3 && first[3] == '-') {
                string endPrefix = code.ToString(CultureInfo.InvariantCulture) + " ";
                string line;
                do {
                    line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line == null)
                        throw new IOException("FTP control connection closed during multiline reply.");
                    reply.Lines.Add(line);
                } while (!line.StartsWith(endPrefix, StringComparison.Ordinal));
                reply.Message = reply.Lines.Last().Length > 4 ? reply.Lines.Last().Substring(4) : string.Empty;
            }
            return reply;
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return await _reader.ReadLineAsync().ConfigureAwait(false);
        }

        private async Task ExpectCompletionAsync(string command, CancellationToken cancellationToken) {
            var reply = await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
            if (!reply.PositiveCompletion)
                throw new InvalidOperationException("FTP command failed: " + reply);
        }

        private static Task<FtpReply> ExpectAsync(FtpReply reply, params int[] expectedCodes) {
            if (!expectedCodes.Contains(reply.Code))
                throw new InvalidOperationException("Unexpected FTP reply: " + reply);
            return Task.FromResult(reply);
        }

        private async Task ExpectTransferCompleteAsync(CancellationToken cancellationToken) {
            var reply = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
            if (!reply.PositiveCompletion)
                throw new InvalidOperationException("FTP transfer did not complete: " + reply);
        }

        private async Task UpgradeControlToTlsAsync(CancellationToken cancellationToken) {
            var ssl = new SslStream(_controlStream, false, (sender, cert, chain, errors) => !ValidateServerCertificate || errors == SslPolicyErrors.None);
            await ssl.AuthenticateAsClientAsync(_host).ConfigureAwait(false);
            _controlStream = ssl;
        }

        private void CreateTextPipes() {
            _reader = new StreamReader(_controlStream, _encoding, false, 4096, true);
            _writer = new StreamWriter(_controlStream, _encoding, 4096, true) { NewLine = "\r\n", AutoFlush = true };
        }

        private static async Task CopyWithProgressAsync(Stream source, Stream destination, string remotePath, long? totalBytes, long initialBytes, IProgress<FtpProgress> progress, CancellationToken cancellationToken) {
            byte[] buffer = new byte[64 * 1024];
            long transferred = initialBytes;
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0) {
                await destination.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                transferred += read;
                progress?.Report(new FtpProgress { RemotePath = remotePath, BytesTransferred = transferred, TotalBytes = totalBytes });
            }
        }

        private IPEndPoint ParsePasv(string message) {
            int start = message.IndexOf('(');
            int end = message.IndexOf(')', start + 1);
            string[] parts = message.Substring(start + 1, end - start - 1).Split(',');
            var address = IPAddress.Parse(string.Join(".", parts.Take(4)));
            int port = int.Parse(parts[4], CultureInfo.InvariantCulture) * 256 + int.Parse(parts[5], CultureInfo.InvariantCulture);
            return new IPEndPoint(address, port);
        }

        private IPEndPoint ParseEpsv(string message) {
            int start = message.IndexOf('(');
            int end = message.IndexOf(')', start + 1);
            string token = message.Substring(start + 1, end - start - 1);
            char delimiter = token[0];
            string[] parts = token.Split(delimiter);
            int port = int.Parse(parts[3], CultureInfo.InvariantCulture);
            return new IPEndPoint(((IPEndPoint)_tcp.Client.RemoteEndPoint).Address, port);
        }

        private static FtpListItem ParseMlsd(string line, string basePath) {
            int split = line.IndexOf(' ');
            var facts = split >= 0 ? line.Substring(0, split) : string.Empty;
            var name = split >= 0 ? line.Substring(split + 1).Trim() : line.Trim();
            var item = new FtpListItem { Name = name, FullPath = CombineRemote(basePath, name), Raw = line };
            foreach (var fact in facts.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                int eq = fact.IndexOf('=');
                if (eq <= 0) continue;
                string key = fact.Substring(0, eq).ToLowerInvariant();
                string value = fact.Substring(eq + 1);
                if (key == "type") item.IsDirectory = value.Equals("dir", StringComparison.OrdinalIgnoreCase) || value.Equals("cdir", StringComparison.OrdinalIgnoreCase) || value.Equals("pdir", StringComparison.OrdinalIgnoreCase);
                if (key == "size" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)) item.Size = size;
                if (key == "modify") item.Modified = ParseFtpTimestamp(value);
            }
            return item;
        }

        private static FtpListItem ParseList(string line, string basePath) {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string name = parts.Length >= 9 ? string.Join(" ", parts.Skip(8)) : line;
            long size = 0;
            if (parts.Length > 4)
                long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            return new FtpListItem { Name = name, FullPath = CombineRemote(basePath, name), IsDirectory = line.StartsWith("d", StringComparison.Ordinal), Size = size, Raw = line };
        }

        private static DateTimeOffset ParseFtpTimestamp(string value) {
            if (DateTimeOffset.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
                return result;
            if (DateTimeOffset.TryParseExact(value, "yyyyMMddHHmmss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
                return result;
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
        }

        private static string CombineRemote(string basePath, string name) {
            if (string.IsNullOrWhiteSpace(basePath)) return name;
            return basePath.TrimEnd('/') + "/" + name;
        }

        private void ThrowIfDisposed() {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FtpClient));
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _controlStream?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
        }
    }

    public sealed class FtpUser {
        public string UserName { get; set; }
        public string Password { get; set; }
        public string RootPath { get; set; }
        public bool CanRead { get; set; } = true;
        public bool CanWrite { get; set; } = true;
        public bool CanDelete { get; set; } = true;
        public bool CanCreateDirectory { get; set; } = true;
        public bool IsAnonymous { get; set; }
    }

    public interface IFtpAuthenticator {
        Task<FtpUser> AuthenticateAsync(string userName, string password, CancellationToken cancellationToken);
    }

    public sealed class FtpUserStore : IFtpAuthenticator {
        private readonly ConcurrentDictionary<string, FtpUser> _users = new ConcurrentDictionary<string, FtpUser>(StringComparer.OrdinalIgnoreCase);

        public void Add(FtpUser user) {
            if (user == null) throw new ArgumentNullException(nameof(user));
            _users[user.UserName] = user;
        }

        public Task<FtpUser> AuthenticateAsync(string userName, string password, CancellationToken cancellationToken) {
            if (_users.TryGetValue(userName ?? string.Empty, out var user) && (user.IsAnonymous || user.Password == password))
                return Task.FromResult(user);
            return Task.FromResult<FtpUser>(null);
        }
    }

    public sealed class FtpServerOptions {
        public string RootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFtpAuthenticator Authenticator { get; set; }
        public bool AllowAnonymous { get; set; } = true;
        public bool RequireTls { get; set; }
        public X509Certificate ServerCertificate { get; set; }
        public IPAddress PublicAddress { get; set; }
        public int PassivePortStart { get; set; } = 50000;
        public int PassivePortEnd { get; set; } = 50100;
        public Encoding Encoding { get; set; } = new UTF8Encoding(false);
        public string ServerName { get; set; } = "SocketJack FTP Server";
    }

    public sealed class FtpServer : TcpServer {
        private readonly FtpServerOptions _ftpOptions;
        private readonly ConcurrentDictionary<Guid, FtpSession> _sessions = new ConcurrentDictionary<Guid, FtpSession>();
        private int _nextPassivePort;

        public FtpServer(int port, FtpServerOptions options = null, string name = "FtpServer") : base(port, name) {
            _ftpOptions = options ?? new FtpServerOptions();
            RawTcpMode = true;
            SuppressConnectionTest = true;
            _nextPassivePort = _ftpOptions.PassivePortStart;
            ClientConnected += OnClientConnected;
            ClientDisconnected += OnClientDisconnected;
        }

        public FtpServer(NetworkOptions options, int port, FtpServerOptions ftpOptions = null, string name = "FtpServer") : base(options, port, name) {
            _ftpOptions = ftpOptions ?? new FtpServerOptions();
            RawTcpMode = true;
            SuppressConnectionTest = true;
            _nextPassivePort = _ftpOptions.PassivePortStart;
            ClientConnected += OnClientConnected;
            ClientDisconnected += OnClientDisconnected;
        }

        private void OnClientConnected(ConnectedEventArgs e) {
            Log("[FTP] Client connected from " + FormatEndpoint(e.Connection) + ".");
            var session = new FtpSession(this, e.Connection, _ftpOptions);
            _sessions[e.Connection.ID] = session;
            Task.Run(() => session.RunAsync());
        }

        private void OnClientDisconnected(DisconnectedEventArgs e) {
            Log("[FTP] Client disconnected from " + FormatEndpoint(e.Connection) + ".");
            if (_sessions.TryRemove(e.Connection.ID, out var session))
                session.Dispose();
        }

        private static string FormatEndpoint(NetworkConnection connection) {
            return connection?.EndPoint?.ToString() ?? "unknown";
        }

        internal int NextPassivePort() {
            int port = Interlocked.Increment(ref _nextPassivePort);
            if (port > _ftpOptions.PassivePortEnd) {
                Interlocked.Exchange(ref _nextPassivePort, _ftpOptions.PassivePortStart);
                port = _ftpOptions.PassivePortStart;
            }
            return port;
        }
    }

    internal sealed class FtpSession : IDisposable {
        private readonly FtpServer _server;
        private readonly NetworkConnection _connection;
        private readonly FtpServerOptions _options;
        private Stream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private FtpUser _user;
        private string _pendingUser;
        private string _cwd = "/";
        private FtpTransferType _type = FtpTransferType.Binary;
        private long _restartOffset;
        private TcpListener _passiveListener;
        private IPEndPoint _activeEndpoint;
        private bool _secureData;
        private bool _quitRequested;

        public FtpSession(FtpServer server, NetworkConnection connection, FtpServerOptions options) {
            _server = server;
            _connection = connection;
            _options = options;
            _stream = connection.Stream;
            CreatePipes();
        }

        public async Task RunAsync() {
            Log("Session started for " + RemoteEndpointText + ".");
            try {
                await ReplyAsync(220, _options.ServerName + " ready.").ConfigureAwait(false);
                string line;
                while ((line = await _reader.ReadLineAsync().ConfigureAwait(false)) != null) {
                    await HandleCommandAsync(line).ConfigureAwait(false);
                }
            } catch (ObjectDisposedException) {
                if (!_quitRequested)
                    Log("Session stream closed unexpectedly for " + RemoteEndpointText + ".");
            } catch (Exception ex) {
                Log("Session error for " + RemoteEndpointText + ": " + ex.Message);
            } finally {
                Dispose();
                try { _connection.CloseConnection(); } catch { }
                Log("Session closed for " + RemoteEndpointText + ".");
            }
        }

        private async Task HandleCommandAsync(string line) {
            string command = line;
            string arg = string.Empty;
            int space = line.IndexOf(' ');
            if (space >= 0) {
                command = line.Substring(0, space);
                arg = line.Substring(space + 1);
            }
            command = command.ToUpperInvariant();
            Log("<= " + FormatCommandForLog(command, arg));

            switch (command) {
                case "USER": _pendingUser = arg; await ReplyAsync(331, "Password required.").ConfigureAwait(false); break;
                case "PASS": await PasswordAsync(arg).ConfigureAwait(false); break;
                case "AUTH": await AuthAsync(arg).ConfigureAwait(false); break;
                case "PBSZ": await ReplyAsync(200, "PBSZ=0").ConfigureAwait(false); break;
                case "PROT": _secureData = arg.Equals("P", StringComparison.OrdinalIgnoreCase); await ReplyAsync(200, "Protection level set.").ConfigureAwait(false); break;
                case "FEAT": await ReplyMultilineAsync(211, new[] { "UTF8", "MLST type*;size*;modify*;", "MLSD", "REST STREAM", "SIZE", "MDTM", "MFMT", "EPSV", "EPRT", "AUTH TLS", "PBSZ", "PROT" }, "End").ConfigureAwait(false); break;
                case "SYST": await ReplyAsync(215, "UNIX Type: L8").ConfigureAwait(false); break;
                case "OPTS": await ReplyAsync(200, arg.ToUpperInvariant().StartsWith("UTF8") ? "UTF8 enabled." : "Option accepted.").ConfigureAwait(false); break;
                case "NOOP": await ReplyAsync(200, "OK").ConfigureAwait(false); break;
                case "QUIT": _quitRequested = true; await ReplyAsync(221, "Goodbye.").ConfigureAwait(false); Dispose(); break;
                case "PWD":
                case "XPWD": await RequireLoginAsync(() => ReplyAsync(257, "\"" + _cwd.Replace("\"", "\"\"") + "\" is current directory.")).ConfigureAwait(false); break;
                case "CWD": await RequireLoginAsync(() => ChangeDirectoryAsync(arg)).ConfigureAwait(false); break;
                case "CDUP": await RequireLoginAsync(() => ChangeDirectoryAsync("..")).ConfigureAwait(false); break;
                case "TYPE": _type = arg.StartsWith("A", StringComparison.OrdinalIgnoreCase) ? FtpTransferType.Ascii : FtpTransferType.Binary; await ReplyAsync(200, "Type set.").ConfigureAwait(false); break;
                case "MODE": await ReplyAsync(arg.Equals("S", StringComparison.OrdinalIgnoreCase) ? 200 : 504, arg.Equals("S", StringComparison.OrdinalIgnoreCase) ? "Mode set." : "Only stream mode supported.").ConfigureAwait(false); break;
                case "STRU": await ReplyAsync(arg.Equals("F", StringComparison.OrdinalIgnoreCase) ? 200 : 504, arg.Equals("F", StringComparison.OrdinalIgnoreCase) ? "Structure set." : "Only file structure supported.").ConfigureAwait(false); break;
                case "PASV": await RequireLoginAsync(StartPassiveAsync).ConfigureAwait(false); break;
                case "EPSV": await RequireLoginAsync(StartExtendedPassiveAsync).ConfigureAwait(false); break;
                case "PORT": await RequireLoginAsync(() => SetActiveAsync(arg)).ConfigureAwait(false); break;
                case "EPRT": await RequireLoginAsync(() => SetExtendedActiveAsync(arg)).ConfigureAwait(false); break;
                case "LIST": await RequireLoginAsync(() => ListAsync(arg, false)).ConfigureAwait(false); break;
                case "NLST": await RequireLoginAsync(() => NameListAsync(arg)).ConfigureAwait(false); break;
                case "MLSD": await RequireLoginAsync(() => ListAsync(arg, true)).ConfigureAwait(false); break;
                case "RETR": await RequireLoginAsync(() => RetrieveAsync(arg)).ConfigureAwait(false); break;
                case "STOR": await RequireLoginAsync(() => StoreAsync(arg, false, false)).ConfigureAwait(false); break;
                case "APPE": await RequireLoginAsync(() => StoreAsync(arg, true, false)).ConfigureAwait(false); break;
                case "STOU": await RequireLoginAsync(() => StoreAsync(string.IsNullOrWhiteSpace(arg) ? Guid.NewGuid().ToString("N") + ".upload" : Path.Combine(arg, Guid.NewGuid().ToString("N") + ".upload"), false, true)).ConfigureAwait(false); break;
                case "REST": long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out _restartOffset); await ReplyAsync(350, "Restart position accepted.").ConfigureAwait(false); break;
                case "SIZE": await RequireLoginAsync(() => SizeAsync(arg)).ConfigureAwait(false); break;
                case "MDTM": await RequireLoginAsync(() => ModifiedAsync(arg)).ConfigureAwait(false); break;
                case "MFMT": await RequireLoginAsync(() => SetModifiedAsync(arg)).ConfigureAwait(false); break;
                case "DELE": await RequireLoginAsync(() => DeleteFileAsync(arg)).ConfigureAwait(false); break;
                case "MKD":
                case "XMKD": await RequireLoginAsync(() => MakeDirectoryAsync(arg)).ConfigureAwait(false); break;
                case "RMD":
                case "XRMD": await RequireLoginAsync(() => RemoveDirectoryAsync(arg)).ConfigureAwait(false); break;
                case "RNFR": await RequireLoginAsync(() => RenameFromAsync(arg)).ConfigureAwait(false); break;
                case "RNTO": await RequireLoginAsync(() => RenameToAsync(arg)).ConfigureAwait(false); break;
                case "SITE": await RequireLoginAsync(() => SiteAsync(arg)).ConfigureAwait(false); break;
                case "STAT": await ReplyAsync(211, _options.ServerName + " status OK.").ConfigureAwait(false); break;
                case "HELP": await ReplyAsync(214, "SocketJack FTP supports USER PASS AUTH PBSZ PROT FEAT SYST PWD CWD CDUP TYPE MODE STRU PASV EPSV PORT EPRT LIST NLST MLSD RETR STOR APPE STOU REST SIZE MDTM MFMT DELE MKD RMD RNFR RNTO SITE NOOP STAT HELP QUIT.").ConfigureAwait(false); break;
                default: await ReplyAsync(502, "Command not implemented.").ConfigureAwait(false); break;
            }
        }

        private string _renameFrom;

        private async Task PasswordAsync(string password) {
            var auth = _options.Authenticator;
            if (auth == null) {
                if (_options.AllowAnonymous && string.Equals(_pendingUser, "anonymous", StringComparison.OrdinalIgnoreCase)) {
                    _user = new FtpUser { UserName = "anonymous", RootPath = _options.RootPath, IsAnonymous = true, CanWrite = false, CanDelete = false, CanCreateDirectory = false };
                }
            } else {
                _user = await auth.AuthenticateAsync(_pendingUser, password, CancellationToken.None).ConfigureAwait(false);
            }
            if (_user == null) {
                await ReplyAsync(530, "Authentication failed.").ConfigureAwait(false);
                return;
            }
            if (string.IsNullOrWhiteSpace(_user.RootPath))
                _user.RootPath = _options.RootPath;
            Directory.CreateDirectory(_user.RootPath);
            await ReplyAsync(230, "Login successful.").ConfigureAwait(false);
        }

        private async Task AuthAsync(string arg) {
            if (!arg.Equals("TLS", StringComparison.OrdinalIgnoreCase) && !arg.Equals("SSL", StringComparison.OrdinalIgnoreCase)) {
                await ReplyAsync(504, "Unsupported AUTH type.").ConfigureAwait(false);
                return;
            }
            if (_options.ServerCertificate == null) {
                await ReplyAsync(454, "TLS unavailable.").ConfigureAwait(false);
                return;
            }
            await ReplyAsync(234, "Starting TLS.").ConfigureAwait(false);
            var ssl = new SslStream(_stream, false);
            ssl.AuthenticateAsServer(_options.ServerCertificate, false, SslProtocols.Tls12, false);
            _stream = ssl;
            CreatePipes();
        }

        private Task RequireLoginAsync(Func<Task> action) {
            if (_user == null)
                return ReplyAsync(530, "Please login with USER and PASS.");
            if (_options.RequireTls && !(_stream is SslStream))
                return ReplyAsync(522, "TLS is required.");
            return action();
        }

        private Task ChangeDirectoryAsync(string path) {
            var resolved = ResolvePath(path);
            if (!Directory.Exists(resolved))
                return ReplyAsync(550, "Directory not found.");
            _cwd = ToVirtualPath(resolved);
            return ReplyAsync(250, "Directory changed.");
        }

        private async Task StartPassiveAsync() {
            StartPassiveListener(out var ep);
            var address = _options.PublicAddress ?? ((IPEndPoint)_connection.Socket.LocalEndPoint).Address;
            var bytes = address.GetAddressBytes();
            await ReplyAsync(227, "Entering Passive Mode (" + string.Join(",", bytes.Select(b => b.ToString(CultureInfo.InvariantCulture))) + "," + (ep.Port / 256) + "," + (ep.Port % 256) + ").").ConfigureAwait(false);
        }

        private async Task StartExtendedPassiveAsync() {
            StartPassiveListener(out var ep);
            await ReplyAsync(229, "Entering Extended Passive Mode (|||" + ep.Port + "|).").ConfigureAwait(false);
        }

        private void StartPassiveListener(out IPEndPoint ep) {
            StopPassive();
            int attempts = Math.Max(1, _options.PassivePortEnd - _options.PassivePortStart + 1);
            Exception last = null;
            for (int i = 0; i < attempts; i++) {
                int port = _server.NextPassivePort();
                try {
                    _passiveListener = new TcpListener(IPAddress.Any, port);
                    _passiveListener.Start(1);
                    ep = (IPEndPoint)_passiveListener.LocalEndpoint;
                    _activeEndpoint = null;
                    Log("Passive listener opened on port " + ep.Port.ToString(CultureInfo.InvariantCulture) + ".");
                    return;
                } catch (Exception ex) {
                    last = ex;
                }
            }
            throw new InvalidOperationException("No passive FTP ports are available.", last);
        }

        private Task SetActiveAsync(string arg) {
            var parts = arg.Split(',');
            if (parts.Length != 6)
                return ReplyAsync(501, "Bad PORT syntax.");
            var address = IPAddress.Parse(string.Join(".", parts.Take(4)));
            int port = int.Parse(parts[4], CultureInfo.InvariantCulture) * 256 + int.Parse(parts[5], CultureInfo.InvariantCulture);
            _activeEndpoint = new IPEndPoint(address, port);
            StopPassive();
            Log("Active data endpoint set to " + _activeEndpoint + ".");
            return ReplyAsync(200, "Active data connection accepted.");
        }

        private Task SetExtendedActiveAsync(string arg) {
            char delimiter = arg[0];
            var parts = arg.Split(delimiter);
            if (parts.Length < 4)
                return ReplyAsync(501, "Bad EPRT syntax.");
            _activeEndpoint = new IPEndPoint(IPAddress.Parse(parts[2]), int.Parse(parts[3], CultureInfo.InvariantCulture));
            StopPassive();
            Log("Extended active data endpoint set to " + _activeEndpoint + ".");
            return ReplyAsync(200, "Extended active data connection accepted.");
        }

        private async Task ListAsync(string path, bool machineReadable) {
            string resolved = ResolvePath(string.IsNullOrWhiteSpace(path) ? _cwd : path);
            if (!Directory.Exists(resolved)) {
                await ReplyAsync(550, "Directory not found.").ConfigureAwait(false);
                return;
            }
            await WithDataStreamAsync(async data => {
                using (var writer = new StreamWriter(data, _options.Encoding, 4096, true) { NewLine = "\r\n" }) {
                    foreach (var dir in Directory.GetDirectories(resolved))
                        await writer.WriteLineAsync(machineReadable ? ToMlsd(dir, true) : ToUnixList(dir, true)).ConfigureAwait(false);
                    foreach (var file in Directory.GetFiles(resolved))
                        await writer.WriteLineAsync(machineReadable ? ToMlsd(file, false) : ToUnixList(file, false)).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        private async Task NameListAsync(string path) {
            string resolved = ResolvePath(string.IsNullOrWhiteSpace(path) ? _cwd : path);
            await WithDataStreamAsync(async data => {
                using (var writer = new StreamWriter(data, _options.Encoding, 4096, true) { NewLine = "\r\n" }) {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(resolved))
                        await writer.WriteLineAsync(Path.GetFileName(entry)).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        private async Task RetrieveAsync(string path) {
            if (!_user.CanRead) { await ReplyAsync(550, "Read denied.").ConfigureAwait(false); return; }
            string resolved = ResolvePath(path);
            if (!File.Exists(resolved)) { await ReplyAsync(550, "File not found.").ConfigureAwait(false); return; }
            await WithDataStreamAsync(async data => {
                using (var input = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    if (_restartOffset > 0) input.Position = _restartOffset;
                    await input.CopyToAsync(data).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
            _restartOffset = 0;
        }

        private async Task StoreAsync(string path, bool append, bool unique) {
            if (!_user.CanWrite) { await ReplyAsync(550, "Write denied.").ConfigureAwait(false); return; }
            string resolved = ResolvePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(resolved));
            await WithDataStreamAsync(async data => {
                using (var output = new FileStream(resolved, append ? FileMode.Append : FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)) {
                    if (!append) output.SetLength(0);
                    if (_restartOffset > 0) output.Position = _restartOffset;
                    await data.CopyToAsync(output).ConfigureAwait(false);
                }
            }, unique ? "FILE: " + ToVirtualPath(resolved) : null).ConfigureAwait(false);
            _restartOffset = 0;
        }

        private Task SizeAsync(string path) {
            string resolved = ResolvePath(path);
            if (!File.Exists(resolved)) return ReplyAsync(550, "File not found.");
            return ReplyAsync(213, new FileInfo(resolved).Length.ToString(CultureInfo.InvariantCulture));
        }

        private Task ModifiedAsync(string path) {
            string resolved = ResolvePath(path);
            if (!File.Exists(resolved) && !Directory.Exists(resolved)) return ReplyAsync(550, "Path not found.");
            return ReplyAsync(213, File.GetLastWriteTimeUtc(resolved).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        }

        private Task SetModifiedAsync(string arg) {
            int space = arg.IndexOf(' ');
            if (space <= 0) return ReplyAsync(501, "Bad MFMT syntax.");
            string stamp = arg.Substring(0, space);
            string path = arg.Substring(space + 1);
            var dt = DateTime.ParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
            string resolved = ResolvePath(path);
            File.SetLastWriteTimeUtc(resolved, dt);
            return ReplyAsync(213, stamp);
        }

        private Task DeleteFileAsync(string path) {
            if (!_user.CanDelete) return ReplyAsync(550, "Delete denied.");
            File.Delete(ResolvePath(path));
            return ReplyAsync(250, "File deleted.");
        }

        private Task MakeDirectoryAsync(string path) {
            if (!_user.CanCreateDirectory) return ReplyAsync(550, "Create directory denied.");
            Directory.CreateDirectory(ResolvePath(path));
            return ReplyAsync(257, "\"" + path + "\" created.");
        }

        private Task RemoveDirectoryAsync(string path) {
            if (!_user.CanDelete) return ReplyAsync(550, "Delete denied.");
            Directory.Delete(ResolvePath(path));
            return ReplyAsync(250, "Directory removed.");
        }

        private Task RenameFromAsync(string path) {
            _renameFrom = ResolvePath(path);
            return ReplyAsync(350, "Rename source accepted.");
        }

        private Task RenameToAsync(string path) {
            if (string.IsNullOrWhiteSpace(_renameFrom)) return ReplyAsync(503, "RNFR required first.");
            string to = ResolvePath(path);
            if (File.Exists(_renameFrom)) File.Move(_renameFrom, to);
            else Directory.Move(_renameFrom, to);
            _renameFrom = null;
            return ReplyAsync(250, "Rename successful.");
        }

        private Task SiteAsync(string arg) {
            if (arg.StartsWith("CHMOD ", StringComparison.OrdinalIgnoreCase))
                return ReplyAsync(200, "SITE CHMOD accepted.");
            return ReplyAsync(502, "SITE command not implemented.");
        }

        private async Task WithDataStreamAsync(Func<Stream, Task> action, string completionSuffix = null) {
            await ReplyAsync(150, "Opening data connection.").ConfigureAwait(false);
            using (var data = await OpenDataStreamAsync().ConfigureAwait(false)) {
                await action(data).ConfigureAwait(false);
            }
            await ReplyAsync(226, string.IsNullOrWhiteSpace(completionSuffix) ? "Transfer complete." : "Transfer complete. " + completionSuffix).ConfigureAwait(false);
        }

        private async Task<Stream> OpenDataStreamAsync() {
            Stream stream;
            if (_passiveListener != null) {
                var tcp = await _passiveListener.AcceptTcpClientAsync().ConfigureAwait(false);
                stream = tcp.GetStream();
                Log("Passive data connection accepted from " + (tcp.Client?.RemoteEndPoint?.ToString() ?? "unknown") + ".");
                StopPassive();
            } else if (_activeEndpoint != null) {
                var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(_activeEndpoint.Address, _activeEndpoint.Port).ConfigureAwait(false);
                stream = tcp.GetStream();
                Log("Active data connection opened to " + _activeEndpoint + ".");
            } else {
                throw new InvalidOperationException("No FTP data connection mode selected.");
            }
            if (_secureData && _options.ServerCertificate != null) {
                var ssl = new SslStream(stream, false);
                ssl.AuthenticateAsServer(_options.ServerCertificate, false, SslProtocols.Tls12, false);
                stream = ssl;
            }
            return stream;
        }

        private string ResolvePath(string path) {
            if (string.IsNullOrWhiteSpace(path)) path = _cwd;
            string combined = path.StartsWith("/", StringComparison.Ordinal) ? Path.Combine(_user.RootPath, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)) : Path.Combine(_user.RootPath, _cwd.TrimStart('/').Replace('/', Path.DirectorySeparatorChar), path.Replace('/', Path.DirectorySeparatorChar));
            string full = Path.GetFullPath(combined);
            string root = Path.GetFullPath(_user.RootPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Path escapes FTP root.");
            return full;
        }

        private string ToVirtualPath(string fullPath) {
            string root = Path.GetFullPath(_user.RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string relative = Path.GetFullPath(fullPath).StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(fullPath).Substring(root.Length) : string.Empty;
            return "/" + relative.Replace(Path.DirectorySeparatorChar, '/').Trim('/');
        }

        private static string ToMlsd(string path, bool directory) {
            var info = directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            long size = directory ? 0 : ((FileInfo)info).Length;
            return "type=" + (directory ? "dir" : "file") + ";size=" + size + ";modify=" + info.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "; " + info.Name;
        }

        private static string ToUnixList(string path, bool directory) {
            var info = directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            long size = directory ? 0 : ((FileInfo)info).Length;
            return (directory ? "d" : "-") + "rw-r--r-- 1 owner group " + size.ToString(CultureInfo.InvariantCulture).PadLeft(12) + " " + info.LastWriteTime.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture) + " " + info.Name;
        }

        private Task ReplyMultilineAsync(int code, IEnumerable<string> lines, string end) {
            Log("=> " + code.ToString(CultureInfo.InvariantCulture) + "-" + _options.ServerName);
            return _writer.WriteLineAsync(code.ToString(CultureInfo.InvariantCulture) + "-" + _options.ServerName)
                .ContinueWith(async _ => {
                    foreach (var line in lines)
                        await _writer.WriteLineAsync(" " + line).ConfigureAwait(false);
                    await ReplyAsync(code, end).ConfigureAwait(false);
                }).Unwrap();
        }

        private Task ReplyAsync(int code, string message) {
            Log("=> " + code.ToString(CultureInfo.InvariantCulture) + " " + message);
            return _writer.WriteLineAsync(code.ToString(CultureInfo.InvariantCulture) + " " + message);
        }

        private string RemoteEndpointText => _connection?.EndPoint?.ToString() ?? "unknown";

        private void Log(string text) {
            try { _server?.Log("[FTP] " + text); } catch { }
        }

        private static string FormatCommandForLog(string command, string arg) {
            if (command.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                return "PASS ********";
            return string.IsNullOrWhiteSpace(arg) ? command : command + " " + arg;
        }

        private void CreatePipes() {
            _reader = new StreamReader(_stream, _options.Encoding, false, 4096, true);
            _writer = new StreamWriter(_stream, _options.Encoding, 4096, true) { NewLine = "\r\n", AutoFlush = true };
        }

        private void StopPassive() {
            try { _passiveListener?.Stop(); } catch { }
            _passiveListener = null;
        }

        public void Dispose() {
            StopPassive();
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
        }
    }

    public sealed class SftpClientOptions {
        public string Host { get; set; }
        public int Port { get; set; } = 22;
        public string UserName { get; set; }
        public string Password { get; set; }
        public string PrivateKeyPath { get; set; }
        public string PrivateKeyPassphrase { get; set; }
        /// <summary>Expected OpenSSH SHA256 host key fingerprint, obtained from a trusted source.</summary>
        public string HostKeySha256 { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    public sealed class SftpClient : IDisposable {
        private Renci.SshNet.SftpClient _client;

        public bool IsConnected => _client != null && _client.IsConnected;

        public void Connect(SftpClientOptions options) {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string pin = options.HostKeySha256;
            if (pin == null || !pin.StartsWith("SHA256:", StringComparison.Ordinal) || pin.Length != 50)
                throw new ArgumentException("An OpenSSH SHA256 host key fingerprint is required.", nameof(options));
            byte[] expected;
            try { expected = Convert.FromBase64String(pin.Substring(7) + "="); }
            catch (FormatException) { throw new ArgumentException("Invalid host key fingerprint.", nameof(options)); }
            var methods = new List<AuthenticationMethod>();
            if (!string.IsNullOrEmpty(options.Password))
                methods.Add(new PasswordAuthenticationMethod(options.UserName, options.Password));
            if (!string.IsNullOrEmpty(options.PrivateKeyPath)) {
                var key = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(options.PrivateKeyPath)
                    : new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
                methods.Add(new PrivateKeyAuthenticationMethod(options.UserName, key));
            }
            var info = new ConnectionInfo(options.Host, options.Port, options.UserName, methods.ToArray()) { Timeout = options.Timeout };
            var candidate = new Renci.SshNet.SftpClient(info);
            candidate.HostKeyReceived += (_, e) => {
                using var hash = System.Security.Cryptography.SHA256.Create();
                e.CanTrust = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, hash.ComputeHash(e.HostKey));
            };
            try { candidate.Connect(); }
            catch { candidate.Dispose(); throw; }
            _client?.Dispose();
            _client = candidate;
        }

        public Task ConnectAsync(SftpClientOptions options, CancellationToken cancellationToken = default) {
            return Task.Run(() => Connect(options), cancellationToken);
        }

        public IEnumerable<ISftpFile> ListDirectory(string path) => _client.ListDirectory(path);
        public Task<IEnumerable<ISftpFile>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() => _client.ListDirectory(path), cancellationToken);
        public void ChangeDirectory(string path) => _client.ChangeDirectory(path);
        public string WorkingDirectory => _client.WorkingDirectory;
        public bool Exists(string path) => _client.Exists(path);
        public void CreateDirectory(string path) => _client.CreateDirectory(path);
        public void DeleteDirectory(string path) => _client.DeleteDirectory(path);
        public void DeleteFile(string path) => _client.DeleteFile(path);
        public void RenameFile(string oldPath, string newPath) => _client.RenameFile(oldPath, newPath);
        public SftpFileAttributes GetAttributes(string path) => _client.GetAttributes(path);
        public void SetAttributes(string path, SftpFileAttributes attributes) => _client.SetAttributes(path, attributes);
        public Stream OpenRead(string path) => _client.OpenRead(path);
        public Stream OpenWrite(string path) => _client.OpenWrite(path);

        public void DownloadFile(string remotePath, string localPath, Action<ulong> progress = null) {
            using (var output = File.Create(localPath))
                _client.DownloadFile(remotePath, output, progress);
        }

        public void UploadFile(string localPath, string remotePath, bool canOverride = true, Action<ulong> progress = null) {
            using (var input = File.OpenRead(localPath))
                _client.UploadFile(input, remotePath, canOverride, progress);
        }

        public Task DownloadFileAsync(string remotePath, string localPath, Action<ulong> progress = null, CancellationToken cancellationToken = default) {
            return Task.Run(() => DownloadFile(remotePath, localPath, progress), cancellationToken);
        }

        public Task UploadFileAsync(string localPath, string remotePath, bool canOverride = true, Action<ulong> progress = null, CancellationToken cancellationToken = default) {
            return Task.Run(() => UploadFile(localPath, remotePath, canOverride, progress), cancellationToken);
        }

        public void Disconnect() {
            if (_client != null && _client.IsConnected)
                _client.Disconnect();
        }

        public void Dispose() {
            try { Disconnect(); } catch { }
            _client?.Dispose();
        }
    }

    public interface ISftpServerBackend {
        Task StartAsync(int port, CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
    }

    public sealed class SftpServer : IDisposable {
        private readonly ISftpServerBackend _backend;

        public SftpServer(ISftpServerBackend backend = null) {
            _backend = backend;
        }

        public Task StartAsync(int port = 22, CancellationToken cancellationToken = default) {
            if (_backend == null)
                throw new NotSupportedException("SFTP is an SSH subsystem. SocketJack provides the SFTP client directly; server hosting requires an SSH/SFTP backend implementing ISftpServerBackend.");
            return _backend.StartAsync(port, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default) {
            return _backend == null ? Task.CompletedTask : _backend.StopAsync(cancellationToken);
        }

        public void Dispose() {
            StopAsync().GetAwaiter().GetResult();
        }
    }
}
