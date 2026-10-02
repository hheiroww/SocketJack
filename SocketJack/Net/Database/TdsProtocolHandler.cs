using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SocketJack.Net.Database {

    /// <summary>
    /// An <see cref="IProtocolHandler"/> that detects TDS (Tabular Data Stream)
    /// connections and routes them to a <see cref="DataServer"/>.
    /// <para>
    /// Register this handler on a <see cref="MutableTcpServer"/> to serve MSSQL
    /// clients on the same port as HTTP, WebSocket, and SocketJack clients.
    /// </para>
    /// <example>
    /// <code>
    /// var mutable = new MutableTcpServer(1433, "MultiServer");
    /// mutable.RegisterProtocol(new TdsProtocolHandler());
    /// mutable.EnabledProtocols.Add(MutableTcpProtocols.Tds);
    /// mutable.Listen();
    /// </code>
    /// </example>
    /// </summary>
    public class TdsProtocolHandler : IProtocolHandler {

        private readonly DataServer _dataServer;

        /// <summary>
        /// Per-connection byte buffer for accumulating partial TDS packets.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, List<byte>> _buffers = new ConcurrentDictionary<Guid, List<byte>>();

        public string Name => "TDS";

        /// <summary>
        /// Returns the <see cref="DataServer"/> that handles TDS query execution,
        /// authentication, and in-memory storage for this handler.
        /// </summary>
        public DataServer Server => _dataServer;

        /// <summary>
        /// Creates a new TDS protocol handler backed by the given <see cref="DataServer"/>.
        /// </summary>
        public TdsProtocolHandler(DataServer dataServer) {
            _dataServer = dataServer ?? throw new ArgumentNullException(nameof(dataServer));
        }

        /// <summary>
        /// Creates a new TDS protocol handler with an automatically created
        /// hosted-mode <see cref="DataServer"/> (no standalone TCP listener).
        /// Use this when the handler is registered on a <see cref="MutableTcpServer"/>.
        /// </summary>
        public TdsProtocolHandler() {
            _dataServer = new DataServer("DataServer", hosted: true, loadFromDisk: true);
        }

        /// <summary>
        /// Returns <see langword="true"/> when the first bytes look like a TDS
        /// Pre-Login packet (type <c>0x12</c>) or a TDS Login7 packet (type <c>0x10</c>).
        /// </summary>
        public bool CanHandle(byte[] data) {
            if (data == null || data.Length < 8)
                return false;

            byte packetType = data[0];
            // TDS Pre-Login = 0x12, Login7 = 0x10
            // Status byte (data[1]) should be 0x00 or 0x01 (normal / EOM)
            if (packetType == 0x12 || packetType == 0x10) {
                byte status = data[1];
                if (status == 0x00 || status == 0x01) {
                    // Validate the length field is sane
                    ushort length = (ushort)((data[2] << 8) | data[3]);
                    return length >= 8 && length <= 32768;
                }
            }

            return false;
        }

        public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
            byte[] incoming = MutableTcpServer.TryGetRawBytes(e.Obj);
            if (incoming == null || incoming.Length == 0)
                return;

            // Once TLS is active
            // This should never be reached because ReceiveData is blocked, but
            // guard against edge cases.
            if (_dataServer.Sessions.TryGetValue(connection.ID, out var existingSession) && existingSession.IsTlsActive)
                return;

            // Detect first data — create session
            bool isNew = !_buffers.ContainsKey(connection.ID);
            if (isNew) {
                connection._Protocol = TcpProtocol.Tds;
                // Take exclusive ownership of the stream so SocketJack's
                // background send/receive loops cannot corrupt TDS framing.
                connection.RawTcpMode = true;
                connection.SuppressConnectionTest = true;
                var session = new SqlSession {
                    ConnectionId = connection.ID,
                    CurrentDatabase = _dataServer.DefaultDatabase,
                    ServerName = _dataServer.ServerName,
                    ServerVersion = _dataServer.ServerVersion
                };
                _dataServer.Sessions.TryAdd(connection.ID, session);
            }

            var buffer = _buffers.GetOrAdd(connection.ID, _ => new List<byte>());
            lock (buffer) {
                buffer.AddRange(incoming);
                ProcessTdsBuffer(buffer, connection);
            }
        }

        public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
            _buffers.TryRemove(connection.ID, out _);
            if (_dataServer.Sessions.TryRemove(connection.ID, out var session)) {
                // Signal the wrapper to stop feeding data so that any blocked
                // SslStream.Read returns promptly.  Do NOT dispose the TlsStream
                // here — RunTdsReadLoop may still be reading from it on another
                // thread.  The read loop will detect connection.Closed and exit,
                // and the post-loop cleanup (or GC) handles disposal.
                session.WrapperStream?.FeedClose();
            }
        }

        /// <summary>
        /// Processes buffered bytes, extracting complete TDS packets and handling them.
        /// </summary>
        private void ProcessTdsBuffer(List<byte> buffer, NetworkConnection connection) {
            while (buffer.Count >= 8) {
                byte packetType = buffer[0];
                byte status = buffer[1];
                ushort length = (ushort)((buffer[2] << 8) | buffer[3]);

                if (length < 8 || buffer.Count < length)
                    break; // incomplete packet

                byte[] data = new byte[length - 8];
                buffer.CopyTo(8, data, 0, data.Length);
                buffer.RemoveRange(0, length);

                // Handle multi-packet TDS messages: if EOM (0x01) is NOT set,
                // buffer the data until we receive the final EOM packet.
                if ((status & 0x01) == 0) {
                    if (_multiPacketBuffer == null)
                        _multiPacketBuffer = new Dictionary<Guid, (byte type, List<byte> data)>();
                    if (!_multiPacketBuffer.TryGetValue(connection.ID, out var mpBuf)) {
                        mpBuf = (packetType, new List<byte>(data));
                        _multiPacketBuffer[connection.ID] = mpBuf;
                    } else {
                        mpBuf.data.AddRange(data);
                        _multiPacketBuffer[connection.ID] = mpBuf;
                    }
                    continue;
                }

                // EOM packet — combine with any buffered data from earlier packets
                if (_multiPacketBuffer != null && _multiPacketBuffer.TryGetValue(connection.ID, out var pending)) {
                    pending.data.AddRange(data);
                    data = pending.data.ToArray();
                    packetType = pending.type;
                    _multiPacketBuffer.Remove(connection.ID);
                }

                if (!_dataServer.Sessions.TryGetValue(connection.ID, out var session))
                    break;

                HandleTdsPacket(connection, session, packetType, data);
            }
        }

        private void HandleTdsPacket(NetworkConnection connection, SqlSession session, byte packetType, byte[] data) {
            switch (packetType) {
                case 0x12: // Pre-Login
                    ProcessPreLogin(connection, session, data);
                    break;
                case 0x10: // Login7 or SQL Batch
                    if (!session.IsAuthenticated)
                        ProcessLogin(connection, session, data);
                    else
                        ProcessSqlBatch(connection, session, data);
                    break;
                case 0x01: // SQL Batch
                    ProcessSqlBatch(connection, session, data);
                    break;
                case 0x03: // RPC
                    ProcessRpc(connection, session, data);
                    break;
                case 0x0E: // Transaction Manager Request
                    SendErrorResponse(connection, session, 40510, "Transaction-manager requests are not implemented; no transaction was started or committed.");
                    break;
                case 0x06: // Attention (cancel)
                    // Respond with DONE + ATTN flag so the client knows we acknowledged.
                    SendDone(connection, 0x0020, 0, 0); // DONE_ATTN = 0x0020
                    break;
                default:
                    // Unknown packet types — just send DONE to avoid hanging.
                    SendDone(connection, 0, 0, 0);
                    break;
            }
        }

        #region TDS Packet Processing

        private void ProcessPreLogin(NetworkConnection connection, SqlSession session, byte[] data) {

            // Parse the client's requested encryption mode from the Pre-Login options.
            byte clientEncryption = 0x00; // Default: ENCRYPT_OFF
            try {
                int pos = 0;
                while (pos < data.Length && data[pos] != 0xFF) {
                    byte token = data[pos];
                    ushort optOffset = (ushort)((data[pos + 1] << 8) | data[pos + 2]);
                    ushort optLength = (ushort)((data[pos + 3] << 8) | data[pos + 4]);
                    if (token == 0x01 && optLength >= 1 && optOffset < data.Length) // ENCRYPTION
                        clientEncryption = data[optOffset];
                    pos += 5;
                }
            } catch { }

            // Negotiate encryption per [MS-TDS] 2.2.6.4:
            //   ENCRYPT_OFF   (0x00): Encrypt login packet only, then plaintext.
            //   ENCRYPT_ON    (0x01): Encrypt all traffic after TLS handshake.
            //   ENCRYPT_NOT_SUP (0x02): No encryption at all.
            //   ENCRYPT_REQ   (0x03): Server requires full encryption.
            //
            // Use the server's configured EncryptionMode.  Default is ENCRYPT_OFF
            // which encrypts only the login phase then reverts to plaintext —
            // matching what most SQL clients request with Encrypt=Optional.
            // If the client requests ENCRYPT_ON or ENCRYPT_REQ, honor it.
            byte serverMode = _dataServer.EncryptionMode;
            byte negotiated;
            if (clientEncryption == 0x01 || clientEncryption == 0x03) {
                // Client wants full encryption — comply.
                negotiated = 0x01;
            } else {
                negotiated = serverMode;
            }
            session.NegotiatedEncryption = negotiated;

            // TDS Pre-Login response: option entries use BIG-ENDIAN offsets/lengths.
            // Options: VERSION(5) + ENCRYPTION(5) + INSTOPT(5) + THREADID(5) + MARS(5) + TERMINATOR(1) = 26 bytes header.
            // Data block starts at offset 26.
            //   VERSION data: offset 26, length 6
            //   ENCRYPTION data: offset 32, length 1
            //   INSTOPT data: offset 33, length 1  (0x00 = default instance)
            //   THREADID data: offset 34, length 4  (0x00000000)
            //   MARS option (token 0x04) — offset 38, length 1
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    // VERSION option (token 0x00) — offset 26, length 6
                    writer.Write((byte)0x00);
                    writer.Write((byte)0x00); writer.Write((byte)0x1A); // offset = 26
                    writer.Write((byte)0x00); writer.Write((byte)0x06); // length = 6

                    // ENCRYPTION option (token 0x01) — offset 32, length 1
                    writer.Write((byte)0x01);
                    writer.Write((byte)0x00); writer.Write((byte)0x20); // offset = 32
                    writer.Write((byte)0x00); writer.Write((byte)0x01); // length = 1

                    // INSTOPT option (token 0x02) — offset 33, length 1
                    writer.Write((byte)0x02);
                    writer.Write((byte)0x00); writer.Write((byte)0x21); // offset = 33
                    writer.Write((byte)0x00); writer.Write((byte)0x01); // length = 1

                    // THREADID option (token 0x03) — offset 34, length 4
                    writer.Write((byte)0x03);
                    writer.Write((byte)0x00); writer.Write((byte)0x22); // offset = 34
                    writer.Write((byte)0x00); writer.Write((byte)0x04); // length = 4

                    // MARS option (token 0x04) — offset 38, length 1
                    writer.Write((byte)0x04);
                    writer.Write((byte)0x00); writer.Write((byte)0x26); // offset = 38
                    writer.Write((byte)0x00); writer.Write((byte)0x01); // length = 1

                    // TERMINATOR
                    writer.Write((byte)0xFF);

                    // VERSION data (6 bytes): Major, Minor, Build (BE), Sub-build (BE)
                    writer.Write((byte)17);                  // Major
                    writer.Write((byte)0);                   // Minor
                    writer.Write((byte)(4025 >> 8));         // Build high byte
                    writer.Write((byte)(4025 & 0xFF));       // Build low byte
                    writer.Write((byte)0); writer.Write((byte)3); // Sub-build

                    // ENCRYPTION data (1 byte) — the negotiated value
                    writer.Write(negotiated);

                    // INSTOPT data (1 byte) — 0x00 = default instance (null-terminated empty string)
                    writer.Write((byte)0x00);

                    // THREADID data (4 bytes) — server thread ID (0 = not applicable)
                    writer.Write((uint)0);

                    // MARS data (1 byte) — 0x00 = MARS not supported
                    writer.Write((byte)0x00);

                    SendTdsPacket(connection, session, 0x04, ms.ToArray());
                }
            }

            // Perform TLS handshake when encryption is negotiated.
            // ENCRYPT_NOT_SUP (0x02) means no encryption at all.
            if (negotiated != 0x02) {
                byte[] leftover = null;
                if (_buffers.TryGetValue(connection.ID, out var buf)) {
                    lock (buf) {
                        if (buf.Count > 0) {
                            leftover = buf.ToArray();
                            buf.Clear();
                        }
                    }
                }
                PerformTlsHandshake(connection, session, leftover);

                // In MutableTcpServer mode, enter the blocking read loop that
                // owns the stream until the session ends.  In standalone mode
                // (RunTdsLoop), just return — the caller handles TLS I/O and
                // feeder startup.
                if (_buffers.ContainsKey(connection.ID)) {
                    RunTdsReadLoop(connection, session);
                }
            }
        }

        /// <summary>
        /// Performs a TLS handshake on the connection's underlying TCP stream.
        /// <para>
        /// Per [MS-TDS] 2.2.6.5 the TLS handshake is encapsulated inside TDS
        /// Pre-Login (0x12) packets. A <see cref="TdsWrapperStream"/> sits
        /// between the <see cref="SslStream"/> and the raw
        /// <see cref="NetworkConnection._Stream"/> to handle this framing.
        /// </para>
        /// After this method returns, <see cref="SqlSession.TlsStream"/> is set
        /// and all subsequent TDS packets are sent/received over TLS.
        /// </summary>
        private void PerformTlsHandshake(NetworkConnection connection, SqlSession session, byte[] leftoverBuffer = null) {
            var cert = _dataServer.Certificate;
            if (cert == null)
                throw new InvalidOperationException("No certificate available for TDS encryption. Set DataServer.Certificate or call DataServer.GenerateSelfSignedCertificate().");

            var networkStream = connection._Stream;
            if (networkStream == null)
                throw new InvalidOperationException("No network stream available for TLS handshake.");

            // The TLS handshake bytes are wrapped in TDS 0x12 packets on the wire.
            // TdsWrapperStream handles the wrap/unwrap so SslStream sees plain TLS.
            var wrapperStream = new TdsWrapperStream(networkStream);

            // If ReceiveData already consumed bytes beyond the Pre-Login packet
            // (e.g. the TLS ClientHello), preseed them so the wrapper reads them
            // before falling back to the NetworkStream.
            if (leftoverBuffer != null && leftoverBuffer.Length > 0)
                wrapperStream.Preseed(leftoverBuffer);

            // Suppress the connection-test poll BEFORE the TLS handshake.
            // AuthenticateAsServer performs multiple round-trips during which
            // SslStream reads ahead from the raw socket, making Socket.Available
            // return 0 — the poll loop would accumulate failures and close the
            // connection before the handshake completes.
            connection.SuppressConnectionTest = true;

            var sslStream = new SslStream(wrapperStream, leaveInnerStreamOpen: true);

            // TDS 7.x wraps the TLS handshake in PRELOGIN packets. Use TLS 1.2;
            // TLS 1.3 requires a separate TDS 8.0 TLS-first handshake.
            var protocols = SslProtocols.Tls12;

            sslStream.AuthenticateAsServer(cert, clientCertificateRequired: false,
                enabledSslProtocols: protocols,
                checkCertificateRevocation: false);

            // TLS handshake is complete.
            // For ENCRYPT_ON the SslStream stays active for the whole session,
            // so passthrough (SslStream reads directly from NetworkStream) is fine.
            //
            // For ENCRYPT_OFF (login-only TLS) we must NOT let SslStream read
            // directly from NetworkStream.  SslStream reads ahead in large
            // chunks and consumes bytes beyond the last TLS record, leaving
            // NetworkStream at EOF when we switch to plaintext after teardown.
            // Instead, use the feed-buffer: RunTdsReadLoop reads individual TLS
            // records from NetworkStream and pushes them into the wrapper's feed
            // queue.  SslStream decrypts from the feed buffer only.  After
            // teardown, NetworkStream is positioned exactly after the last TLS
            // record and any subsequent plaintext data is still available.
            if (session.NegotiatedEncryption == 0x00) {
                wrapperStream.Passthrough = true;
                wrapperStream.UseFeedBuffer = true;
            } else {
                wrapperStream.Passthrough = true;
            }

            session.WrapperStream = wrapperStream;
            session.TlsStream = sslStream;
            session.IsTlsActive = true;
        }

        /// <summary>
        /// Runs a blocking TDS read loop that reads TDS packets from the
        /// appropriate stream (encrypted <see cref="SslStream"/> or plaintext
        /// <see cref="System.Net.Sockets.NetworkStream"/>) and dispatches them
        /// through <see cref="HandleTdsPacket"/>.
        /// <para>
        /// This method blocks the caller (ultimately <c>ReceiveData</c>) so that
        /// the TDS handler has exclusive access to the underlying
        /// <see cref="NetworkConnection._Stream"/> with no competing reader.
        /// When the session ends (ENCRYPT_OFF after login, disconnect, or
        /// ENCRYPT_NOT_SUP plaintext session) this method returns.
        /// </para>
        /// <para>
        /// For ENCRYPT_OFF the wrapper's feed-buffer is used: a background thread
        /// reads raw TLS records from <see cref="NetworkConnection._Stream"/> and
        /// pushes them into <see cref="TdsWrapperStream.Feed"/> so that
        /// <see cref="SslStream"/> never touches the NetworkStream directly.
        /// After teardown the feeder stops and this loop reads plaintext from
        /// NetworkStream.
        /// </para>
        /// </summary>
        private void RunTdsReadLoop(NetworkConnection connection, SqlSession session, byte[] leftover = null) {
            // For ENCRYPT_OFF with feed-buffer mode, start a background feeder
            // that reads raw TLS records from NetworkStream and pushes them to
            // the wrapper so SslStream can decrypt without touching NetworkStream.
            Thread feederThread = null;
            if (session.NegotiatedEncryption == 0x00 && session.WrapperStream != null && session.WrapperStream.UseFeedBuffer) {
                feederThread = new Thread(() => TlsRecordFeeder(connection, session)) {
                    IsBackground = true,
                    Name = "TDS-TlsFeeder-" + connection.ID.ToString("N").Substring(0, 8)
                };
                feederThread.Start();
            }

            // Track whether the feeder is still running.  While the feeder
            // owns the NetworkStream, we must keep reading from SslStream
            // (even after IsTlsActive is cleared) until the feeder exits.
            bool feederActive = (feederThread != null);

            try {
                Stream stream = (session.IsTlsActive && session.TlsStream != null)
                    ? (Stream)session.TlsStream
                    : connection._Stream;

                Stream leftoverStream = null;
                if (leftover != null && leftover.Length > 0) {
                    leftoverStream = new ConcatenatedStream(new MemoryStream(leftover, false), stream);
                }

                while (!connection.Closed) {
                    if (leftoverStream != null) {
                        stream = leftoverStream;
                        leftoverStream = null;
                    } else if (feederActive && session.TlsStream != null) {
                        // ENCRYPT_OFF: feeder owns NetworkStream; read from SslStream
                        stream = session.TlsStream;
                    } else if (session.IsTlsActive && session.TlsStream != null) {
                        // ENCRYPT_ON: SslStream reads directly from NetworkStream
                        stream = session.TlsStream;
                    } else {
                        stream = connection._Stream;
                    }
                    if (stream == null) break;

                    // Wrap reads in try-catch so that IOException from SslStream
                    // during ENCRYPT_OFF feed-buffer closure doesn't skip the
                    // PlaintextLeftover transition (the feeder closes the feed,
                    // and SslStream may throw instead of returning 0).
                    byte[] header;
                    Exception readEx = null;
                    try {
                        header = ReadBytesSync(stream, 8);
                    } catch (IOException ioEx) when (feederActive) {
                        header = null;
                        readEx = ioEx;
                    } catch (ObjectDisposedException odEx) when (feederActive) {
                        header = null;
                        readEx = odEx;
                    } catch (Exception ex) {
                        // Capture any other exception for diagnostics
                        header = null;
                        readEx = ex;
                    }

                    if (header == null || header.Length < 8) {
                        // ENCRYPT_OFF transition: the TLS feeder saw plaintext
                        // and closed the feed buffer, causing SslStream to return 0
                        // or throw IOException.  If the feeder captured leftover
                        // bytes, prepend them and continue reading plaintext from
                        // NetworkStream.
                        if (feederActive) {
                            // Give the feeder a moment to finish and set PlaintextLeftover.
                            if (feederThread != null && feederThread.IsAlive)
                                feederThread.Join(2000);
                            feederActive = false;

                            if (session.PlaintextLeftover != null) {
                                var lo = session.PlaintextLeftover;
                                session.PlaintextLeftover = null;
                                leftoverStream = new ConcatenatedStream(
                                    new MemoryStream(lo, false), connection._Stream);
                                continue;
                            }
                            // Feeder exited without leftover
                            // already sent plaintext directly on the NetworkStream.
                            // Try reading from NetworkStream before giving up.
                            // Give the client a moment to send data after processing login response.
                            if (!connection.Closed) {

                                // Wait briefly for client data to arrive
                                int waitAttempts = 0;
                                while (!connection.Closed && waitAttempts < 20) { // Increased to 1 second total
                                    if (connection._Stream != null && connection.Socket != null && connection.Socket.Available > 0)
                                        break;
                                    Thread.Sleep(50);
                                    waitAttempts++;
                                }

                                if (connection.Closed) {
                                    break;
                                }

                                stream = connection._Stream;
                                continue;
                            }
                        }
                        break;
                    }
                    connection.TrackBytesReceived(8);

                    byte packetType = header[0];
                    byte status = header[1];
                    ushort length = (ushort)((header[2] << 8) | header[3]);

                    if (length < 8) {
                        break;
                    }

                    byte[] data;
                    try {
                        data = ReadBytesSync(stream, length - 8);
                    } catch (IOException) when (feederActive) {
                        data = null;
                    } catch (ObjectDisposedException) when (feederActive) {
                        data = null;
                    }

                    if (data == null) {
                        // Same ENCRYPT_OFF transition handling as header read.
                        if (feederActive) {
                            if (feederThread != null && feederThread.IsAlive)
                                feederThread.Join(2000);
                            feederActive = false;

                            if (session.PlaintextLeftover != null) {
                                var lo = session.PlaintextLeftover;
                                session.PlaintextLeftover = null;
                                leftoverStream = new ConcatenatedStream(
                                    new MemoryStream(lo, false), connection._Stream);
                                continue;
                            }
                            if (!connection.Closed) {
                                stream = connection._Stream;
                                continue;
                            }
                        }
                        break;
                    }
                    connection.TrackBytesReceived(data.Length);

                    if ((status & 0x01) == 0) {
                        if (_multiPacketBuffer == null)
                            _multiPacketBuffer = new Dictionary<Guid, (byte type, List<byte> data)>();
                        if (!_multiPacketBuffer.TryGetValue(connection.ID, out var mpBuf)) {
                            mpBuf = (packetType, new List<byte>(data));
                            _multiPacketBuffer[connection.ID] = mpBuf;
                        } else {
                            mpBuf.data.AddRange(data);
                            _multiPacketBuffer[connection.ID] = mpBuf;
                        }
                        continue;
                    }

                    if (_multiPacketBuffer != null && _multiPacketBuffer.TryGetValue(connection.ID, out var pending)) {
                        pending.data.AddRange(data);
                        data = pending.data.ToArray();
                        packetType = pending.type;
                        _multiPacketBuffer.Remove(connection.ID);
                    }

                    HandleTdsPacket(connection, session, packetType, data);
                }
            } catch (Exception) {
                // Last-resort catch: check for ENCRYPT_OFF transition even here,
                // in case an unexpected exception type propagated from SslStream.
                if (feederActive && session.PlaintextLeftover != null) {
                    // Cannot re-enter the while loop from catch, but the finally
                    // block will clean up and the connection remains open for the
                    // ReceiveData loop to resume.
                }
            } finally {
                // Signal the feeder to stop: FeedClose unblocks any pending
                // Feed read so the feeder thread can see IsTlsActive==false.
                if (session.WrapperStream != null)
                    session.WrapperStream.FeedClose();
                if (feederThread != null && feederThread.IsAlive)
                    feederThread.Join(2000);
                // Keep RawTcpMode and SuppressConnectionTest enabled for TDS connections
            }
        }

        /// <summary>
        /// Background thread that reads raw TLS records from the
        /// <see cref="NetworkConnection._Stream"/> and feeds them into the
        /// <see cref="TdsWrapperStream"/> feed buffer.  This keeps SslStream
        /// from ever touching NetworkStream directly, preventing read-ahead
        /// that would steal post-teardown plaintext bytes.
        /// <para>
        /// A TLS 1.2 record has a 5-byte header:
        /// <c>[ContentType(1) ProtocolVersion(2) Length(2)]</c>.
        /// Valid content types are 0x14-0x17.  After ENCRYPT_OFF teardown
        /// the client sends plaintext TDS; when the feeder reads a non-TLS
        /// header it stores those bytes as <see cref="SqlSession.PlaintextLeftover"/>
        /// and exits so <see cref="RunTdsReadLoop"/> can consume them.
        /// </para>
        /// </summary>
        private void TlsRecordFeeder(NetworkConnection connection, SqlSession session) {
            var networkStream = connection._Stream;
            var wrapper = session.WrapperStream;
            try {
                while (!connection.Closed) {
                    // Read 5-byte TLS record header
                    byte[] recHdr = ReadBytesSync(networkStream, 5);
                    if (recHdr == null) {
                        break;
                    }

                    // Validate TLS content type (0x14=ChangeCipherSpec, 0x15=Alert,
                    // 0x16=Handshake, 0x17=ApplicationData).
                    // If the first byte is not a valid TLS content type, the client
                    // has switched to plaintext (ENCRYPT_OFF teardown).  Save the
                    // bytes we already read and exit.
                    byte contentType = recHdr[0];
                    if (contentType < 0x14 || contentType > 0x17) {
                        session.PlaintextLeftover = recHdr;
                        break;
                    }

                    ushort recLen = (ushort)((recHdr[3] << 8) | recHdr[4]);
                    byte[] recBody = null;
                    if (recLen > 0) {
                        recBody = ReadBytesSync(networkStream, recLen);
                        if (recBody == null) {
                            break;
                        }
                    }

                    // Feed the complete TLS record (header + body) to the wrapper
                    byte[] fullRecord = new byte[5 + recLen];
                    Buffer.BlockCopy(recHdr, 0, fullRecord, 0, 5);
                    if (recBody != null)
                        Buffer.BlockCopy(recBody, 0, fullRecord, 5, recLen);
                    wrapper.Feed(fullRecord);
                    connection.TrackBytesReceived(fullRecord.Length);
                }
            } catch (IOException) {
            } catch (ObjectDisposedException) {
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("[TlsRecordFeeder] Exception: " + ex.Message);
            } finally {
                wrapper.FeedClose();
            }
        }

        private Dictionary<Guid, (byte type, List<byte> data)> _multiPacketBuffer;

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes from the stream synchronously.
        /// Returns <see langword="null"/> if the stream ends prematurely.
        /// </summary>
        private static byte[] ReadBytesSync(Stream stream, int count) {
            byte[] buffer = new byte[count];
            int totalRead = 0;
            while (totalRead < count) {
                int read = stream.Read(buffer, totalRead, count - totalRead);
                if (read == 0) return null;
                totalRead += read;
            }
            return buffer;
        }

        private static string ExtractTdsClientIp(NetworkConnection connection) {
            try {
                return DataServer.NormalizeSqlLoginIpAddress(connection?.EndPoint?.Address?.ToString());
            } catch { }
            return "";
        }
        private void ProcessLogin(NetworkConnection connection, SqlSession session, byte[] data) {
            try {
                string username = ExtractLoginUsername(data);
                string password = ExtractLoginPassword(data);
                string database = ExtractLoginDatabase(data);

                if (data.Length >= 8) {
                    // Login7 TDSVersion is a DWORD at offset 4, written little-endian
                    // by the client.  BitConverter reads it in host (LE) order which
                    // matches the Motorola-order uint that WriteLoginAckToken emits.
                    uint tdsVer = BitConverter.ToUInt32(data, 4);
                    session.ClientTdsVersion = tdsVer;
                }

                string clientIp = ExtractTdsClientIp(connection);
                bool ipAllowed = _dataServer.IsSqlLoginIpAllowed(clientIp);
                bool authenticated = ipAllowed && _dataServer.Authenticate(username, password);

                if (authenticated) {
                    session.Username = username;
                    session.CurrentDatabase = !string.IsNullOrEmpty(database) ? database : _dataServer.DefaultDatabase;
                    session.IsAuthenticated = true;

                    // Extract client-requested packet size from Login7 (bytes 8-11, LE)
                    string packetSize = "4096";
                    if (data.Length >= 12) {
                        uint reqSize = BitConverter.ToUInt32(data, 8);
                        if (reqSize >= 512 && reqSize <= 32767)
                            packetSize = reqSize.ToString();
                    }

                    // Check if Login7 has FeatureExt data (OptionFlags3 bit 4)
                    bool hasFeatureExt = data.Length > 27 && (data[27] & 0x10) != 0;

                    // Parse and log feature extension data if present
                    List<byte> clientFeatureIds = new List<byte>();
                    if (hasFeatureExt) {
                        // Per [MS-TDS] 2.2.6.3 Login7, the fixed portion is 94 bytes.
                        // ibExtension is at offset 56 (USHORT) — byte offset within the
                        // Login7 packet to the Extension area.  The first DWORD at that
                        // position is ibFeatureExtLong — the byte offset from the start
                        // of the Login7 packet to the FeatureExt option list.
                        if (data.Length >= 94) {
                            ushort ibExtension = BitConverter.ToUInt16(data, 56);
                            ushort cbExtension = BitConverter.ToUInt16(data, 58);

                            // Read ibFeatureExtLong from the extension area
                            uint featureExtOffset = 0;
                            if (ibExtension > 0 && ibExtension + 4 <= data.Length) {
                                featureExtOffset = BitConverter.ToUInt32(data, ibExtension);
                            }

                            if (featureExtOffset > 0 && featureExtOffset < data.Length) {
                                int pos = (int)featureExtOffset;
                                while (pos < data.Length) {
                                    byte featureId = data[pos++];
                                    if (featureId == 0xFF) break; // Terminator
                                    if (pos + 4 > data.Length) break;
                                    uint featureDataLen = BitConverter.ToUInt32(data, pos);
                                    pos += 4;
                                    if (featureDataLen > (uint)(data.Length - pos)) break; // sanity check
                                    clientFeatureIds.Add(featureId);
                                    pos += (int)featureDataLen;
                                }
                            }
                        }
                    }

                    // Combine all login response tokens into a single TDS packet
                    using (var combinedMs = new MemoryStream()) {
                        using (var combinedWriter = new BinaryWriter(combinedMs)) {
                            WriteLoginAckToken(combinedWriter, connection, session);
                            WriteEnvChangeToken(combinedWriter, connection, 1, session.CurrentDatabase, "");
                            WriteEnvChangeToken(combinedWriter, connection, 2, "us_english", "");
                            WriteEnvChangeToken(combinedWriter, connection, 4, packetSize, "4096");

                            // ENVCHANGE type 7: SQL Collation (5 raw bytes, not Unicode B_VARCHAR)
                            // SQL_Latin1_General_CP1_CI_AS = LCID 0x0409 | flags 0x00D0 | SortId 0x34
                            WriteCollationEnvChange(combinedWriter);

                            WriteInfoToken(combinedWriter, connection, 5701, 2, "Changed database context to '" + session.CurrentDatabase + "'.");
                            WriteInfoToken(combinedWriter, connection, 5703, 1, "Changed language setting to us_english.");

                            // FEATUREEXTACK (0xAE) — only if the client sent FeatureExt data.
                            // Per [MS-TDS], the server MUST send this when OptionFlags3.fExtension is set
                            // and MUST NOT send it otherwise.
                            //
                            // SqlClient's OnFeatureExtAck is strict: each feature has specific
                            // DataLen/Data requirements, and any unrecognized feature ID causes a
                            // fatal parsing error.  We must only acknowledge features that SqlClient
                            // recognizes, with correct per-feature data formats.
                            //
                            // Feature requirements (from SqlClient source):
                            //   0x01 SRECOVERY       — only if _sessionRecoveryRequested (skip otherwise)
                            //   0x04 TCE             — DataLen>=1, Data[0]=version (1-3, NOT 0)
                            //   0x05 GLOBALTRANSACTIONS — DataLen>=1, Data[0]=0x00 or 0x01
                            //   0x09 DATACLASSIFICATION — DataLen==2, Data[0]=version, Data[1]=enabled
                            //   0x0A UTF8SUPPORT     — DataLen>=1
                            //   0x0B SQLDNSCACHING   — DataLen>=1, Data[0]=0x00 or 0x01
                            //   0x0D JSONSUPPORT     — DataLen==1, Data[0]=version (1+)
                            //   0x0E VECTORSUPPORT   — DataLen==1, Data[0]=version (1+)
                            //   default              — THROWS fatal error
                            if (hasFeatureExt) {
                                combinedWriter.Write((byte)0xAE);

                                foreach (byte featureId in clientFeatureIds) {
                                    switch (featureId) {
                                        case 0x02: // FEDAUTH (Federated Authentication) — skip
                                            // Skip: server doesn't support federated auth
                                            break;
                                        case 0x04: // Column encryption is not implemented.
                                            break;
                                        case 0x05: // GlobalTransactions — not enabled
                                            combinedWriter.Write(featureId);
                                            combinedWriter.Write((uint)1);
                                            combinedWriter.Write((byte)0x00);
                                            break;
                                        case 0x08: // AZURESQLSUPPORT — skip (not Azure)
                                            break;
                                        case 0x09: // DataClassification — version 1, not enabled
                                            combinedWriter.Write(featureId);
                                            combinedWriter.Write((uint)2);
                                            combinedWriter.Write((byte)0x01); // version
                                            combinedWriter.Write((byte)0x00); // not enabled
                                            break;
                                        case 0x0A: // UTF8Support — not supported
                                            combinedWriter.Write(featureId);
                                            combinedWriter.Write((uint)1);
                                            combinedWriter.Write((byte)0x00);
                                            break;
                                        case 0x0B: // SqlDNSCaching — not supported
                                            combinedWriter.Write(featureId);
                                            combinedWriter.Write((uint)1);
                                            combinedWriter.Write((byte)0x00);
                                            break;
                                        // Skip features we cannot safely acknowledge:
                                        //   0x01 SRECOVERY — throws if client didn't request it
                                        //   0x0D JSONSUPPORT — version must be 1+, skip to avoid issues
                                        //   0x0E VECTORSUPPORT — version must be 1+, skip to avoid issues
                                        //   0x0F ENHANCEDROUTINGSUPPORT — skip
                                        //   0x10 USERAGENT — safe to skip
                                        //   unknown IDs — MUST skip (default throws in SqlClient)
                                        default:
                                            break;
                                    }
                                }

                                combinedWriter.Write((byte)0xFF); // terminator
                            }

                            // DONE token with DONE_FINAL (0x00) to signal end of login response
                            WriteDoneToken(combinedWriter, connection, 0x00, 0, 0);
                        }
                        var loginResponseData = combinedMs.ToArray();

                        // Per [MS-TDS] 2.2.6.5, the login response MUST be sent
                        // over TLS even for ENCRYPT_OFF.  The client reads the
                        // response encrypted, then both sides tear down TLS.
                        // Send first, THEN clear IsTlsActive.
                        SendTdsPacket(connection, session, 0x04, loginResponseData);

                        // ENCRYPT_OFF teardown — now that the login response has
                        // been sent over TLS, clear IsTlsActive so subsequent
                        // packets go over plaintext.  Do NOT dispose SslStream —
                        // that would send a TLS close_notify alert and confuse
                        // the client.
                        if (session.NegotiatedEncryption == 0x00 && session.IsTlsActive) {
                            if (session.TlsStream != null) {
                                try { session.TlsStream.Flush(); } catch { }
                            }
                            session.IsTlsActive = false;
                        }
                    }
                } else {
                    string failureMessage = ipAllowed
                        ? "Login failed for user '" + username + "'."
                        : "SQL login from this IP address is not allowed.";
                    _dataServer.LogFormat("[{0}] {1} User='{2}' ClientIp='{3}'",
                        new[] { _dataServer.Name, failureMessage, username, clientIp });

                    // Per [MS-TDS] 2.2.6.5, the error response MUST be sent
                    // over TLS even for ENCRYPT_OFF.  Tear down after sending.
                    SendErrorResponse(connection, session, 18456, failureMessage);

                    if (session.NegotiatedEncryption == 0x00 && session.IsTlsActive) {
                        session.IsTlsActive = false;
                    }
                }
            } catch (Exception ex) {
                SendErrorResponse(connection, 0, "Login error: " + ex.Message);
            }
        }

        private void ProcessSqlBatch(NetworkConnection connection, SqlSession session, byte[] data) {
            try {
                if (!session.IsAuthenticated) {
                    SendErrorResponse(connection, 0, "Not authenticated");
                    return;
                }

                // SSMS and other TDS clients send an ALL_HEADERS prefix before
                // the SQL text.  The first 4 bytes are the total header length
                // (DWORD LE, including itself).  Skip past it to reach the SQL.
                int sqlOffset = 0;
                if (data.Length >= 4) {
                    uint totalHeaderLen = BitConverter.ToUInt32(data, 0);
                    if (totalHeaderLen >= 4 && totalHeaderLen <= (uint)data.Length)
                        sqlOffset = (int)totalHeaderLen;
                }

                string query = Encoding.Unicode.GetString(data, sqlOffset, data.Length - sqlOffset).TrimEnd('\0');

                SendBatchResults(connection, _dataServer.Execute(session, new SqlCommand(query)));
            } catch (Exception ex) {
                SendErrorResponse(connection, 0, "Query execution error: " + ex.Message);
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> if the SQL batch contains the given
        /// statement keyword at a line/statement boundary (not inside a string).
        /// This is a simple heuristic — it looks for the keyword at the start of
        /// any line or after a semicolon.
        /// </summary>
        private static bool ContainsStatement(string sql, string keyword) {
            if (sql.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
            // Look for keyword after newlines or semicolons
            int idx = 0;
            while (idx < sql.Length) {
                idx = sql.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                if (idx == 0) return true;
                char prev = sql[idx - 1];
                if (prev == '\n' || prev == '\r' || prev == ';' || prev == ' ' || prev == '\t' || prev == '(')
                    return true;
                idx += keyword.Length;
            }
            return false;
        }

        /// <summary>
        /// Detects SSMS's GetServerInformation query (which references
        /// SERVERPROPERTY) and returns populated result sets matching
        /// the multi-result structure SSMS expects. Returns <see langword="null"/>
        /// if the query is not a server-info query.
        /// </summary>
        private List<QueryResult> TryHandleServerInfoQuery(SqlSession session, string sql) {
            // SSMS's GetServerInformation query always references SERVERPROPERTY.
            if (sql.IndexOf("SERVERPROPERTY", StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            const byte S = 0xE7; // NVARCHAR
            const byte I = 0x38; // INT (sent as INTN 0x26 with length 4)

            var results = new List<QueryResult>();

            // Result Set 1: main SELECT with server properties
            var rs1 = new QueryResult { HasResultSet = true };
            rs1.Columns.AddRange(new[] { "DatabaseEngineType", "DatabaseEngineEdition", "ProductVersion",
                "MicrosoftVersion", "IsFabricServer", "Collation" });
            rs1.ColumnTypes = new List<byte> { I, I, S, I, I, S };
            rs1.Rows.Add(new object[] { 1, 2, session.ServerVersion ?? "17.0.4025.3", 285216697, 0,
                "SQL_Latin1_General_CP1_CI_AS" });
            results.Add(rs1);

            // Result Set 2: host_platform from sys.dm_os_host_info
            var rs2 = new QueryResult { HasResultSet = true };
            rs2.Columns.Add("host_platform");
            rs2.ColumnTypes = new List<byte> { S };
            rs2.Rows.Add(new object[] { "Windows" });
            results.Add(rs2);

            // Result Set 3: ConnectionProtocol
            var rs3 = new QueryResult { HasResultSet = true };
            rs3.Columns.Add("ConnectionProtocol");
            rs3.ColumnTypes = new List<byte> { S };
            rs3.Rows.Add(new object[] { "TCP" });
            results.Add(rs3);

            return results;
        }

        /// <summary>
        /// Handles TDS RPC requests (packet type 0x03).
        /// SSMS uses RPC for sp_executesql, sp_prepexec, and similar calls.
        /// The RPC payload format differs from SQL batch — it starts with
        /// ALL_HEADERS, then a procedure name or ID, followed by parameters.
        /// </summary>
        private void ProcessRpc(NetworkConnection connection, SqlSession session, byte[] data) {
            try {
                if (!session.IsAuthenticated) {
                    SendErrorResponse(connection, 0, "Not authenticated");
                    return;
                }

                // Skip ALL_HEADERS (first 4 bytes = total header length LE, including itself).
                int pos = 0;
                if (data.Length >= 4) {
                    uint totalHeaderLen = BitConverter.ToUInt32(data, 0);
                    if (totalHeaderLen >= 4 && totalHeaderLen <= (uint)data.Length)
                        pos = (int)totalHeaderLen;
                }

                // Read procedure name or special procedure ID.
                // First USHORT: NameLenProcID. If 0xFFFF, the next USHORT is a
                // well-known procedure ID. Otherwise it's the char-count of the
                // procedure name in Unicode.
                string procName = null;
                ushort rpcProcId = 0;
                if (pos + 2 <= data.Length) {
                    ushort nameLen = BitConverter.ToUInt16(data, pos);
                    pos += 2;
                    if (nameLen == 0xFFFF) {
                        // Well-known stored procedure by ID
                        if (pos + 2 <= data.Length) {
                            rpcProcId = BitConverter.ToUInt16(data, pos);
                            pos += 2;
                        }
                    } else if (nameLen > 0 && pos + nameLen * 2 <= data.Length) {
                        procName = Encoding.Unicode.GetString(data, pos, nameLen * 2);
                        pos += nameLen * 2;
                    }
                }

                if (rpcProcId != 10 && !string.Equals(procName, "sp_executesql", StringComparison.OrdinalIgnoreCase))
                    throw new SqlExecutionException("Unsupported RPC: " + (procName ?? rpcProcId.ToString()), "0A000", 2812);
                var arguments = new List<KeyValuePair<string, object>>();
                using (var input = new MemoryStream(data)) using (var reader = new BinaryReader(input)) {
                    input.Position = pos;
                    reader.ReadUInt16(); // OptionFlags
                    while (input.Position < input.Length) {
                        int length = reader.ReadByte();
                        string name = Encoding.Unicode.GetString(reader.ReadBytes(length * 2));
                        byte flags = reader.ReadByte();
                        if ((flags & 1) != 0) throw new SqlExecutionException("Output RPC parameters are not implemented.", "0A000");
                        var type = TdsValueCodec.ReadType(reader);
                        arguments.Add(new KeyValuePair<string, object>(name, TdsValueCodec.ReadValue(reader, type)));
                    }
                }
                if (arguments.Count < 1 || !(arguments[0].Value is string sql)) throw new InvalidDataException("sp_executesql requires SQL text.");
                var command = new SqlCommand(sql);
                for (int i = 2; i < arguments.Count; i++) command.Parameters.Add(arguments[i].Key, arguments[i].Value);
                SendBatchResults(connection, _dataServer.Execute(session, command));
            } catch (Exception ex) {
                SendErrorResponse(connection, 0, "RPC execution error: " + ex.Message);
            }
        }

        /// <summary>
        /// Attempts to extract the first NVARCHAR/NTEXT parameter value from
        /// the RPC parameter data starting at <paramref name="pos"/>.
        /// Returns <see langword="null"/> if extraction fails.
        /// </summary>
        private static string TryExtractFirstNvarcharParam(byte[] data, int pos) {
            try {
                // Skip OptionFlags (USHORT) after the procedure identifier.
                if (pos + 2 > data.Length) return null;
                pos += 2; // OptionFlags

                // First parameter: B_VARCHAR name (1-byte char-count + Unicode),
                // then StatusFlags (1 byte), then TYPE_INFO, then value.
                if (pos >= data.Length) return null;
                byte nameCharCount = data[pos++];
                pos += nameCharCount * 2; // skip parameter name

                if (pos >= data.Length) return null;
                pos++; // StatusFlags

                // TYPE_INFO: first byte is the type token.
                if (pos >= data.Length) return null;
                byte typeToken = data[pos++];

                // NVARCHARTYPE (0xE7) or NTEXTTYPE (0x63)
                if (typeToken == 0xE7) {
                    // NVARCHAR: USHORT maxLen + 5-byte collation
                    if (pos + 7 > data.Length) return null;
                    pos += 2 + 5; // maxLen + collation

                    // Value: USHORT actual byte length, then data
                    if (pos + 2 > data.Length) return null;
                    ushort valLen = BitConverter.ToUInt16(data, pos);
                    pos += 2;
                    if (valLen == 0xFFFF) return null; // NULL
                    if (pos + valLen > data.Length) return null;
                    return Encoding.Unicode.GetString(data, pos, valLen);
                } else if (typeToken == 0x63) {
                    // NTEXT: 4-byte textPtr size, 5-byte collation, 4-byte maxLen
                    if (pos + 13 > data.Length) return null;
                    pos += 4 + 5 + 4; // skip type info

                    // Value: 4-byte actual byte length, then data
                    if (pos + 4 > data.Length) return null;
                    int valLen = BitConverter.ToInt32(data, pos);
                    pos += 4;
                    if (valLen <= 0 || pos + valLen > data.Length) return null;
                    return Encoding.Unicode.GetString(data, pos, valLen);
                }
            } catch { }
            return null;
        }

        #endregion

        #region TDS Response Methods

        private void SendLoginAck(NetworkConnection connection, SqlSession session) {
            WriteLoginAckToken(null, connection, session);
        }

        private void WriteLoginAckToken(BinaryWriter targetWriter, NetworkConnection connection, SqlSession session) {
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    writer.Write((byte)0xAD);

                    // Per [MS-TDS] 2.2.7.13, ProgName is the product name, not the machine name.
                    var progNameBytes = Encoding.Unicode.GetBytes("Microsoft SQL Server");
                    ushort tokenLength = (ushort)(1 + 4 + 1 + progNameBytes.Length + 4);
                    writer.Write(tokenLength);

                    writer.Write((byte)0x01);
                    // TDS version — written as big-endian per [MS-TDS] 2.2.7.13.
                    // Echo the client's version so the handshake matches.
                    uint tdsVer = session.ClientTdsVersion;
                    writer.Write((byte)((tdsVer >> 24) & 0xFF));
                    writer.Write((byte)((tdsVer >> 16) & 0xFF));
                    writer.Write((byte)((tdsVer >> 8) & 0xFF));
                    writer.Write((byte)(tdsVer & 0xFF));

                    writer.Write((byte)(progNameBytes.Length / 2));
                    writer.Write(progNameBytes);

                    // Major.Minor as single bytes, then BuildNumber as two BE bytes
                    writer.Write((byte)17);
                    writer.Write((byte)0);
                    writer.Write((byte)(4025 >> 8));
                    writer.Write((byte)(4025 & 0xFF));

                    if (targetWriter != null)
                        targetWriter.Write(ms.ToArray());
                    else
                        SendTdsPacket(connection, session, 0x04, ms.ToArray());
                }
            }
        }

        private void SendEnvChange(NetworkConnection connection, byte changeType, string newValue, string oldValue) {
            WriteEnvChangeToken(null, connection, changeType, newValue, oldValue);
        }

        private void WriteEnvChangeToken(BinaryWriter targetWriter, NetworkConnection connection, byte changeType, string newValue, string oldValue) {
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    writer.Write((byte)0xE3);

                    var newBytes = Encoding.Unicode.GetBytes(newValue);
                    var oldBytes = Encoding.Unicode.GetBytes(oldValue);

                    ushort tokenLength = (ushort)(1 + 1 + newBytes.Length + 1 + oldBytes.Length);
                    writer.Write(tokenLength);

                    writer.Write(changeType);
                    writer.Write((byte)(newBytes.Length / 2));
                    writer.Write(newBytes);
                    writer.Write((byte)(oldBytes.Length / 2));
                    writer.Write(oldBytes);

                    if (targetWriter != null)
                        targetWriter.Write(ms.ToArray());
                    else
                        SendTdsPacket(connection, null, 0x04, ms.ToArray());
                }
            }
        }

        /// <summary>
        /// Writes an ENVCHANGE type 7 (SQL Collation) token.
        /// Unlike other ENVCHANGE types, the collation payload uses raw bytes
        /// (byte-count + octets) rather than Unicode B_VARCHAR.
        /// Emits SQL_Latin1_General_CP1_CI_AS for both new and old values.
        /// </summary>
        private void WriteCollationEnvChange(BinaryWriter targetWriter) {
            // Collation: 5 bytes = LCID (3 bytes LE) + SortFlags (1 byte) + SortId (1 byte)
            // SQL_Latin1_General_CP1_CI_AS: LCID 0x0409, flags 0xD0, SortId 0x34
            byte[] collation = { 0x09, 0x04, 0x00, 0xD0, 0x34 };

            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    writer.Write((byte)0xE3); // ENVCHANGE token
                    // tokenLength = type(1) + newLen(1) + newData(5) + oldLen(1) + oldData(5) = 13
                    writer.Write((ushort)13);
                    writer.Write((byte)7);    // type 7 = SQL Collation
                    writer.Write((byte)5);    // new collation length
                    writer.Write(collation);
                    writer.Write((byte)5);    // old collation length
                    writer.Write(collation);

                    if (targetWriter != null)
                        targetWriter.Write(ms.ToArray());
                }
            }
        }

        private void SendInfo(NetworkConnection connection, string message) {
            WriteInfoToken(null, connection, 0, 0, message);
        }

        private void WriteInfoToken(BinaryWriter targetWriter, NetworkConnection connection, int infoNumber, byte state, string message) {
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    writer.Write((byte)0xAB);

                    var msgBytes = Encoding.Unicode.GetBytes(message);
                    var serverNameBytes = Encoding.Unicode.GetBytes(_dataServer.ServerName ?? "");
                    ushort tokenLength = (ushort)(4 + 1 + 1 + 2 + msgBytes.Length + 1 + serverNameBytes.Length + 1 + 4);
                    writer.Write(tokenLength);

                    writer.Write((uint)infoNumber);
                    writer.Write(state);
                    writer.Write((byte)10);
                    writer.Write((ushort)(msgBytes.Length / 2));
                    writer.Write(msgBytes);
                    writer.Write((byte)(serverNameBytes.Length / 2));
                    if (serverNameBytes.Length > 0)
                        writer.Write(serverNameBytes);
                    writer.Write((byte)0); // ProcName length
                    writer.Write((uint)0); // LineNumber

                    if (targetWriter != null)
                        targetWriter.Write(ms.ToArray());
                    else
                        SendTdsPacket(connection, null, 0x04, ms.ToArray());
                }
            }
        }

        private void SendDone(NetworkConnection connection, ushort status, ushort curCmd, long rowCount) {
            WriteDoneToken(null, connection, status, curCmd, rowCount);
        }

        private void WriteDoneToken(BinaryWriter targetWriter, NetworkConnection connection, ushort status, ushort curCmd, long rowCount) {
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    writer.Write((byte)0xFD);
                    writer.Write(status);
                    writer.Write(curCmd);
                    writer.Write(rowCount);

                    if (targetWriter != null)
                        targetWriter.Write(ms.ToArray());
                    else
                        SendTdsPacket(connection, null, 0x04, ms.ToArray());
                }
            }
        }

        internal void SendErrorResponse(NetworkConnection connection, SqlSession session, int errorNumber, string message) {
            // Combine ERROR + DONE into a single TDS packet so the client
            // receives a complete message (EOM) containing both tokens.
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    // ERROR token
                    writer.Write((byte)0xAA);

                    var msgBytes = Encoding.Unicode.GetBytes(message);
                    ushort tokenLength = (ushort)(4 + 1 + 1 + 2 + msgBytes.Length + 1 + 1 + 4);
                    writer.Write(tokenLength);

                    writer.Write((uint)errorNumber);
                    writer.Write((byte)1);
                    writer.Write((byte)14);
                    writer.Write((ushort)(msgBytes.Length / 2));
                    writer.Write(msgBytes);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((uint)0);

                    // DONE token with DONE_ERROR (0x0002) flag
                    WriteDoneToken(writer, connection, 0x0002, 0, 0);

                    if (session != null)
                        SendTdsPacket(connection, session, 0x04, ms.ToArray());
                    else
                        SendTdsPacket(connection, null, 0x04, ms.ToArray());
                }
            }
        }

        internal void SendErrorResponse(NetworkConnection connection, int errorNumber, string message) {
            SendErrorResponse(connection, null, errorNumber, message);
        }

        private void SendResultSet(NetworkConnection connection, QueryResult result) {
            // Combine COLMETADATA + all ROW tokens + DONE into a single TDS
            // response message so the client receives the complete result set
            // in one EOM-marked packet.  Sending them as separate EOM packets
            // causes SSMS to treat each as a complete message and hang/crash.
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    WriteResultSetTokens(writer, result);

                    // --- DONE token (0xFD) with DONE_COUNT ---
                    writer.Write((byte)0xFD);
                    writer.Write((ushort)0x0010);        // DONE_COUNT
                    writer.Write((ushort)0x00C1);        // curCmd = SELECT (0x00C1)
                    writer.Write((long)result.Rows.Count);
                }

                SendTdsPacket(connection, null, 0x04, ms.ToArray());
            }
        }

        private void SendBatchResults(NetworkConnection connection, IReadOnlyList<QueryResult> results) {
            using (var buffer = new MemoryStream()) {
                using (var writer = new BinaryWriter(buffer)) {
                    for (int i = 0; i < results.Count; i++) {
                        var result = results[i];
                        if (result.HasResultSet) WriteResultSetTokens(writer, result);
                        WriteDoneToken(writer, connection, (ushort)(0x10 | (i + 1 < results.Count ? 1 : 0)), 0, result.RowsAffected);
                    }
                    SendTdsPacket(connection, null, 0x04, buffer.ToArray());
                }
            }
        }

        /// <summary>
        /// Sends multiple result sets in a single TDS message (single EOM packet).
        /// For each result set, writes COLMETADATA + ROW(s) followed by
        /// DONE (0xFD) with DONE_MORE — except the last which gets DONE (0xFD) without DONE_MORE.
        /// Per [MS-TDS], SQL batch results use DONE (0xFD); DONEINPROC (0xFF) is only for stored procedures.
        /// </summary>
        private void SendMultiResultSet(NetworkConnection connection, List<QueryResult> results) {
            using (var ms = new MemoryStream()) {
                using (var writer = new BinaryWriter(ms)) {
                    for (int r = 0; r < results.Count; r++) {
                        var result = results[r];
                        WriteResultSetTokens(writer, result);

                        // Between result sets, use DONE (0xFD) with DONE_MORE (0x0001) + DONE_COUNT (0x0010).
                        // After the last result set, use DONE (0xFD) with DONE_COUNT only (no DONE_MORE = final).
                        // DONEINPROC (0xFF) is only for results inside stored procedures, not SQL batches.
                        if (r < results.Count - 1) {
                            writer.Write((byte)0xFD);                // DONE (batch-level)
                            writer.Write((ushort)0x0011);            // DONE_MORE | DONE_COUNT
                            writer.Write((ushort)0x00C1);            // curCmd = SELECT
                            writer.Write((long)result.Rows.Count);
                        } else {
                            writer.Write((byte)0xFD);                // DONE (final)
                            writer.Write((ushort)0x0010);            // DONE_COUNT
                            writer.Write((ushort)0x00C1);            // curCmd = SELECT
                            writer.Write((long)result.Rows.Count);
                        }
                    }
                }

                var responseData = ms.ToArray();

                SendTdsPacket(connection, null, 0x04, responseData);
            }
        }

        /// <summary>
        /// Writes COLMETADATA + ROW tokens for a single result set to a BinaryWriter.
        /// Does NOT write a DONE token — the caller is responsible for that.
        /// </summary>
        private void WriteResultSetTokens(BinaryWriter writer, QueryResult result) {
            TdsValueCodec.WriteResult(writer, result);
        }

        /// <summary>
        /// Returns the appropriate stream for TDS I/O on this connection.
        /// When TLS is active, returns the encrypted <see cref="SslStream"/>;
        /// otherwise returns the raw <see cref="NetworkConnection._Stream"/>.
        /// </summary>
        private Stream GetTdsStream(NetworkConnection connection, SqlSession session) {
            if (session != null && session.IsTlsActive && session.TlsStream != null)
                return session.TlsStream;
            return connection._Stream;
        }

        /// <summary>
        /// Returns the appropriate stream for TDS I/O, looking up the session
        /// from the DataServer's session dictionary.
        /// </summary>
        private Stream GetTdsStream(NetworkConnection connection) {
            if (_dataServer.Sessions.TryGetValue(connection.ID, out var session))
                return GetTdsStream(connection, session);
            return connection._Stream;
        }

        internal void SendTdsPacket(NetworkConnection connection, SqlSession session, byte packetType, byte[] data) {
            var stream = session != null ? GetTdsStream(connection, session) : GetTdsStream(connection);
            if (stream == null) throw new IOException("TDS connection has no stream.");
            const int capacity = 4088;
            lock (connection.SendQueueRaw) {
                int offset = 0; byte packetId = 1;
                do {
                    int count = Math.Min(capacity, data.Length - offset);
                    int size = count + 8;
                    ushort spid = (ushort)((connection.ID.GetHashCode() & 0x7FFF) | 1);
                    byte[] header = { packetType, (byte)(offset + count == data.Length ? 1 : 0), (byte)(size >> 8), (byte)size, (byte)(spid >> 8), (byte)spid, packetId++, 0 };
                    var packet = new byte[size];
                    Buffer.BlockCopy(header, 0, packet, 0, 8);
                    Buffer.BlockCopy(data, offset, packet, 8, count);
                    stream.Write(packet, 0, packet.Length);
                    connection.TrackBytesSent(size);
                    offset += count;
                } while (offset < data.Length);
                stream.Flush();
            }
        }

        private string ExtractLoginUsername(byte[] data) {
            try {
                if (data.Length < 100) return _dataServer.Username;

                // MS-TDS Login7: ibHostName at offset 36, cchHostName at offset 38
                int hostOffset = BitConverter.ToUInt16(data, 36);
                int hostLength = BitConverter.ToUInt16(data, 38);

                // MS-TDS Login7: ibUserName at offset 40, cchUserName at offset 42
                int offset = BitConverter.ToUInt16(data, 40);
                int length = BitConverter.ToUInt16(data, 42);

                if (offset > 0 && length > 0 && offset + length * 2 <= data.Length) {
                    return Encoding.Unicode.GetString(data, offset, length * 2);
                }
            } catch { }
            return _dataServer.Username;
        }

        private string ExtractLoginPassword(byte[] data) {
            try {
                if (data.Length < 100) return _dataServer.Password;
                // MS-TDS Login7: ibPassword at offset 44, cchPassword at offset 46
                int offset = BitConverter.ToUInt16(data, 44);
                int length = BitConverter.ToUInt16(data, 46);
                if (offset > 0 && length > 0 && offset + length * 2 <= data.Length) {
                    byte[] encrypted = new byte[length * 2];
                    Array.Copy(data, offset, encrypted, 0, length * 2);
                    // TDS password decryption: XOR with 0xA5 first, then swap nibbles
                    // (reverse of encryption which swaps nibbles first, then XORs with 0xA5)
                    for (int i = 0; i < encrypted.Length; i++) {
                        byte b = (byte)(encrypted[i] ^ 0xA5); // XOR first
                        encrypted[i] = (byte)((b >> 4) | (b << 4)); // then swap nibbles
                    }
                    return Encoding.Unicode.GetString(encrypted);
                }
            } catch { }
            return _dataServer.Password;
        }

        private string ExtractLoginDatabase(byte[] data) {
            try {
                if (data.Length < 100) return _dataServer.DefaultDatabase;
                // MS-TDS Login7: ibDatabase at offset 68, cchDatabase at offset 70
                int offset = BitConverter.ToUInt16(data, 68);
                int length = BitConverter.ToUInt16(data, 70);
                if (offset > 0 && length > 0 && offset + length * 2 <= data.Length)
                    return Encoding.Unicode.GetString(data, offset, length * 2);
            } catch { }
            return _dataServer.DefaultDatabase;
        }

        #endregion

        #region Standalone TDS Loop

        /// <summary>
        /// Runs the TDS read loop for a standalone <see cref="DataServer"/> connection.
        /// Called from <see cref="DataServer.DataServer_ClientConnected"/>.
        /// After a TLS handshake (triggered by Pre-Login), the loop automatically
        /// switches to reading from the encrypted <see cref="SslStream"/>.
        /// For <c>ENCRYPT_OFF</c> (login-only), it reverts to plaintext after login.
        /// </summary>
        internal static async void RunTdsLoop(DataServer server, NetworkConnection connection, SqlSession session) {
            var handler = new TdsProtocolHandler(server);
            List<byte> multiPacketData = null;
            byte multiPacketType = 0;
            connection._Protocol = TcpProtocol.Tds;
            connection.RawTcpMode = true;
            connection.SuppressConnectionTest = true;

            Thread feederThread = null;
            bool feederActive = false;

            Stream leftoverStream = null;
            try {
                while (!connection.Closed) {
                    Stream activeStream;
                    if (leftoverStream != null) {
                        activeStream = leftoverStream;
                        leftoverStream = null;
                    } else if (feederActive && session.TlsStream != null) {
                        activeStream = session.TlsStream;
                    } else if (session.IsTlsActive && session.TlsStream != null) {
                        activeStream = session.TlsStream;
                    } else {
                        activeStream = connection._Stream;
                    }

                    // Wrap reads in try-catch so that IOException from SslStream
                    // during ENCRYPT_OFF feed-buffer closure doesn't skip the
                    // PlaintextLeftover transition.
                    byte[] header;
                    try {
                        header = await ReadBytesAsync(activeStream, 8);
                    } catch (IOException) when (feederActive) {
                        header = null;
                    } catch (ObjectDisposedException) when (feederActive) {
                        header = null;
                    }

                    if (header == null || header.Length < 8) {
                        if (feederActive) {
                            if (feederThread != null && feederThread.IsAlive)
                                feederThread.Join(2000);
                            feederActive = false;

                            if (session.PlaintextLeftover != null) {
                                var lo = session.PlaintextLeftover;
                                session.PlaintextLeftover = null;
                                leftoverStream = new ConcatenatedStream(
                                    new MemoryStream(lo, false), connection._Stream);
                                continue;
                            }
                            if (!connection.Closed) {
                                continue;
                            }
                        }
                        break;
                    }
                    connection.TrackBytesReceived(8);

                    byte packetType = header[0];
                    byte status = header[1];
                    ushort length = (ushort)((header[2] << 8) | header[3]);

                    if (length < 8) break;

                    byte[] data;
                    try {
                        data = await ReadBytesAsync(activeStream, length - 8);
                    } catch (IOException) when (feederActive) {
                        data = null;
                    } catch (ObjectDisposedException) when (feederActive) {
                        data = null;
                    }

                    if (data == null) {
                        if (feederActive) {
                            if (feederThread != null && feederThread.IsAlive)
                                feederThread.Join(2000);
                            feederActive = false;

                            if (session.PlaintextLeftover != null) {
                                var lo = session.PlaintextLeftover;
                                session.PlaintextLeftover = null;
                                leftoverStream = new ConcatenatedStream(
                                    new MemoryStream(lo, false), connection._Stream);
                                continue;
                            }
                            if (!connection.Closed) {
                                continue;
                            }
                        }
                        break;
                    }
                    connection.TrackBytesReceived(data.Length);

                    if ((status & 0x01) == 0) {
                        if (multiPacketData == null) {
                            multiPacketData = new List<byte>(data);
                            multiPacketType = packetType;
                        } else {
                            multiPacketData.AddRange(data);
                        }
                        continue;
                    }

                    if (multiPacketData != null) {
                        multiPacketData.AddRange(data);
                        data = multiPacketData.ToArray();
                        packetType = multiPacketType;
                        multiPacketData = null;
                    }

                    handler.HandleTdsPacket(connection, session, packetType, data);

                    // After Pre-Login processing, TLS may have been set up.
                    // Start the feed-buffer feeder for ENCRYPT_OFF so that
                    // SslStream can decrypt without reading directly from
                    // NetworkStream (which would cause read-ahead issues
                    // during the plaintext transition after login).
                    if (!feederActive && session.WrapperStream != null && session.WrapperStream.UseFeedBuffer) {
                        feederActive = true;
                        feederThread = new Thread(() => {
                            var networkStream = connection._Stream;
                            var wrapper = session.WrapperStream;
                            try {
                                while (!connection.Closed) {
                                    byte[] recHdr = ReadBytesSync(networkStream, 5);
                                    if (recHdr == null) break;
                                    byte ct = recHdr[0];
                                    if (ct < 0x14 || ct > 0x17) {
                                        session.PlaintextLeftover = recHdr;
                                        break;
                                    }
                                    ushort recLen = (ushort)((recHdr[3] << 8) | recHdr[4]);
                                    byte[] recBody = recLen > 0 ? ReadBytesSync(networkStream, recLen) : null;
                                    if (recLen > 0 && recBody == null) break;
                                    byte[] full = new byte[5 + recLen];
                                    Buffer.BlockCopy(recHdr, 0, full, 0, 5);
                                    if (recBody != null) Buffer.BlockCopy(recBody, 0, full, 5, recLen);
                                    wrapper.Feed(full);
                                    connection.TrackBytesReceived(full.Length);
                                }
                            } catch { }
                            finally { wrapper.FeedClose(); }
                        }) { IsBackground = true, Name = "TDS-TlsFeeder-SA-" + connection.ID.ToString("N").Substring(0, 8) };
                        feederThread.Start();
                    }
                }
            } catch (Exception ex) {
                server.LogFormat("[{0}] RunTdsLoop exception: {1}", new[] { server.Name, ex.Message });
            } finally {
                if (session.WrapperStream != null)
                    session.WrapperStream.FeedClose();
                if (feederThread != null && feederThread.IsAlive)
                    feederThread.Join(2000);
            }
        }

        private static async Task<byte[]> ReadBytesAsync(Stream stream, int count) {
            byte[] buffer = new byte[count];
            int totalRead = 0;
            while (totalRead < count) {
                int read = await stream.ReadAsync(buffer, totalRead, count - totalRead);
                if (read == 0) return null;
                totalRead += read;
            }
            return buffer;
        }

        #endregion
    }
}
