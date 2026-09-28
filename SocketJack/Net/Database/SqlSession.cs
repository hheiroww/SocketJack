using System;
using System.Net.Security;

namespace SocketJack.Net.Database {

    public class SqlSession {
        public Guid ConnectionId { get; set; }
        public string Username { get; set; }
        public string CurrentDatabase { get; set; }
        public string ServerName { get; set; }
        public string ServerVersion { get; set; }
        public bool IsAuthenticated { get; set; }
        internal ManagedTransaction Transaction { get; set; }

        /// <summary>
        /// The TDS protocol version sent by the client in Login7 (bytes 4-7, big-endian).
        /// Stored as raw big-endian bytes so the LOGINACK can echo them exactly.
        /// Defaults to TDS 7.4 (0x74000004).
        /// </summary>
        internal uint ClientTdsVersion { get; set; } = 0x74000004;

        /// <summary>
        /// The negotiated TDS encryption mode for this connection.
        /// <c>0x00</c> = login-only, <c>0x01</c>/<c>0x03</c> = full, <c>0x02</c> = none.
        /// </summary>
        internal byte NegotiatedEncryption { get; set; } = 0x02;

        /// <summary>
        /// The <see cref="TdsWrapperStream"/> that sits between the
        /// <see cref="SslStream"/> and the raw TCP stream.
        /// Used to feed raw TLS record bytes from <see cref="TdsProtocolHandler.ProcessReceive"/>.
        /// </summary>
        internal TdsWrapperStream WrapperStream { get; set; }

        /// <summary>
        /// The TLS stream wrapping the raw TCP stream for this connection.
        /// <see langword="null"/> when encryption is not active.
        /// </summary>
        internal SslStream TlsStream { get; set; }

        /// <summary>
        /// When <see langword="true"/>, the TLS handshake has completed and
        /// <see cref="TlsStream"/> is ready for I/O.
        /// </summary>
        internal bool IsTlsActive { get; set; }

        /// <summary>
        /// Bytes read from the NetworkStream by the TLS record feeder that
        /// turned out to be plaintext (after ENCRYPT_OFF teardown).  The
        /// read loop prepends these before reading from the NetworkStream.
        /// </summary>
        internal byte[] PlaintextLeftover { get; set; }
    }
}
