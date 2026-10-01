using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    public class TcpDuplicator : IDisposable {

        // This class is basically a reverse VPN.

        #region Fields

        private readonly string _remoteHost;
        private readonly int _remotePort;
        private readonly int _localPort;
        private readonly IPAddress _listenAddress;
        private System.Net.Sockets.TcpListener _listener;
        private readonly List<ForwardingSession> _sessions = new List<ForwardingSession>();
        private bool _isRunning = false;
        private bool _isDisposed = false;
        private CancellationTokenSource _cts;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new TcpDuplicator that listens locally and forwards to a remote server.
        /// </summary>
        /// <param name="remoteHost">The remote host to forward connections to.</param>
        /// <param name="remotePort">The remote port to forward connections to.</param>
        /// <param name="localPort">The local port to listen on.</param>
        public TcpDuplicator(string remoteHost, int remotePort, int localPort) {
            _remoteHost = remoteHost;
            _remotePort = remotePort;
            _localPort = localPort;
            _listenAddress = IPAddress.Loopback;
        }

        /// <summary>
        /// Creates a new TcpDuplicator that listens on the given local address and forwards to a remote server.
        /// </summary>
        /// <param name="remoteHost">The remote host to forward connections to.</param>
        /// <param name="remotePort">The remote port to forward connections to.</param>
        /// <param name="localPort">The local port to listen on.</param>
        /// <param name="listenAddress">The local address to bind. Defaults to loopback when null.</param>
        public TcpDuplicator(string remoteHost, int remotePort, int localPort, IPAddress listenAddress) {
            _remoteHost = remoteHost;
            _remotePort = remotePort;
            _localPort = localPort;
            _listenAddress = listenAddress ?? IPAddress.Loopback;
        }

        #endregion

        #region Public Methods

        public bool IsRunning {
            get { return _isRunning; }
        }

        /// <summary>
        /// Starts listening on the local port and forwarding connections to the remote server.
        /// </summary>
        /// <returns>True if successfully started; false otherwise.</returns>
        public bool Start()
        {
            if (_isDisposed)
                throw new ObjectDisposedException("TcpDuplicator is disposed.");
            if (_isRunning)
                return false;

            try
            {
                _listener = new System.Net.Sockets.TcpListener(_listenAddress, _localPort);
                _listener.Start();
                _cts = new CancellationTokenSource();
                _isRunning = true;
                _ = AcceptLoopAsync(_cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to start TcpDuplicator on port {_localPort}.", ex);
            }
        }

        /// <summary>
        /// Stops listening and closes all forwarding sessions.
        /// </summary>
        public void Stop()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            _cts?.Cancel();
            _listener?.Stop();

            lock (_sessions)
            {
                foreach (var session in _sessions)
                {
                    session.Close();
                }
                _sessions.Clear();
            }
        }

        #endregion

        #region Private Methods

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    System.Net.Sockets.TcpClient localClient = await _listener.AcceptTcpClientAsync();
                    ConfigureStreamingSocket(localClient, GetForwardingLoad());

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            System.Net.Sockets.TcpClient remoteClient = new System.Net.Sockets.TcpClient();
                            ConfigureStreamingSocket(remoteClient, GetForwardingLoad());
                            await remoteClient.ConnectAsync(_remoteHost, _remotePort);

                            var session = new ForwardingSession(localClient, remoteClient, OnSessionClosed, GetForwardingLoad);
                            lock (_sessions)
                            {
                                _sessions.Add(session);
                            }
                            session.Start();
                        }
                        catch
                        {
                            localClient.Dispose();
                        }
                    }, cancellationToken);
                }
            }
            catch (ObjectDisposedException)
            {
                // Expected when listener is stopped
            }
            catch (Exception)
            {
                // Log or handle other exceptions
            }
        }

        private int GetForwardingLoad()
        {
            lock (_sessions)
            {
                return _sessions.Count + 1;
            }
        }

        private void OnSessionClosed(ForwardingSession session)
        {
            lock (_sessions)
            {
                _sessions.Remove(session);
            }
        }

        private static void ConfigureStreamingSocket(System.Net.Sockets.TcpClient client, int activeOrQueuedSessions)
        {
            TcpForwardingBufferProfile.ConfigureStreamingSocket(client, activeOrQueuedSessions);
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            Stop();
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }

        #endregion

        #region ForwardingSession

        /// <summary>
        /// Manages bidirectional forwarding between a local client and remote server.
        /// </summary>
        private class ForwardingSession
        {
            private readonly System.Net.Sockets.TcpClient _localClient;
            private readonly System.Net.Sockets.TcpClient _remoteClient;
            private readonly Action<ForwardingSession> _onClose;
            private readonly Func<int> _getForwardingLoad;
            private bool _isClosed = false;

            public ForwardingSession(System.Net.Sockets.TcpClient localClient, System.Net.Sockets.TcpClient remoteClient, Action<ForwardingSession> onClose, Func<int> getForwardingLoad)
            {
                _localClient = localClient;
                _remoteClient = remoteClient;
                _onClose = onClose;
                _getForwardingLoad = getForwardingLoad;
            }

            public void Start()
            {
                Task.Run(() => ForwardAsync(_localClient.GetStream(), _remoteClient.GetStream()));
                Task.Run(() => ForwardAsync(_remoteClient.GetStream(), _localClient.GetStream()));
            }

            public void Close()
            {
                if (_isClosed)
                    return;
                _isClosed = true;
                try { _localClient.Dispose(); } catch { }
                try { _remoteClient.Dispose(); } catch { }
                _onClose?.Invoke(this);
            }

            private async Task ForwardAsync(System.Net.Sockets.NetworkStream from, System.Net.Sockets.NetworkStream to)
            {
                byte[] buffer = TcpForwardingBufferProfile.RentCopyBuffer(_getForwardingLoad());
                try
                {
                    while (!_isClosed)
                    {
                        int desiredSize = TcpForwardingBufferProfile.GetCopyBufferSize(_getForwardingLoad());
                        if (buffer.Length < desiredSize)
                        {
                            TcpForwardingBufferProfile.ReturnCopyBuffer(buffer);
                            buffer = TcpForwardingBufferProfile.RentCopyBuffer(_getForwardingLoad());
                        }

                        int bytesRead = await from.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead <= 0)
                            break;
                        await to.WriteAsync(buffer, 0, bytesRead);
                        await to.FlushAsync();
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    TcpForwardingBufferProfile.ReturnCopyBuffer(buffer);
                    Close();
                }
            }
        }

        #endregion
    }
}
