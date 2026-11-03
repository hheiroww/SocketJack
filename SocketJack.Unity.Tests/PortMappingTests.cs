using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Unity.Transport;

namespace SocketJack.Unity.Tests
{
    [TestClass]
    public class PortMappingTests
    {
        private static readonly GatewayRoute Route = new GatewayRoute(IPAddress.Parse("192.168.50.25"), IPAddress.Parse("192.168.50.1"));

        [DataTestMethod]
        [DataRow(TransportType.Udp)]
        [DataRow(TransportType.Tcp)]
        public async Task MapsDesiredProtocolEscapesSoapAndReleasesOnlyItsOwnedLease(TransportType protocol)
        {
            var gateway = new FakeGateway();
            await using var mapper = Mapper(gateway, new UpnpPortMapperOptions { Description = "Game <&>\"", AutoRenew = false });
            var lease = await mapper.RequestMappingAsync(7777, protocol);
            Assert.AreEqual(PortMappingStatus.Mapped, lease.Status);
            Assert.IsTrue(lease.IsOwned); Assert.IsTrue(lease.IsMapped);
            Assert.AreEqual(new IPEndPoint(Route.LocalAddress, 7777), lease.InternalEndpoint);
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("203.0.113.4"), 7777), lease.ExternalEndpoint);
            Assert.AreEqual(3600, gateway.LeaseSeconds);
            Assert.AreEqual(protocol == TransportType.Udp ? "UDP" : "TCP", gateway.Protocol);
            Assert.IsTrue(gateway.Mapping.Description.StartsWith("Game <&>\" ", StringComparison.Ordinal));
            Assert.IsTrue(gateway.Bodies.Any(body => body.Contains("Game &lt;&amp;&gt;\"")));
            Assert.IsTrue(await lease.RenewAsync());
            Assert.AreEqual(2, gateway.Adds);
            await lease.ReleaseAsync(); await lease.ReleaseAsync(); lease.Dispose();
            Assert.AreEqual(1, gateway.Deletes);
            Assert.IsNull(gateway.Mapping);
            Assert.IsTrue(lease.ReleaseSucceeded);
        }

        [TestMethod]
        public async Task ExistingIdenticalMappingIsUsableButNeverRenewedOrDeleted()
        {
            var gateway = new FakeGateway { Mapping = new Map { Address = Route.LocalAddress, Port = 7777, Description = "Another application", Enabled = true } };
            await using var mapper = Mapper(gateway);
            var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp);
            Assert.AreEqual(PortMappingStatus.AlreadyMapped, lease.Status);
            Assert.IsTrue(lease.IsMapped); Assert.IsFalse(lease.IsOwned);
            Assert.IsFalse(await lease.RenewAsync());
            await lease.ReleaseAsync();
            Assert.AreEqual(0, gateway.Adds); Assert.AreEqual(0, gateway.Deletes);
            Assert.AreEqual("Another application", gateway.Mapping.Description);
        }

        [TestMethod]
        public async Task ExistingDifferentTargetOrDisabledMappingIsNeverOverwritten()
        {
            foreach (var existing in new[] {
                new Map { Address = IPAddress.Parse("192.168.50.99"), Port = 7777, Description = "Other host", Enabled = true },
                new Map { Address = Route.LocalAddress, Port = 8888, Description = "Other port", Enabled = true },
                new Map { Address = Route.LocalAddress, Port = 7777, Description = "Disabled by owner", Enabled = false } })
            {
                var gateway = new FakeGateway { Mapping = existing };
                await using var mapper = Mapper(gateway);
                var lease = await mapper.RequestMappingAsync(7777, TransportType.Tcp);
                Assert.AreEqual(PortMappingStatus.Conflict, lease.Status);
                await lease.ReleaseAsync();
                Assert.AreEqual(0, gateway.Adds); Assert.AreEqual(0, gateway.Deletes);
                Assert.AreSame(existing, gateway.Mapping);
            }
        }

        [TestMethod]
        public async Task ReleaseAndRenewRecheckDescriptionAndTargetOwnership()
        {
            var gateway = new FakeGateway();
            await using var mapper = Mapper(gateway);
            var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp);
            gateway.Mapping.Description = "Replacement owner";
            Assert.IsFalse(await lease.RenewAsync());
            await lease.ReleaseAsync();
            Assert.AreEqual(1, gateway.Adds); Assert.AreEqual(0, gateway.Deletes);
            Assert.AreEqual("Replacement owner", gateway.Mapping.Description);
        }

        [TestMethod]
        public async Task CancellationAfterAmbiguousAddCleansOnlyConfirmedOwnedMapping()
        {
            var gateway = new FakeGateway();
            using var cancellation = new CancellationTokenSource();
            gateway.AfterAdd = () => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
            await using var mapper = Mapper(gateway);
            var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp, cancellation.Token);
            Assert.AreEqual(PortMappingStatus.Cancelled, lease.Status);
            Assert.AreEqual(1, gateway.Adds); Assert.AreEqual(1, gateway.Deletes);
            Assert.IsNull(gateway.Mapping);
        }

        [TestMethod]
        public async Task CancellationAndTimeoutDoNotBlockOrAttemptMapping()
        {
            var gateway = new FakeGateway();
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await using (var mapper = Mapper(gateway))
            {
                var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp, cancellation.Token);
                Assert.AreEqual(PortMappingStatus.Cancelled, lease.Status);
                Assert.AreEqual(0, gateway.Requests);
            }
            var stalled = new FakeGateway { Stall = true };
            await using (var mapper = Mapper(stalled, new UpnpPortMapperOptions { RequestTimeout = TimeSpan.FromMilliseconds(40), OperationTimeout = TimeSpan.FromMilliseconds(200), AutoRenew = false }))
            {
                var watch = Stopwatch.StartNew();
                var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp);
                Assert.AreEqual(PortMappingStatus.Unavailable, lease.Status);
                Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2));
                Assert.AreEqual(0, stalled.Adds);
            }
        }

        [DataTestMethod]
        [DataRow("http://192.168.50.2/device.xml")]
        [DataRow("http://router.local/device.xml")]
        [DataRow("http://127.0.0.1/device.xml")]
        [DataRow("http://user@192.168.50.1/device.xml")]
        [DataRow("file:///device.xml")]
        public async Task DiscoveryLocationMustBeLiteralSelectedGatewayBeforeAnyHttp(string location)
        {
            var gateway = new FakeGateway();
            await using var mapper = Mapper(gateway, location: new Uri(location));
            var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp);
            Assert.AreEqual(PortMappingStatus.Unavailable, lease.Status);
            Assert.AreEqual(0, gateway.Requests);
        }

        [DataTestMethod]
        [DataRow("http://192.168.50.2/control", null)]
        [DataRow("http://router.local/control", null)]
        [DataRow("/control", "http://192.168.50.2/")]
        [DataRow("//192.168.50.2/control", null)]
        public async Task DeviceControlAndUrlBaseCannotEscapeGateway(string control, string urlBase)
        {
            var gateway = new FakeGateway { Control = control, UrlBase = urlBase };
            await using var mapper = Mapper(gateway);
            var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp);
            Assert.AreEqual(PortMappingStatus.Unavailable, lease.Status);
            Assert.AreEqual(1, gateway.Requests); Assert.AreEqual(0, gateway.Adds);
        }

        [TestMethod]
        public async Task RedirectsDtdAndOversizedXmlAreRejectedWithoutSoapMutation()
        {
            foreach (var attack in new[] { "redirect", "dtd", "oversize" })
            {
                var gateway = new FakeGateway { Attack = attack };
                await using var mapper = Mapper(gateway, new UpnpPortMapperOptions { MaxResponseBytes = 1024, AutoRenew = false });
                var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp);
                Assert.AreEqual(PortMappingStatus.Unavailable, lease.Status, attack);
                Assert.AreEqual(1, gateway.Requests); Assert.AreEqual(0, gateway.Adds);
            }
        }

        [TestMethod]
        public async Task UnsupportedPermanentLeasesAndMissingGatewayAreGraceful()
        {
            var gateway = new FakeGateway { AddError = 725 };
            await using (var mapper = Mapper(gateway))
            {
                var lease = await mapper.RequestMappingAsync(7777, TransportType.Tcp);
                Assert.AreEqual(PortMappingStatus.Unavailable, lease.Status);
                StringAssert.Contains(lease.Error, "permanent");
                Assert.AreEqual(1, gateway.Adds); Assert.AreEqual(0, gateway.Deletes);
                Assert.IsNull(gateway.Mapping);
            }
            var unused = new FakeGateway();
            await using (var mapper = new UpnpPortMapper(routeProvider: new FakeRoutes(null), discovery: new FakeDiscovery(), httpHandler: unused))
            {
                var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp);
                Assert.AreEqual(PortMappingStatus.Unavailable, lease.Status);
                Assert.AreEqual(0, unused.Requests);
            }
        }

        [TestMethod]
        public async Task DirectWanPppServiceAndMapperDisposalReleaseOwnedMapping()
        {
            var gateway = new FakeGateway { ServiceType = "urn:schemas-upnp-org:service:WANPPPConnection:1" };
            var mapper = Mapper(gateway);
            var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp);
            Assert.IsTrue(lease.IsOwned);
            await mapper.DisposeAsync();
            Assert.IsTrue(lease.IsReleased);
            Assert.AreEqual(1, gateway.Deletes);
        }

        [TestMethod]
        public void SsdpAdvertisementRequiresSelectedGatewaySourceAndOneSafeLocation()
        {
            var gateway = new IPEndPoint(Route.GatewayAddress, 1900);
            byte[] Reply(string headers) => Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" + headers + "\r\n\r\n");
            byte[] valid = Reply("LOCATION: http://192.168.50.1:5000/device.xml\r\nST: urn:schemas-upnp-org:service:WANIPConnection:1");
            Assert.IsTrue(UpnpPortMapper.TryGetGatewayDescription(Route, gateway, valid, out var location));
            Assert.AreEqual("/device.xml", location.AbsolutePath);
            Assert.IsFalse(UpnpPortMapper.TryGetGatewayDescription(Route, new IPEndPoint(IPAddress.Parse("192.168.50.99"), 1900), valid, out _));
            Assert.IsFalse(UpnpPortMapper.TryGetGatewayDescription(Route, gateway, Reply("LOCATION: http://127.0.0.1/private"), out _));
            Assert.IsFalse(UpnpPortMapper.TryGetGatewayDescription(Route, gateway, Reply("LOCATION: http://192.168.50.1/one\r\nLOCATION: http://192.168.50.1/two"), out _));
            Assert.IsFalse(UpnpPortMapper.TryGetGatewayDescription(Route, gateway, new byte[4097], out _));
        }

        [TestMethod]
        public async Task RouterSilentlyCreatingPermanentLeaseIsRejectedAndOwnedMappingRemoved()
        {
            var gateway = new FakeGateway { OverrideLeaseSeconds = 0 };
            await using var mapper = Mapper(gateway);
            var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp);
            Assert.AreEqual(PortMappingStatus.Unavailable, lease.Status);
            Assert.AreEqual(1, gateway.Adds); Assert.AreEqual(1, gateway.Deletes);
            Assert.IsNull(gateway.Mapping);
        }

        [TestMethod]
        public async Task DeleteResponseDoesNotClaimCleanupWhenRouterRetainsMapping()
        {
            var gateway = new FakeGateway { IgnoreDelete = true };
            await using var mapper = Mapper(gateway);
            var lease = await mapper.RequestMappingAsync(7777, TransportType.Udp);
            await lease.ReleaseAsync();
            Assert.IsFalse(lease.ReleaseSucceeded);
            Assert.IsNotNull(lease.ReleaseError);
            Assert.IsNotNull(gateway.Mapping);
        }

        private static UpnpPortMapper Mapper(FakeGateway gateway, UpnpPortMapperOptions options = null, Uri location = null) =>
            new UpnpPortMapper(options ?? new UpnpPortMapperOptions { AutoRenew = false }, new FakeRoutes(Route),
                new FakeDiscovery { Location = location ?? new Uri("http://192.168.50.1:5000/device.xml") }, gateway);

        private sealed class FakeRoutes : IGatewayRouteProvider
        {
            private readonly GatewayRoute _route;
            public FakeRoutes(GatewayRoute route) { _route = route; }
            public Task<GatewayRoute> GetDefaultRouteAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_route); }
        }
        private sealed class FakeDiscovery : IGatewayDiscovery
        {
            public Uri Location = new Uri("http://192.168.50.1:5000/device.xml");
            public Task<Uri> DiscoverAsync(GatewayRoute route, TimeSpan timeout, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); Assert.AreEqual(Route, route); return Task.FromResult(Location); }
        }
        private sealed class Map
        {
            public IPAddress Address;
            public int Port;
            public string Description;
            public bool Enabled;
        }
        private sealed class FakeGateway : HttpMessageHandler
        {
            public int Requests, Adds, Deletes, LeaseSeconds, AddError;
            public int? OverrideLeaseSeconds;
            public string Protocol, Attack, UrlBase;
            public bool Stall, IgnoreDelete;
            public string Control = "/control";
            public string ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1";
            public Map Mapping;
            public Action AfterAdd;
            public readonly List<string> Bodies = new List<string>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                Assert.AreEqual("192.168.50.1", request.RequestUri.Host);
                if (Stall) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (request.Method == HttpMethod.Get)
                {
                    if (Attack == "redirect") return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://127.0.0.1/private") }, Content = new StringContent("") };
                    if (Attack == "dtd") return Xml("<!DOCTYPE root [<!ENTITY xxe SYSTEM 'file:///never-read-this'>]><root>&xxe;</root>");
                    if (Attack == "oversize") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnboundedLengthContent(new string('x', 2000)) };
                    return Xml(new XDocument(new XElement("root", UrlBase == null ? null : new XElement("URLBase", UrlBase),
                        new XElement("device", new XElement("serviceList", new XElement("service", new XElement("serviceType", ServiceType), new XElement("controlURL", Control)))))).ToString());
                }
                string text = await request.Content.ReadAsStringAsync(); Bodies.Add(text);
                var xml = XDocument.Parse(text);
                string action = request.Headers.GetValues("SOAPACTION").Single().Trim('"').Split('#').Last();
                string Field(string name) => xml.Descendants().First(e => e.Name.LocalName == name).Value;
                if (action == "GetSpecificPortMappingEntry")
                {
                    if (Mapping == null) return Fault(714);
                    return Response(action, new XElement("NewInternalClient", Mapping.Address.ToString()), new XElement("NewInternalPort", Mapping.Port),
                        new XElement("NewEnabled", Mapping.Enabled ? "1" : "0"), new XElement("NewPortMappingDescription", Mapping.Description), new XElement("NewLeaseDuration", OverrideLeaseSeconds ?? (LeaseSeconds == 0 ? 3600 : LeaseSeconds)));
                }
                if (action == "AddPortMapping")
                {
                    Adds++;
                    if (AddError != 0) return Fault(AddError);
                    Assert.AreEqual("", Field("NewRemoteHost"));
                    Assert.AreEqual(Field("NewExternalPort"), Field("NewInternalPort"));
                    LeaseSeconds = int.Parse(Field("NewLeaseDuration")); Protocol = Field("NewProtocol");
                    Mapping = new Map { Address = IPAddress.Parse(Field("NewInternalClient")), Port = int.Parse(Field("NewInternalPort")),
                        Description = Field("NewPortMappingDescription"), Enabled = true };
                    AfterAdd?.Invoke();
                    return Response(action);
                }
                if (action == "DeletePortMapping") { Deletes++; if (!IgnoreDelete) Mapping = null; return Response(action); }
                if (action == "GetExternalIPAddress") return Response(action, new XElement("NewExternalIPAddress", "203.0.113.4"));
                throw new InvalidOperationException("Unexpected SOAP action.");
            }
            private HttpResponseMessage Response(string action, params object[] fields) => Xml(new XElement(XName.Get("Envelope", "http://schemas.xmlsoap.org/soap/envelope/"),
                new XElement(XName.Get("Body", "http://schemas.xmlsoap.org/soap/envelope/"), new XElement(XName.Get(action + "Response", ServiceType), fields))).ToString());
            private static HttpResponseMessage Fault(int code) => Xml(new XElement("Envelope", new XElement("Body", new XElement("Fault",
                new XElement("detail", new XElement("UPnPError", new XElement("errorCode", code)))))).ToString(), HttpStatusCode.InternalServerError);
            private static HttpResponseMessage Xml(string text, HttpStatusCode status = HttpStatusCode.OK) => new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "text/xml") };
        }
        private sealed class UnboundedLengthContent : HttpContent
        {
            private readonly byte[] _bytes;
            public UnboundedLengthContent(string text) { _bytes = Encoding.UTF8.GetBytes(text); }
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => stream.WriteAsync(_bytes, 0, _bytes.Length);
            protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(_bytes, false));
        }
    }
}
