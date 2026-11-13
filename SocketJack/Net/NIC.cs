using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using Mono.Nat;

namespace SocketJack.Net {

    /// <summary>
    /// Active Network Interface Card
    /// </summary>
    public class NIC {

        public static event OnInterfaceDiscoveredEventHandler OnInterfaceDiscovered;
        public delegate void OnInterfaceDiscoveredEventHandler(int MTU, IPAddress LocalIP);

        public static event NatDiscoveredEventHandler NatDiscovered;

        public delegate void NatDiscoveredEventHandler();
        public static event OnErrorEventHandler OnError;

        public delegate void OnErrorEventHandler(Exception ex);

        /// <summary>
        /// Maximum Transmission Unit defined by the currently active NIC.
        /// </summary>
        public static int MTU {
            get {
                return _MTU;
            }
        }
        private static int _MTU = -1;

        /// <summary>
        /// <para>Overhead for Segment Type string for Reflection.</para>
        /// <para>Default is derrived from Typical JSON serialization.</para>
        /// <para></para>
        /// <para>If the Serializer is adding a lot of padding and object transfers are failing, increase this on Client and Server.</para>
        /// <para>Default is 200 (bytes)</para>
        /// </summary>
        public static int SegmentOverhead {
            get {
                return _SegmentOverhead;
            }
            set {
                int LastValue = _SegmentOverhead;
                _SegmentOverhead = value;
                _MTU = MTU + LastValue - SegmentOverhead;
            }
        }
        private static int _SegmentOverhead = 200;

        /// <summary>
        /// State of the active Network Interface Card
        /// </summary>
        /// <returns>True when the NIC has been found.</returns>
        public static bool InterfaceDiscovered {
            get {
                return _InterfaceDiscovered;
            }
        }
        private static bool _InterfaceDiscovered = false;

        /// <summary>
        /// Network Address Translation
        /// </summary>
        /// <returns>Discovered NAT device, or NULL.</returns>
        public static INatDevice NAT {
            get {
                return _NAT;
            }
        }
        private static INatDevice _NAT;
        private static bool DiscoveryStarted = false;

        /// <summary>
        /// Call if lost internet connection during construction of TcpClient/TcpServer and NAT = null.
        /// </summary>
        public static void DiscoverNAT() {
            if (NAT == null&& !DiscoveryStarted) {
                DiscoveryStarted = true;
                NatUtility.DeviceFound += OnNatDeviceFound;
                NatUtility.StartDiscovery();
            }
        }

        private static async void OnNatDeviceFound(object sender, DeviceEventArgs args) {
            _NAT = args.Device;
            NatDiscovered?.Invoke();
            var lIP = LocalIP();
            _InterfaceDiscovered = true;
            _MTU = await GetMTU(lIP) - _SegmentOverhead;
            OnInterfaceDiscovered?.Invoke(MTU, lIP);
        }

        /// <summary>
        /// Gets the Maximum Transmission Unit of the active NIC.
        /// </summary>
        /// <param name="LocalIP"></param>
        /// <returns></returns>
        public static async Task<int> GetMTU(IPAddress LocalIP) {
            return await Task.Run(() => {
                var computerProperties = IPGlobalProperties.GetIPGlobalProperties();
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                if (nics == null|| nics.Length < 1)
                    return -1;
                foreach (NetworkInterface adapter in nics) {
                    var properties = adapter.GetIPProperties();
                    int output = -1;
                    if (adapter.Supports(NetworkInterfaceComponent.IPv4)) {
                        var ipv4 = properties.GetIPv4Properties();
                        foreach (var IP in properties.UnicastAddresses) {
                            if ((IP.Address.ToString() ?? "") == (LocalIP.ToString() ?? "")) {
                                output = ipv4.Mtu;
                                break;
                            }
                        }
                    }
                    if (output != -1) {
                        return output;
                    }
                }
                return -1;
            });
        }

        /// <summary>
        /// <para>Check if the internet is available.</para>
        /// <para>Uses Google.com as a test.</para>
        /// </summary>
        /// <returns></returns>
        public static bool InternetAvailable() {
            using (var ping = new Ping()) {
                var reply = ping.Send("www.google.com");
                if (reply != null && reply.Status != IPStatus.Success) {
                    return false;
                } else {
                    return true;
                }
            }
        }

