using System;
using System.Collections.Concurrent;
using System.Linq;
using SocketJack.Net;
using SocketJack.Serialization;

namespace SocketJack.Extensions {

    public static class ConnectedClientExtensions {

        /// <summary>
        /// Send a serializable object to all ConnectedSocket Except
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <param name="Except">The socket to exclude.</param>
        /// <remarks></remarks>
        public static void SendBroadcast(this ConcurrentDictionary<Guid, NetworkConnection> Clients, object Obj, NetworkConnection Except) {
            var clientArray = Clients.ToArray();
            if (clientArray.Length == 0) return;

            // Find a valid parent to serialize with (all clients share the same server parent)
            ISocket parent = null;
            for (int i = 0; i < clientArray.Length; i++) {
                var c = clientArray[i].Value;
                if (c != null && !c.Closed && c.Socket != null && c.Socket.Connected) {
                    parent = c.Parent;
                    break;
                }
            }
            if (parent == null) return;

            // Serialize the object once instead of per-client
            var wrapped = new Wrapper(Obj, parent);
            var bytes = parent.Options.Serializer.Serialize(wrapped);
            byte[] processed = parent.Options.UseCompression ? parent.Options.CompressionAlgorithm.Compress(bytes) : bytes;
            var serialized = processed.Terminate();

            // Distribute pre-serialized bytes to each client's send queue
            for (int i = 0; i < clientArray.Length; i++) {
                var client = clientArray[i].Value;
                if (client != null && !ReferenceEquals(client, Except) && !client.Closed && client.Socket != null && client.Socket.Connected) {
                    if (!client.CanSend(Obj)) continue;
                    if (parent.Options.Serializer is BinarySerializer) {
                        var encoded = BinaryNetworkCompression.Encode(parent, client, bytes);
                        client.TryEnqueueSendBytes((parent.Options.UseCompression ? parent.Options.CompressionAlgorithm.Compress(encoded) : encoded).Terminate());
                    } else client.TryEnqueueSendBytes(serialized);
                }
            }
        }

        /// <summary>
        /// Send a serializable object to all ConnectedSocket.
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <remarks></remarks>
        public static void SendBroadcast(this ConcurrentDictionary<Guid, NetworkConnection> Clients, object Obj) {
            Clients.SendBroadcast(Obj, null);
        }
    }
}
