using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace SocketJack.Net {
    // One instance per sequential socket loop. Reuse completion state without allocating a Task per datagram.
    internal sealed class UdpReliableSocketOperation : SocketAsyncEventArgs, IValueTaskSource<int> {
        ManualResetValueTaskSourceCore<int> completion;
        internal UdpReliableSocketOperation(byte[] buffer, int size) {
            SetBuffer(buffer, 0, size);
            // Continue the bounded socket loop directly from I/O completion; application callbacks have separate workers.
            completion.RunContinuationsAsynchronously = false;
            Completed += (_, __) => completion.SetResult(BytesTransferred);
        }
        internal ValueTask<int> Receive(Socket socket) {
            completion.Reset(); RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
            return socket.ReceiveFromAsync(this) ? new ValueTask<int>(this, completion.Version) : new ValueTask<int>(CheckResult());
        }
        internal ValueTask<int> Send(Socket socket, IPEndPoint endpoint, int length) {
            completion.Reset(); RemoteEndPoint = endpoint; SetBuffer(0, length);
            return socket.SendToAsync(this) ? new ValueTask<int>(this, completion.Version) : new ValueTask<int>(CheckResult());
        }
        int CheckResult() {
            if (SocketError != SocketError.Success) throw new SocketException((int)SocketError);
            return BytesTransferred;
        }
        int IValueTaskSource<int>.GetResult(short token) { completion.GetResult(token); return CheckResult(); }
        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => completion.GetStatus(token);
        void IValueTaskSource<int>.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            => completion.OnCompleted(continuation, state, token, flags);
    }
}
