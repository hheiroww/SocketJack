using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace SocketJack.Unity.Transport
{
    public enum PortMappingStatus { Mapped, AlreadyMapped, Unavailable, Conflict, Cancelled, Failed }

    public interface INatPortMapper
    {
        Task<PortMappingLease> RequestMappingAsync(int port, TransportType protocol, CancellationToken cancellationToken = default);
    }

    /// <summary>A best-effort IPv4 gateway mapping, not a guarantee of public reachability.
    /// Dispose starts nonblocking cleanup; await ReleaseAsync or DisposeAsync to observe completion.
    /// A mapping that predates this lease is never renewed or removed.</summary>
    public sealed class PortMappingLease : IDisposable, IAsyncDisposable
    {
        private readonly Func<CancellationToken, Task> _release;
        private readonly Func<CancellationToken, Task<bool>> _renew;
        private int _leaseSeconds;
        private readonly CancellationTokenSource _renewalCancellation = new CancellationTokenSource();
        private readonly object _sync = new object();
        private Task _releaseTask;
        private Task _renewalTask = Task.CompletedTask;
        public PortMappingStatus Status { get; }
        public bool IsMapped => !IsReleased && (Status == PortMappingStatus.Mapped || Status == PortMappingStatus.AlreadyMapped);
        public bool IsOwned { get; }
        public IPEndPoint InternalEndpoint { get; }
        public IPEndPoint ExternalEndpoint { get; internal set; }
        public TransportType Protocol { get; }
        public string Error { get; internal set; }
        public string ReleaseError { get; private set; }
        public string RenewalError { get; private set; }
        public DateTimeOffset? ExpiresAtUtc { get; private set; }
        public bool IsReleased { get; private set; }
        public bool ReleaseSucceeded => IsReleased && ReleaseError == null;

        /// <summary>Creates a mapper result. Custom INatPortMapper implementations may supply
        /// cleanup and renewal callbacks only for mappings they created and still own.</summary>
        public PortMappingLease(PortMappingStatus status, int port, TransportType protocol, IPAddress local = null,
            string error = null, bool owned = false, int leaseSeconds = 0, Func<CancellationToken, Task> release = null,
            Func<CancellationToken, Task<bool>> renew = null)
        {
            Status = status; Protocol = protocol; Error = error; IsOwned = owned;
            InternalEndpoint = local == null ? null : new IPEndPoint(local, port);
            _release = release; _renew = renew; _leaseSeconds = leaseSeconds;
            if (owned) ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
        }

        internal void StartRenewal(int leaseSeconds, bool automatic)
        {
            if (automatic && IsOwned) _renewalTask = RenewLoopAsync(leaseSeconds);
        }
        internal void UpdateLifetime(int leaseSeconds)
        { _leaseSeconds = leaseSeconds; ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds); }

        public async Task<bool> RenewAsync(CancellationToken cancellationToken = default)
        {
            if (!IsOwned || _renew == null || _renewalCancellation.IsCancellationRequested) return false;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _renewalCancellation.Token))
            {
                try
                {
                    bool renewed = await _renew(linked.Token).ConfigureAwait(false);
                    if (!renewed) RenewalError = "Gateway mapping ownership changed; renewal was skipped.";
                    else ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(_leaseSeconds);
                    return renewed;
                }
                catch (OperationCanceledException) { return false; }
                catch { RenewalError = "Gateway lease renewal failed; the existing mapping will expire unless renewed."; return false; }
            }
        }

        private async Task RenewLoopAsync(int leaseSeconds)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _leaseSeconds / 2)), _renewalCancellation.Token).ConfigureAwait(false);
                    if (!await RenewAsync(_renewalCancellation.Token).ConfigureAwait(false)) return;
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(_leaseSeconds);
                }
            }
            catch (OperationCanceledException) { }
        }

        public Task ReleaseAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync) return _releaseTask ?? (_releaseTask = ReleaseCoreAsync(cancellationToken));
        }
        private async Task ReleaseCoreAsync(CancellationToken cancellationToken)
        {
            _renewalCancellation.Cancel();
            try
            {
                await _renewalTask.ConfigureAwait(false);
                if (_release != null && IsOwned) await _release(cancellationToken).ConfigureAwait(false);
            }
            catch { ReleaseError = "Gateway cleanup could not be confirmed; the finite mapping lease will expire."; }
            finally { IsReleased = true; }
        }
        public void Dispose() { _ = ReleaseAsync(); }
        public ValueTask DisposeAsync() => new ValueTask(ReleaseAsync());
    }

    public sealed class UpnpPortMapperOptions
    {
        public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public int LeaseDurationSeconds { get; set; } = 3600;
        public bool AutoRenew { get; set; } = true;
        public int MaxResponseBytes { get; set; } = 64 * 1024;
        public int MaxOwnedMappings { get; set; } = 16;
        public string Description { get; set; } = "SocketJack.Unity";
        internal UpnpPortMapperOptions Snapshot()
        {
            if (DiscoveryTimeout <= TimeSpan.Zero || DiscoveryTimeout > TimeSpan.FromSeconds(30) || RequestTimeout <= TimeSpan.Zero ||
                RequestTimeout > TimeSpan.FromSeconds(30) || OperationTimeout <= TimeSpan.Zero || OperationTimeout > TimeSpan.FromMinutes(2) ||
                LeaseDurationSeconds < 60 || LeaseDurationSeconds > 86400 || MaxResponseBytes < 1024 || MaxResponseBytes > 1024 * 1024 ||
                MaxOwnedMappings < 1 || MaxOwnedMappings > 256 || string.IsNullOrWhiteSpace(Description) || Description.Length > 31)
                throw new ArgumentOutOfRangeException(nameof(UpnpPortMapperOptions));
            return (UpnpPortMapperOptions)MemberwiseClone();
        }
    }

    /// <summary>The IPv4 source address and unambiguous gateway selected by the default outbound route.</summary>
    public sealed class GatewayRoute
    {
        public IPAddress LocalAddress { get; }
        public IPAddress GatewayAddress { get; }
        public GatewayRoute(IPAddress localAddress, IPAddress gatewayAddress)
        {
            if (!ValidUnicast(localAddress) || !ValidUnicast(gatewayAddress) || localAddress.Equals(gatewayAddress))
                throw new ArgumentException("A route requires distinct unicast IPv4 source and gateway addresses.");
            LocalAddress = localAddress; GatewayAddress = gatewayAddress;
        }
        internal static bool ValidUnicast(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
            byte first = address.GetAddressBytes()[0];
            return first != 0 && first < 224 && !address.Equals(IPAddress.Broadcast);
        }
    }

    public interface IGatewayRouteProvider
    {
        Task<GatewayRoute> GetDefaultRouteAsync(CancellationToken cancellationToken);
    }
    public interface IGatewayDiscovery
    {
        Task<Uri> DiscoverAsync(GatewayRoute route, TimeSpan timeout, CancellationToken cancellationToken);
    }

    /// <summary>UPnP WANIPConnection/WANPPPConnection mapper for the selected IPv4 gateway only.
    /// Discovery URLs and service control URLs must use that literal gateway address. Redirects,
    /// proxies, external XML entities, oversized responses and permanent-lease fallbacks are disabled.
    /// Unsupported gateways, CGNAT and double NAT do not prevent a game server from listening.</summary>
    public sealed class UpnpPortMapper : INatPortMapper, IDisposable, IAsyncDisposable
    {
        private const string SoapNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
        private static readonly string[] Services = {
            "urn:schemas-upnp-org:service:WANIPConnection:2", "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1" };
        private sealed class Service { public GatewayRoute Route; public Uri Control; public string Type; }
        private sealed class Registration { public Service Service; public int Port; public string Protocol, Description; }
        private sealed class Mapping { public IPAddress Address; public int Port; public string Description; public bool Enabled; public uint? LeaseSeconds; }
        private sealed class UpnpFault : Exception { public int Code; public UpnpFault(int code) { Code = code; } }
        private readonly UpnpPortMapperOptions _options;
        private readonly IGatewayRouteProvider _routes;
        private readonly IGatewayDiscovery _discovery;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly Dictionary<Registration, PortMappingLease> _owned = new Dictionary<Registration, PortMappingLease>();
        private readonly object _disposeSync = new object();
        private Task _disposeTask;
        private bool _disposed;

        /// <summary>Optional injected dependencies support offline tests. An injected HTTP handler must
        /// not follow redirects; HttpClientHandler redirects/proxies are disabled here as well.</summary>
        public UpnpPortMapper(UpnpPortMapperOptions options = null, IGatewayRouteProvider routeProvider = null,
            IGatewayDiscovery discovery = null, HttpMessageHandler httpHandler = null)
        {
            _options = (options ?? new UpnpPortMapperOptions()).Snapshot();
            _routes = routeProvider ?? new DefaultGatewayRouteProvider();
            _discovery = discovery ?? new GatewaySsdpDiscovery();
            if (httpHandler == null) httpHandler = new HttpClientHandler();
            if (httpHandler is HttpClientHandler handler)
            { handler.AllowAutoRedirect = false; handler.UseProxy = false; handler.UseCookies = false; handler.AutomaticDecompression = DecompressionMethods.None; }
            _http = new HttpClient(httpHandler, true) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public async Task<PortMappingLease> RequestMappingAsync(int port, TransportType protocol, CancellationToken cancellationToken = default)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (protocol != TransportType.Udp && protocol != TransportType.Tcp) throw new ArgumentOutOfRangeException(nameof(protocol));
            if (_disposed) throw new ObjectDisposedException(nameof(UpnpPortMapper));
            Registration pending = null;
            bool addAttempted = false, held = false;
            using (var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token))
            {
                operation.CancelAfter(_options.OperationTimeout);
                try
                {
                    await _gate.WaitAsync(operation.Token).ConfigureAwait(false); held = true;
                    if (_owned.Count >= _options.MaxOwnedMappings) return Result(PortMappingStatus.Unavailable, port, protocol, "Owned mapping capacity reached.");
                    var route = await _routes.GetDefaultRouteAsync(operation.Token).ConfigureAwait(false);
                    if (route == null) return Result(PortMappingStatus.Unavailable, port, protocol, "No unambiguous IPv4 default gateway is available.");
                    var location = await _discovery.DiscoverAsync(route, _options.DiscoveryTimeout, operation.Token).ConfigureAwait(false);
                    if (location == null) return Result(PortMappingStatus.Unavailable, port, protocol, "The selected gateway did not advertise UPnP.");
                    ValidateUrl(location, route);
                    var service = await ReadServiceAsync(route, location, operation.Token).ConfigureAwait(false);
                    if (service == null) return Result(PortMappingStatus.Unavailable, port, protocol, "The gateway has no supported WAN connection service.");
                    pending = new Registration { Service = service, Port = port, Protocol = protocol == TransportType.Udp ? "UDP" : "TCP",
                        Description = _options.Description + " " + Guid.NewGuid().ToString("N") };
                    var existing = await GetMappingAsync(pending, operation.Token).ConfigureAwait(false);
                    if (existing != null)
                    {
                        if (!SameTarget(pending, existing) || !existing.Enabled)
                            return Result(PortMappingStatus.Conflict, port, protocol, "The external port already belongs to a different or disabled mapping.", route.LocalAddress);
                        var usable = new PortMappingLease(PortMappingStatus.AlreadyMapped, port, protocol, route.LocalAddress);
                        usable.ExternalEndpoint = await ExternalEndpointAsync(service, port, operation.Token).ConfigureAwait(false);
                        return usable; // Pre-existing mappings are never renewed or deleted.
                    }
                    addAttempted = true;
                    await AddMappingAsync(pending, operation.Token).ConfigureAwait(false);
                    var created = await GetMappingAsync(pending, operation.Token).ConfigureAwait(false);
                    if (!Owns(pending, created) || !FiniteLease(created)) throw new InvalidDataException("Gateway mapping ownership and finite lifetime could not be confirmed.");
                    var registration = pending;
                    int leaseSeconds = (int)created.LeaseSeconds.Value;
                    var lease = new PortMappingLease(PortMappingStatus.Mapped, port, protocol, route.LocalAddress, owned: true,
                        leaseSeconds: leaseSeconds,
                        release: token => ReleaseRegistrationAsync(registration, token), renew: token => RenewRegistrationAsync(registration, token));
                    lease.ExternalEndpoint = await ExternalEndpointAsync(service, port, operation.Token).ConfigureAwait(false);
                    _owned.Add(registration, lease);
                    lease.StartRenewal(leaseSeconds, _options.AutoRenew);
                    addAttempted = false;
                    return lease;
                }
                catch (OperationCanceledException)
                {
                    if (addAttempted && pending != null) await TryUndoAsync(pending).ConfigureAwait(false);
                    return Result(cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested ? PortMappingStatus.Cancelled : PortMappingStatus.Unavailable,
                        port, protocol, "Gateway mapping was cancelled or timed out.");
                }
                catch (UpnpFault fault)
                {
                    if (addAttempted && pending != null) await TryUndoAsync(pending).ConfigureAwait(false);
                    return Result(fault.Code == 718 ? PortMappingStatus.Conflict : PortMappingStatus.Unavailable, port, protocol,
                        fault.Code == 725 ? "The gateway requires permanent leases; no permanent mapping was requested." : "The gateway rejected the mapping request (UPnP " + fault.Code.ToString(CultureInfo.InvariantCulture) + ").");
                }
                catch (Exception error) when (error is HttpRequestException || error is IOException || error is InvalidDataException || error is XmlException || error is SocketException || error is InvalidOperationException || error is ArgumentException || error is UriFormatException)
                {
                    if (addAttempted && pending != null) await TryUndoAsync(pending).ConfigureAwait(false);
                    return Result(PortMappingStatus.Unavailable, port, protocol, "The gateway returned an unavailable, unsafe or invalid UPnP response.");
                }
                finally { if (held) _gate.Release(); }
            }
        }

        private async Task<Service> ReadServiceAsync(GatewayRoute route, Uri location, CancellationToken token)
        {
            var xml = await SendXmlAsync(new HttpRequestMessage(HttpMethod.Get, location), route, token).ConfigureAwait(false);
            Uri basis = location;
            var urlBase = xml.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value;
            if (!string.IsNullOrWhiteSpace(urlBase)) { basis = new Uri(urlBase, UriKind.Absolute); ValidateUrl(basis, route); }
            foreach (var type in Services)
                foreach (var entry in xml.Descendants().Where(e => e.Name.LocalName == "service"))
                {
                    if (Value(entry, "serviceType") != type) continue;
                    string control = Value(entry, "controlURL");
                    if (string.IsNullOrWhiteSpace(control)) continue;
                    var uri = new Uri(basis, control); ValidateUrl(uri, route);
                    return new Service { Route = route, Control = uri, Type = type };
                }
            return null;
        }

        private async Task<Mapping> GetMappingAsync(Registration registration, CancellationToken token)
        {
            try
            {
                var xml = await ActionAsync(registration.Service, "GetSpecificPortMappingEntry", PortFields(registration), token).ConfigureAwait(false);
                var response = Response(xml, "GetSpecificPortMappingEntryResponse");
                if (!IPAddress.TryParse(Value(response, "NewInternalClient"), out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
                    !int.TryParse(Value(response, "NewInternalPort"), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
                    throw new InvalidDataException("Invalid mapping target.");
                string enabled = Value(response, "NewEnabled");
                bool finite = uint.TryParse(Value(response, "NewLeaseDuration"), NumberStyles.None, CultureInfo.InvariantCulture, out uint leaseSeconds);
                return new Mapping { Address = address, Port = port, Description = Value(response, "NewPortMappingDescription"), Enabled = enabled == "1" || enabled == "true", LeaseSeconds = finite ? (uint?)leaseSeconds : null };
            }
            catch (UpnpFault fault) when (fault.Code == 714) { return null; }
        }

        private Task<XDocument> AddMappingAsync(Registration registration, CancellationToken token)
        {
            var fields = PortFields(registration);
            fields.Add(new KeyValuePair<string, string>("NewInternalPort", registration.Port.ToString(CultureInfo.InvariantCulture)));
            fields.Add(new KeyValuePair<string, string>("NewInternalClient", registration.Service.Route.LocalAddress.ToString()));
            fields.Add(new KeyValuePair<string, string>("NewEnabled", "1"));
            fields.Add(new KeyValuePair<string, string>("NewPortMappingDescription", registration.Description));
            fields.Add(new KeyValuePair<string, string>("NewLeaseDuration", _options.LeaseDurationSeconds.ToString(CultureInfo.InvariantCulture)));
            return ActionAsync(registration.Service, "AddPortMapping", fields, token);
        }
        private async Task<IPEndPoint> ExternalEndpointAsync(Service service, int port, CancellationToken token)
        {
            try
            {
                var xml = await ActionAsync(service, "GetExternalIPAddress", Array.Empty<KeyValuePair<string, string>>(), token).ConfigureAwait(false);
                return IPAddress.TryParse(Value(Response(xml, "GetExternalIPAddressResponse"), "NewExternalIPAddress"), out var address) && GatewayRoute.ValidUnicast(address)
                    ? new IPEndPoint(address, port) : null;
            }
            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return null; }
            catch (Exception error) when (error is UpnpFault || error is IOException || error is InvalidDataException || error is HttpRequestException || error is XmlException) { return null; }
        }

        private async Task<bool> RenewRegistrationAsync(Registration registration, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token))
            {
                timeout.CancelAfter(_options.OperationTimeout);
                await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    var previous = await GetMappingAsync(registration, timeout.Token).ConfigureAwait(false);
                    if (!Owns(registration, previous) || !FiniteLease(previous)) return false;
                    await AddMappingAsync(registration, timeout.Token).ConfigureAwait(false);
                    var renewed = await GetMappingAsync(registration, timeout.Token).ConfigureAwait(false);
                    if (!Owns(registration, renewed) || !FiniteLease(renewed)) return false;
                    if (_owned.TryGetValue(registration, out var lease)) lease.UpdateLifetime((int)renewed.LeaseSeconds.Value);
                    return true;
                }
                finally { _gate.Release(); }
            }
        }
        private async Task ReleaseRegistrationAsync(Registration registration, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(_options.OperationTimeout);
                await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
                try { await DeleteIfOwnedAsync(registration, timeout.Token).ConfigureAwait(false); }
                finally { _owned.Remove(registration); _gate.Release(); }
            }
        }
        private async Task TryUndoAsync(Registration registration)
        {
            using (var timeout = new CancellationTokenSource(_options.OperationTimeout))
            {
                try { await DeleteIfOwnedAsync(registration, timeout.Token).ConfigureAwait(false); }
                catch { } // A finite lease bounds any mapping whose cleanup cannot be confirmed.
            }
        }
        private async Task DeleteIfOwnedAsync(Registration registration, CancellationToken token)
        {
            var mapping = await GetMappingAsync(registration, token).ConfigureAwait(false);
            if (mapping == null) return;
            if (!Owns(registration, mapping)) return; // Never remove another owner or changed target.
            var response = await ActionAsync(registration.Service, "DeletePortMapping", PortFields(registration), token).ConfigureAwait(false);
            Response(response, "DeletePortMappingResponse");
            if (Owns(registration, await GetMappingAsync(registration, token).ConfigureAwait(false)))
                throw new InvalidDataException("Gateway retained the owned mapping after deletion.");
        }

        private Task<XDocument> ActionAsync(Service service, string action, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken token)
        {
            var text = new StringBuilder();
            using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
            {
                writer.WriteStartElement("s", "Envelope", SoapNamespace);
                writer.WriteAttributeString("s", "encodingStyle", SoapNamespace, "http://schemas.xmlsoap.org/soap/encoding/");
                writer.WriteStartElement("s", "Body", SoapNamespace); writer.WriteStartElement("u", action, service.Type);
                foreach (var field in fields) writer.WriteElementString(field.Key, field.Value);
                writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
            }
            var request = new HttpRequestMessage(HttpMethod.Post, service.Control) { Content = new StringContent(text.ToString(), Encoding.UTF8, "text/xml") };
            request.Headers.TryAddWithoutValidation("SOAPACTION", "\"" + service.Type + "#" + action + "\"");
            return SendXmlAsync(request, service.Route, token);
        }

        private async Task<XDocument> SendXmlAsync(HttpRequestMessage request, GatewayRoute route, CancellationToken token)
        {
            using (request)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(_options.RequestTimeout);
                ValidateUrl(request.RequestUri, route);
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) throw new InvalidDataException("UPnP redirects are disabled.");
                    if (response.RequestMessage?.RequestUri != null) ValidateUrl(response.RequestMessage.RequestUri, route);
                    if (response.Content == null) throw new InvalidDataException("UPnP response body is missing.");
                    if (response.Content.Headers.ContentLength > _options.MaxResponseBytes) throw new InvalidDataException("UPnP response is too large.");
                    using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var bounded = new MemoryStream())
                    {
                        var buffer = new byte[4096];
                        while (true)
                        {
                            int read = await body.ReadAsync(buffer, 0, Math.Min(buffer.Length, _options.MaxResponseBytes + 1 - (int)bounded.Length), timeout.Token).ConfigureAwait(false);
                            if (read == 0) break;
                            bounded.Write(buffer, 0, read);
                            if (bounded.Length > _options.MaxResponseBytes) throw new InvalidDataException("UPnP response is too large.");
                        }
                        bounded.Position = 0;
                        using (var reader = XmlReader.Create(bounded, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                            MaxCharactersInDocument = _options.MaxResponseBytes, IgnoreComments = true }))
                        {
                            var xml = XDocument.Load(reader);
                            var fault = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
                            if (fault != null)
                            {
                                int.TryParse(fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int code);
                                throw new UpnpFault(code);
                            }
                            if (!response.IsSuccessStatusCode) throw new HttpRequestException("UPnP gateway HTTP request failed.");
                            return xml;
                        }
                    }
                }
            }
        }

        private static void ValidateUrl(Uri uri, GatewayRoute route)
        {
            if (uri == null || !uri.IsAbsoluteUri || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                uri.HostNameType != UriHostNameType.IPv4 || !IPAddress.TryParse(uri.Host, out var address) || !address.Equals(route.GatewayAddress))
                throw new InvalidDataException("UPnP endpoints must use the selected gateway's literal IPv4 address.");
        }

        /// <summary>Validates a bounded SSDP reply from the selected gateway only. Custom discovery
        /// implementations can reuse this parser without trusting arbitrary LAN LOCATION headers.</summary>
        public static bool TryGetGatewayDescription(GatewayRoute route, IPEndPoint responder, byte[] response, out Uri location)
        {
            location = null;
            if (route == null || responder == null || response == null || response.Length > 4096 || !responder.Address.Equals(route.GatewayAddress)) return false;
            string[] lines = Encoding.ASCII.GetString(response).Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1 200 ", StringComparison.OrdinalIgnoreCase)) return false;
            var locations = lines.Skip(1).Where(line => line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (locations.Length != 1 || !Uri.TryCreate(locations[0].Substring(9).Trim(), UriKind.Absolute, out var uri)) return false;
            try { ValidateUrl(uri, route); location = uri; return true; }
            catch (InvalidDataException) { return false; }
        }
        private static bool SameTarget(Registration registration, Mapping mapping) => mapping != null && mapping.Port == registration.Port && mapping.Address.Equals(registration.Service.Route.LocalAddress);
        private static bool Owns(Registration registration, Mapping mapping) => SameTarget(registration, mapping) && mapping.Enabled && mapping.Description == registration.Description;
        private bool FiniteLease(Mapping mapping) => mapping?.LeaseSeconds > 0 && mapping.LeaseSeconds <= (uint)_options.LeaseDurationSeconds + 5;
        private static string Value(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
        private static XElement Response(XDocument document, string name) => document.Descendants().FirstOrDefault(e => e.Name.LocalName == name) ?? throw new InvalidDataException("UPnP action response is missing.");
        private static List<KeyValuePair<string, string>> PortFields(Registration registration) => new List<KeyValuePair<string, string>> {
            new KeyValuePair<string, string>("NewRemoteHost", ""), new KeyValuePair<string, string>("NewExternalPort", registration.Port.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("NewProtocol", registration.Protocol) };
        private static PortMappingLease Result(PortMappingStatus status, int port, TransportType protocol, string error, IPAddress local = null) => new PortMappingLease(status, port, protocol, local, error);

        public void Dispose() { _ = DisposeAsync(); }
        public ValueTask DisposeAsync()
        {
            lock (_disposeSync) return new ValueTask(_disposeTask ?? (_disposeTask = DisposeCoreAsync()));
        }
        private async Task DisposeCoreAsync()
        {
            _disposed = true; _lifetime.Cancel();
            await _gate.WaitAsync().ConfigureAwait(false);
            PortMappingLease[] leases;
            try { leases = _owned.Values.ToArray(); }
            finally { _gate.Release(); }
            foreach (var lease in leases) await lease.ReleaseAsync().ConfigureAwait(false);
            _http.Dispose();
        }

        private sealed class DefaultGatewayRouteProvider : IGatewayRouteProvider
        {
            public Task<GatewayRoute> GetDefaultRouteAsync(CancellationToken cancellationToken)
                => Task.Run(() => Resolve(cancellationToken), cancellationToken);
            private static GatewayRoute Resolve(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    IPAddress local;
                    // UDP Connect chooses an OS route without sending a datagram.
                    using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    { probe.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9)); local = ((IPEndPoint)probe.LocalEndPoint).Address; }
                    var matches = new List<GatewayRoute>();
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up) continue;
                        var properties = nic.GetIPProperties();
                        if (!properties.UnicastAddresses.Any(a => a.Address.Equals(local))) continue;
                        var gateways = properties.GatewayAddresses.Select(g => g.Address).Where(GatewayRoute.ValidUnicast).Distinct().ToArray();
                        if (gateways.Length != 1) return null; // Ambiguous gateway choice is not guessed.
                        matches.Add(new GatewayRoute(local, gateways[0]));
                    }
                    if (matches.Count == 1) return matches[0];
                }
                catch (SocketException) { }
                catch (NetworkInformationException) { }
                catch (PlatformNotSupportedException) { }
                return null;
            }
        }

        private sealed class GatewaySsdpDiscovery : IGatewayDiscovery
        {
            public async Task<Uri> DiscoverAsync(GatewayRoute route, TimeSpan timeout, CancellationToken cancellationToken)
            {
                using (var discovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    discovery.CancelAfter(timeout);
                    socket.Bind(new IPEndPoint(route.LocalAddress, 0));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, route.LocalAddress.GetAddressBytes());
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    using (discovery.Token.Register(() => socket.Dispose()))
                    {
                        try
                        {
                            foreach (string target in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1" })
                            {
                                string request = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: " + target + "\r\n\r\n";
                                byte[] bytes = Encoding.ASCII.GetBytes(request);
                                await socket.SendToAsync(new ArraySegment<byte>(bytes), SocketFlags.None, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)).ConfigureAwait(false);
                            }
                            var buffer = new byte[4097];
                            while (true)
                            {
                                var packet = await socket.ReceiveFromAsync(new ArraySegment<byte>(buffer), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0)).ConfigureAwait(false);
                                if (packet.ReceivedBytes > 4096 || !((IPEndPoint)packet.RemoteEndPoint).Address.Equals(route.GatewayAddress)) continue;
                                var bytes = new byte[packet.ReceivedBytes]; Buffer.BlockCopy(buffer, 0, bytes, 0, bytes.Length);
                                if (TryGetGatewayDescription(route, (IPEndPoint)packet.RemoteEndPoint, bytes, out var uri)) return uri;
                            }
                        }
                        catch (ObjectDisposedException) { cancellationToken.ThrowIfCancellationRequested(); return null; }
                        catch (SocketException) { cancellationToken.ThrowIfCancellationRequested(); return null; }
                    }
                }
            }
        }
    }
}
