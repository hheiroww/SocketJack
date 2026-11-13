using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    /// <summary>A DNS-SD advertisement. Treat discovery results as unauthenticated connection hints.</summary>
    public sealed class LanServiceEndpoint {
        internal LanServiceEndpoint(string instance, string host, int port, IPAddress[] addresses) {
            Instance = instance;
            Host = host;
            Port = port;
            Addresses = Array.AsReadOnly(addresses);
        }
        public string Instance { get; }
        public string Host { get; }
        public int Port { get; }
        public IReadOnlyList<IPAddress> Addresses { get; }
    }

    /// <summary>Opt-in link-local discovery. Established SocketJack connections retain their regular authentication and serializer.</summary>
    public static class LanServiceDiscovery {
        private static string Service(bool udp) => udp ? "_socketjack._udp" : "_socketjack._tcp";

        /// <summary>Advertise a listening endpoint until the returned registration is disposed.</summary>
        public static IDisposable Advertise(string instance, int port, bool udp = false, IEnumerable<IPAddress> addresses = null) {
            if (string.IsNullOrWhiteSpace(instance) || System.Text.Encoding.UTF8.GetByteCount(instance) > 63 ||
                instance.Any(c => char.IsControl(c) || c == '.' || c == '\\'))
                throw new ArgumentException("Specify a DNS-SD instance label.", nameof(instance));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            var discovery = new ServiceDiscovery();
            try {
                var profile = new ServiceProfile(new DomainName(instance), new DomainName(Service(udp)), (ushort)port, addresses);
                discovery.Advertise(profile);
                discovery.Announce(profile);
                return discovery;
            } catch {
                discovery.Dispose();
                throw;
            }
        }

        /// <summary>Browse the local link for a bounded interval and return resolved service endpoints.</summary>
        public static async Task<IReadOnlyList<LanServiceEndpoint>> BrowseAsync(TimeSpan duration, bool udp = false, CancellationToken cancellation = default) {
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(duration));
            cancellation.ThrowIfCancellationRequested();
            string service = Service(udp);
            string suffix = "." + service + ".local";
            var found = new Dictionary<string, LanServiceEndpoint>(StringComparer.OrdinalIgnoreCase);
            using var discovery = new ServiceDiscovery();
            discovery.Mdns.AnswerReceived += (_, answer) => {
                var records = answer.Message.Answers.Concat(answer.Message.AdditionalRecords).ToArray();
                lock (found) {
                    foreach (var record in records.OfType<SRVRecord>()) {
                        string instance = record.Name.ToString().TrimEnd('.');
                        string host = record.Target.ToString().TrimEnd('.');
                        if (!instance.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || record.Port == 0 ||
                            !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || found.Count >= 128 && !found.ContainsKey(instance))
                            continue;
                        var ips = records.OfType<AddressRecord>().Where(item => item.Name == record.Target)
                            .Select(item => item.Address).Distinct().Take(8).ToArray();
                        found[instance] = new LanServiceEndpoint(instance, host, record.Port, ips);
                    }
                }
            };
            discovery.QueryServiceInstances(new DomainName(service));
            await Task.Delay(duration, cancellation).ConfigureAwait(false);
            lock (found) return found.Values.ToArray();
        }
    }
}