        /// <summary>
        /// <para>Get the Local IP Address of the active NIC.</para>
        /// <para>Uses a UDPClient to get the LocalEndPoint.</para>
        /// </summary>
        /// <returns></returns>
        public static IPAddress LocalIP() {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()) {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var properties = adapter.GetIPProperties();
                if (properties.GatewayAddresses.Count == 0) continue;
                foreach (var address in properties.UnicastAddresses) {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                        return address.Address;
                }
            }
            return IPAddress.Loopback;
        }

        /// <summary>
        /// <para>Check if a port is available.</para>
        /// <para>Optionally forward the port if available.</para>
        /// </summary>
        /// <param name="port"></param>
        /// <param name="ForwardPortIfAvailable"></param>
        /// <returns></returns>
        public static async Task<bool> PortAvailable(int port, bool ForwardPortIfAvailable = false) {
            bool isAvailable = true;

            var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            // Use GetActiveTcpListeners() instead of GetActiveTcpConnections():
            // listeners is a much smaller set (only bound ports) and avoids
            // enumerating every active TCP connection on the system.
            IPEndPoint[] listeners = ipGlobalProperties.GetActiveTcpListeners();

            for (int i = 0; i < listeners.Length; i++) {
                if (listeners[i].Port == port) {
                    isAvailable = false;
                    break;
                }
            }

            if (isAvailable && ForwardPortIfAvailable && InterfaceDiscovered && _NAT != null) {
                return await ForwardPort(port).ConfigureAwait(false);
            } else {
                return isAvailable;
            }
        }

        /// <summary>
        /// <para>Check if a port is available.</para>
        /// <para>Optionally forward the port if available.</para>
        /// </summary>
        /// <param name="port"></param>
        /// <param name="ForwardPortIfAvailable"></param>
        /// <returns></returns>
        public static async Task<bool> ForwardPort(int port) {
            var device = NAT;
            if (!InterfaceDiscovered || device == null || port < 1 || port > 65535)
                return false;
            var mapping = new Mapping(Protocol.Tcp, port, port, 1800, "SocketJack");
            try {
                var create = device.CreatePortMapAsync(mapping);
                using var stopDelay = new CancellationTokenSource();
                if (await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(5), stopDelay.Token)).ConfigureAwait(false) != create) {
                    _ = RevokeLateMappingAsync(device, mapping, create);
                    return false;
                }
                stopDelay.Cancel();
                await create.ConfigureAwait(false);
                return true;
            } catch (Exception ex) {
                OnError?.Invoke(ex);
                return false;
            }
        }

        private static async Task RevokeLateMappingAsync(INatDevice device, Mapping mapping, Task create) {
            try {
                await create.ConfigureAwait(false);
                await device.DeletePortMapAsync(mapping).ConfigureAwait(false);
            } catch (Exception ex) {
                OnError?.Invoke(ex);
            }
        }

        /// <summary>
        /// <para>Check if a port is available.</para>
        /// <para>Optionally forward the port if available.</para>
        /// </summary>
        /// <param name="port"></param>
        /// <param name="ForwardPortIfAvailable"></param>
        /// <returns></returns>
        public static async Task<bool> ForwardPorts(int[] ports) {
            bool allForwarded = true;
            for (int i = 0; i < ports.Length; i++) {
                bool forwarded = await ForwardPort(ports[i]);
                if (!forwarded) {
                    allForwarded = false;
                }
            }
            return allForwarded;
        }

        private static int RandomNumber(int LowerBound, int UpperBound) {
            return new Random().Next(LowerBound, UpperBound);
        }

        /// <summary>
        /// <para>Find an open port within a range.</para>
        /// <para>Optionally forward the port if available.</para>
        /// </summary>
        /// <param name="PortLowerBound"></param>
        /// <param name="PortUpperBound"></param>
        /// <param name="ForwardPortIfAvailable"></param>
        /// <returns></returns>
        public static async Task<long> FindOpenPort(int PortLowerBound, int PortUpperBound, bool ForwardPortIfAvailable = false) {
            if (PortLowerBound < 1 || PortUpperBound > 65536 || PortUpperBound <= PortLowerBound)
                throw new ArgumentOutOfRangeException(nameof(PortLowerBound));
            int count = PortUpperBound - PortLowerBound;
            int start = RandomNumber(0, count);
            for (int offset = 0; offset < count; offset++) {
                int port = PortLowerBound + (start + offset) % count;
                if (await PortAvailable(port, ForwardPortIfAvailable).ConfigureAwait(false)) return port;
            }
            throw new InvalidOperationException("No port in the requested range is available.");
        }

        protected internal static async void Initialize() {
            if (_NAT is null)
                DiscoverNAT();
        }

    }
}
