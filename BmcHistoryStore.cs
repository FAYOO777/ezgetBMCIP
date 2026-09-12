#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace EzGetBmcIp
{
    /// <summary>
    /// Versioned evidence required before a BMC endpoint becomes eligible for
    /// the convenience history. Version 1 records were based on a TCP
    /// connection only and deliberately do not satisfy this contract.
    /// </summary>
    internal static class BmcHistoryVerification
    {
        internal const int HttpResponseV1Version = BmcEndpointProbe.StrictVerificationVersion;
        internal const string HttpResponseV1 = BmcEndpointProbe.StrictVerificationKind;
        internal const int AddressReachableV1Version = 3;
        internal const string AddressReachableV1 = "AddressReachableV1";

        internal static bool IsVerifiedHttpResponse(
            int verificationVersion,
            string verificationKind,
            int httpStatusCode)
        {
            return verificationVersion >= HttpResponseV1Version
                && string.Equals(verificationKind, HttpResponseV1, StringComparison.Ordinal)
                && BmcEndpointProbe.IsAcceptedManagementHttpStatus(httpStatusCode);
        }

        internal static bool IsVerifiedReachability(
            int verificationVersion,
            string verificationKind,
            bool pingSucceeded,
            bool httpsPortOpen,
            bool httpPortOpen)
        {
            return verificationVersion >= AddressReachableV1Version
                && string.Equals(verificationKind, AddressReachableV1, StringComparison.Ordinal)
                && (pingSucceeded || httpsPortOpen || httpPortOpen);
        }
    }

    /// <summary>
    /// The last BMC endpoint confirmed reachable through one physical Windows
    /// adapter. This is a local convenience record only; it never contains
    /// credentials and it is not proof that a later response is the same BMC.
    /// </summary>
    [DataContract]
    public sealed class BmcHistoryRecord
    {
        [DataMember(Order = 1)] public string AdapterMac { get; set; }
        [DataMember(Order = 2)] public string AdapterName { get; set; }
        [DataMember(Order = 3)] public string BmcAddress { get; set; }
        [DataMember(Order = 4)] public string LocalAddress { get; set; }
        [DataMember(Order = 5)] public string Mask { get; set; }
        [DataMember(Order = 6)] public string EndpointScheme { get; set; }
        [DataMember(Order = 7)] public int EndpointPort { get; set; }
        [DataMember(Order = 8)] public string BmcMac { get; set; }
        [DataMember(Order = 9)] public DateTime LastConfirmedUtc { get; set; }
        [DataMember(Order = 10)] public int VerificationVersion { get; set; }
        [DataMember(Order = 11)] public string VerificationKind { get; set; }
        [DataMember(Order = 12)] public int HttpStatusCode { get; set; }
        [DataMember(Order = 13)] public bool PingSucceeded { get; set; }
        [DataMember(Order = 14)] public bool HttpsPortOpen { get; set; }
        [DataMember(Order = 15)] public bool HttpPortOpen { get; set; }

        public bool IsForAdapter(WiredAdapter adapter)
        {
            return adapter != null && string.Equals(
                BmcHistoryStore.NormalizeMac(AdapterMac),
                BmcHistoryStore.NormalizeMac(adapter.MacAddress),
                StringComparison.OrdinalIgnoreCase);
        }

        public bool IsSameSubnet(SubnetConfig config)
        {
            if (config == null || !TryGetLocalAddress(out var localAddress))
                return false;

            var configuredAddress = IPAddress.Parse(config.ServerIp).GetAddressBytes();
            var historicalAddress = localAddress.GetAddressBytes();
            return configuredAddress[0] == historicalAddress[0]
                && configuredAddress[1] == historicalAddress[1]
                && configuredAddress[2] == historicalAddress[2];
        }

        public bool TryApplyTo(SubnetConfig config)
        {
            if (config == null || !TryGetLocalAddress(out var localAddress))
                return false;

            var bytes = localAddress.GetAddressBytes();
            if (!BmcHistoryStore.IsPrivateIpv4(bytes) || bytes[3] == 0 || bytes[3] == 100 || bytes[3] == 255)
                return false;

            config.Octet1 = bytes[0];
            config.Octet2 = bytes[1];
            config.Octet3 = bytes[2];
            config.Octet4 = bytes[3];
            return true;
        }

        public bool IsValid()
        {
            var isHttpRecord = BmcHistoryVerification.IsVerifiedHttpResponse(
                    VerificationVersion, VerificationKind, HttpStatusCode);
            var isReachabilityRecord = BmcHistoryVerification.IsVerifiedReachability(
                    VerificationVersion, VerificationKind, PingSucceeded, HttpsPortOpen, HttpPortOpen);
            if ((!isHttpRecord && !isReachabilityRecord)
                || BmcHistoryStore.NormalizeMac(AdapterMac).Length != 12
                || BmcHistoryStore.NormalizeMac(BmcMac).Length != 12
                || !TryGetIpv4(BmcAddress, out _)
                || !TryGetLocalAddress(out var localAddress)
                || !BmcHistoryStore.IsPrivateIpv4(localAddress.GetAddressBytes())
                || !string.Equals(Mask, "255.255.255.0", StringComparison.Ordinal))
            {
                return false;
            }

            if (isHttpRecord &&
                (EndpointPort < 1 || EndpointPort > 65535 ||
                 (EndpointScheme != "http" && EndpointScheme != "https")))
                return false;

            if (isReachabilityRecord &&
                ((HttpsPortOpen && (EndpointScheme != "https" || EndpointPort != 443)) ||
                 (!HttpsPortOpen && HttpPortOpen && (EndpointScheme != "http" || EndpointPort != 80)) ||
                 (!HttpsPortOpen && !HttpPortOpen && (!string.IsNullOrWhiteSpace(EndpointScheme) || EndpointPort != 0))))
                return false;

            return LastConfirmedUtc != default(DateTime);
        }

        private bool TryGetLocalAddress(out IPAddress address)
        {
            return TryGetIpv4(LocalAddress, out address);
        }

        private static bool TryGetIpv4(string text, out IPAddress address)
        {
            return IPAddress.TryParse(text, out address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }
    }

    [DataContract]
    internal sealed class BmcHistoryDocument
    {
        [DataMember(Order = 1)]
        public List<BmcHistoryRecord> Records { get; set; } = new List<BmcHistoryRecord>();
    }

    public static class BmcHistoryStore
    {
        private static readonly object Sync = new object();

        public static string HistoryFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ezgetBMCIP", "bmc-history.json");

        public static BmcHistoryRecord LoadForAdapter(WiredAdapter adapter)
        {
            return LoadForAdapter(adapter, HistoryFilePath);
        }

        public static void SaveConfirmedEndpoint(
            WiredAdapter adapter,
            SubnetConfig subnetConfig,
            IPAddress bmcAddress,
            BmcEndpointProbeResult endpoint,
            string bmcMac)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            if (subnetConfig == null) throw new ArgumentNullException(nameof(subnetConfig));
            if (bmcAddress == null) throw new ArgumentNullException(nameof(bmcAddress));
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));

            SaveConfirmedEndpoint(adapter, subnetConfig, bmcAddress, endpoint, bmcMac, HistoryFilePath);
        }

        public static void SaveReachableAddress(
            WiredAdapter adapter,
            SubnetConfig subnetConfig,
            IPAddress bmcAddress,
            BmcReachabilityResult reachability,
            string bmcMac)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            if (subnetConfig == null) throw new ArgumentNullException(nameof(subnetConfig));
            if (bmcAddress == null) throw new ArgumentNullException(nameof(bmcAddress));
            if (reachability == null) throw new ArgumentNullException(nameof(reachability));

            SaveReachableAddress(adapter, subnetConfig, bmcAddress, reachability, bmcMac, HistoryFilePath);
        }

        internal static BmcHistoryRecord LoadForAdapter(WiredAdapter adapter, string path)
        {
            if (adapter == null || string.IsNullOrWhiteSpace(path))
                return null;

            lock (Sync)
            {
                var document = TryRead(path);
                if (document == null)
                    return null;

                return (document.Records ?? new List<BmcHistoryRecord>())
                    .Where(record => record != null && record.IsValid() && record.IsForAdapter(adapter))
                    .OrderByDescending(record => record.LastConfirmedUtc)
                    .FirstOrDefault();
            }
        }

        internal static void SaveConfirmedEndpoint(
            WiredAdapter adapter,
            SubnetConfig subnetConfig,
            IPAddress bmcAddress,
            BmcEndpointProbeResult endpoint,
            string bmcMac,
            string path)
        {
            if (adapter == null || subnetConfig == null || bmcAddress == null || endpoint == null)
                throw new ArgumentNullException("A BMC history record requires adapter, subnet, endpoint, and address.");
            if (bmcAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ArgumentException("Only IPv4 BMC endpoints can be recorded.", nameof(bmcAddress));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A history path is required.", nameof(path));
            if (!BmcHistoryVerification.IsVerifiedHttpResponse(
                    endpoint.VerificationVersion,
                    endpoint.VerificationKind,
                    endpoint.HttpStatusCode))
            {
                throw new InvalidOperationException(
                    "Only an endpoint verified by an HTTP response can be stored in BMC history.");
            }

            var verifiedPeerMac = NormalizeMac(endpoint.PeerMac);
            if (verifiedPeerMac.Length != 12)
                throw new InvalidOperationException(
                    "A confirmed BMC history endpoint requires the ARP-verified peer MAC address.");

            // The legacy parameter is retained for source compatibility. When
            // a DHCP lease MAC is available it must agree with the direct ARP
            // evidence, but the persisted identity always comes from the
            // verified endpoint probe rather than the lease cache.
            var providedDhcpMac = NormalizeMac(bmcMac);
            if (providedDhcpMac.Length > 0 && !string.Equals(
                    providedDhcpMac, verifiedPeerMac, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The DHCP lease MAC does not match the ARP-verified BMC endpoint.");
            }

            var record = new BmcHistoryRecord
            {
                AdapterMac = NormalizeMac(adapter.MacAddress),
                AdapterName = adapter.DisplayName ?? string.Empty,
                BmcAddress = bmcAddress.ToString(),
                LocalAddress = subnetConfig.ServerIp,
                Mask = subnetConfig.Mask,
                EndpointScheme = endpoint.Scheme,
                EndpointPort = endpoint.Port,
                BmcMac = verifiedPeerMac,
                LastConfirmedUtc = DateTime.UtcNow,
                VerificationVersion = endpoint.VerificationVersion,
                VerificationKind = endpoint.VerificationKind,
                HttpStatusCode = endpoint.HttpStatusCode
            };

            if (!record.IsValid())
                throw new InvalidOperationException("The confirmed BMC endpoint cannot be stored as a valid history record.");

            lock (Sync)
            {
                var document = TryRead(path) ?? new BmcHistoryDocument();
                document.Records = document.Records ?? new List<BmcHistoryRecord>();
                // A successful v2 write is the migration point: old TCP-only,
                // malformed, and replaced records are removed in the same
                // atomic document update. They must never become suggestions.
                document.Records.RemoveAll(item =>
                    item == null || !item.IsValid() || item.IsForAdapter(adapter));
                document.Records.Add(record);
                WriteAtomically(path, document);
            }
        }

        internal static void SaveReachableAddress(
            WiredAdapter adapter,
            SubnetConfig subnetConfig,
            IPAddress bmcAddress,
            BmcReachabilityResult reachability,
            string bmcMac,
            string path)
        {
            if (adapter == null || subnetConfig == null || bmcAddress == null || reachability == null)
                throw new ArgumentNullException("A BMC reachability history record requires adapter, subnet, result, and address.");
            if (bmcAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ArgumentException("Only IPv4 BMC addresses can be recorded.", nameof(bmcAddress));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A history path is required.", nameof(path));
            if (!reachability.IsReachable)
                throw new InvalidOperationException("Only a reachable BMC address can be stored in history.");

            var verifiedPeerMac = NormalizeMac(bmcMac);
            if (verifiedPeerMac.Length != 12)
                throw new InvalidOperationException("A reachable BMC history address requires the DHCP client MAC address.");

            var hasHttps = reachability.HttpsPortOpen;
            var hasHttp = !hasHttps && reachability.HttpPortOpen;
            var record = new BmcHistoryRecord
            {
                AdapterMac = NormalizeMac(adapter.MacAddress),
                AdapterName = adapter.DisplayName ?? string.Empty,
                BmcAddress = bmcAddress.ToString(),
                LocalAddress = subnetConfig.ServerIp,
                Mask = subnetConfig.Mask,
                EndpointScheme = hasHttps ? "https" : hasHttp ? "http" : string.Empty,
                EndpointPort = hasHttps ? 443 : hasHttp ? 80 : 0,
                BmcMac = verifiedPeerMac,
                LastConfirmedUtc = DateTime.UtcNow,
                VerificationVersion = BmcHistoryVerification.AddressReachableV1Version,
                VerificationKind = BmcHistoryVerification.AddressReachableV1,
                HttpStatusCode = 0,
                PingSucceeded = reachability.PingSucceeded,
                HttpsPortOpen = reachability.HttpsPortOpen,
                HttpPortOpen = reachability.HttpPortOpen
            };

            if (!record.IsValid())
                throw new InvalidOperationException("The reachable BMC address cannot be stored as a valid history record.");

            lock (Sync)
            {
                var document = TryRead(path) ?? new BmcHistoryDocument();
                document.Records = document.Records ?? new List<BmcHistoryRecord>();
                document.Records.RemoveAll(item =>
                    item == null || !item.IsValid() || item.IsForAdapter(adapter));
                document.Records.Add(record);
                WriteAtomically(path, document);
            }
        }

        internal static string NormalizeMac(string value)
        {
            return new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        }

        internal static bool IsPrivateIpv4(byte[] bytes)
        {
            return bytes != null && bytes.Length == 4 &&
                (bytes[0] == 10 ||
                 (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                 (bytes[0] == 192 && bytes[1] == 168));
        }

        private static BmcHistoryDocument TryRead(string path)
        {
            if (!File.Exists(path))
                return new BmcHistoryDocument();

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var serializer = new DataContractJsonSerializer(typeof(BmcHistoryDocument));
                    var document = serializer.ReadObject(stream) as BmcHistoryDocument;
                    if (document != null && document.Records == null)
                        document.Records = new List<BmcHistoryRecord>();
                    return document;
                }
            }
            catch
            {
                // A damaged convenience record must never block normal BMC discovery.
                return null;
            }
        }

        private static void WriteAtomically(string path, BmcHistoryDocument document)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("The BMC history directory is unavailable.");

            Directory.CreateDirectory(directory);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var serializer = new DataContractJsonSerializer(typeof(BmcHistoryDocument));
                    serializer.WriteObject(stream, document);
                    stream.Flush();
                }

                File.Copy(temporaryPath, path, true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
    }
}
