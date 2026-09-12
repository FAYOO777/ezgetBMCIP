#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EzGetBmcIp
{

/// <summary>
/// The proof level attached to a management endpoint. A TCP handshake alone
/// is deliberately not an endpoint proof: transparent proxies and TUN
/// adapters can acknowledge it without a BMC being present.
/// </summary>
public sealed class BmcEndpointProbeResult
{
    public string Url { get; set; } = string.Empty;
    public string Scheme { get; set; } = string.Empty;
    public int Port { get; set; }

    // v2 means route + ARP + a complete HTTP response were observed.
    public int VerificationVersion { get; set; }
    public string VerificationKind { get; set; } = string.Empty;
    public int HttpStatusCode { get; set; }
    public string PeerMac { get; set; } = string.Empty;
    public BmcEndpointProbeEvidence Evidence { get; set; }
}

public sealed class BmcEndpointCandidate
{
    public string Scheme { get; set; } = string.Empty;
    public int Port { get; set; }
}

/// <summary>
/// All network identity needed to prove that the connection stays on the
/// selected temporary BMC adapter. SourceAddress and InterfaceIndex are
/// supplied by the caller after it has configured the adapter.
/// </summary>
public sealed class BmcEndpointProbeRequest
{
    public IPAddress TargetAddress { get; set; }
    public IPAddress SourceAddress { get; set; }
    public string AdapterName { get; set; } = string.Empty;
    public string AdapterId { get; set; } = string.Empty;
    public int InterfaceIndex { get; set; }

    // Optional DHCP client MAC. When present it must match the ARP neighbor.
    public string ExpectedPeerMac { get; set; } = string.Empty;
}

public enum EndpointProbeFailureStage
{
    None,
    InvalidRequest,
    NetworkEvidenceUnavailable,
    RouteLookupFailed,
    RouteInterfaceMismatch,
    NeighborUnresolved,
    NeighborInvalid,
    PeerMacMismatch,
    SocketBindFailed,
    SocketInterfaceBindingFailed,
    TcpConnectFailed,
    TlsHandshakeFailed,
    HttpRequestFailed,
    HttpResponseMissing,
    InvalidHttpResponse,
    UnexpectedHttpStatus
}

/// <summary>
/// Raw route and neighbor facts. It is separate from transport probing so
/// tests can emulate route/TUN/ARP scenarios without changing the host.
/// </summary>
public sealed class EndpointNetworkEvidence
{
    public bool RouteResolved { get; set; }
    public int BestRouteInterfaceIndex { get; set; }
    public bool NeighborResolved { get; set; }
    public string NeighborMac { get; set; } = string.Empty;
    public int NativeErrorCode { get; set; }
    public string Error { get; set; } = string.Empty;
}

public sealed class BmcEndpointProbeEvidence
{
    public string TargetAddress { get; set; } = string.Empty;
    public string SourceAddress { get; set; } = string.Empty;
    public string AdapterName { get; set; } = string.Empty;
    public string AdapterId { get; set; } = string.Empty;
    public int RequestedInterfaceIndex { get; set; }
    public bool RouteResolved { get; set; }
    public int BestRouteInterfaceIndex { get; set; }
    public bool NeighborResolved { get; set; }
    public string NeighborMac { get; set; } = string.Empty;
    public bool? PeerMacMatchesExpected { get; set; }
    public string ActualLocalAddress { get; set; } = string.Empty;
    public bool TcpConnected { get; set; }
    public bool TlsEstablished { get; set; }
    public string TlsProtocol { get; set; } = string.Empty;
    public bool HttpResponseReceived { get; set; }
    public int HttpStatusCode { get; set; }
    public EndpointProbeFailureStage FailureStage { get; set; }
    public string FailureDetail { get; set; } = string.Empty;
}

public sealed class BmcEndpointProbeOutcome
{
    public BmcEndpointProbeResult VerifiedEndpoint { get; set; }
    public BmcEndpointProbeEvidence Evidence { get; set; }
    public int Attempts { get; set; }
}

public interface IEndpointNetworkEvidenceProvider
{
    EndpointNetworkEvidence Inspect(BmcEndpointProbeRequest request);
}

/// <summary>
/// Windows-only read/probe implementation. GetBestInterface and SendARP do
/// not change adapter configuration or firewall policy.
/// </summary>
public sealed class NativeEndpointNetworkEvidenceProvider : IEndpointNetworkEvidenceProvider
{
    public static readonly NativeEndpointNetworkEvidenceProvider Instance = new NativeEndpointNetworkEvidenceProvider();

    public EndpointNetworkEvidence Inspect(BmcEndpointProbeRequest request)
    {
        var evidence = new EndpointNetworkEvidence();
        if (request == null || request.TargetAddress == null || request.SourceAddress == null)
        {
            evidence.Error = "A target and source IPv4 address are required.";
            return evidence;
        }

        try
        {
            uint routeIndex;
            var routeStatus = GetBestInterface(ToNativeIpv4ForWindows(request.TargetAddress), out routeIndex);
            evidence.NativeErrorCode = routeStatus;
            if (routeStatus != 0)
            {
                evidence.Error = "GetBestInterface failed with " + routeStatus + ".";
                return evidence;
            }

            evidence.RouteResolved = true;
            evidence.BestRouteInterfaceIndex = unchecked((int)routeIndex);

            var mac = new byte[32];
            var macLength = mac.Length;
            var arpStatus = SendARP(
                ToNativeIpv4ForWindows(request.TargetAddress),
                ToNativeIpv4ForWindows(request.SourceAddress),
                mac,
                ref macLength);
            if (arpStatus != 0)
            {
                evidence.NativeErrorCode = arpStatus;
                evidence.Error = "SendARP failed with " + arpStatus + ".";
                return evidence;
            }

            if (macLength <= 0 || macLength > mac.Length)
            {
                evidence.Error = "SendARP returned an invalid neighbor length.";
                return evidence;
            }

            evidence.NeighborMac = NormalizeMac(BitConverter.ToString(mac, 0, macLength));
            evidence.NeighborResolved = IsUsableMac(evidence.NeighborMac);
            if (!evidence.NeighborResolved)
                evidence.Error = "SendARP returned an invalid neighbor MAC.";
            return evidence;
        }
        catch (Exception ex)
        {
            evidence.Error = ex.GetType().Name + ": " + ex.Message;
            return evidence;
        }
    }

    // IPAddr is passed to iphlpapi as a native UInt32. On supported Windows
    // hosts the raw bytes of the integer must remain the network-order IPv4
    // octets, which is why little-endian machines intentionally do not reverse.
    internal static uint ToNativeIpv4ForWindows(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] physicalAddress, ref int physicalAddressLength);

    internal static string NormalizeMac(string value)
    {
        return new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
    }

    internal static bool IsUsableMac(string value)
    {
        var normalized = NormalizeMac(value);
        return normalized.Length == 12
            && !string.Equals(normalized, "000000000000", StringComparison.Ordinal)
            && !string.Equals(normalized, "FFFFFFFFFFFF", StringComparison.Ordinal);
    }
}

public static class BmcEndpointProbe
{
    public const int StrictVerificationVersion = 2;
    public const string StrictVerificationKind = "HttpResponseV1";
    private static readonly TimeSpan MaximumPhaseDuration = TimeSpan.FromSeconds(3);
    // SendARP/GetBestInterface are synchronous native calls.  Keep one native
    // inspection in flight so a slow Windows call cannot accumulate a queue of
    // overlapping probes after cancellation or a short retry interval.
    private static readonly SemaphoreSlim NetworkEvidenceGate = new SemaphoreSlim(1, 1);

    private static readonly IReadOnlyList<BmcEndpointCandidate> DefaultCandidates = new[]
    {
        new BmcEndpointCandidate { Scheme = "https", Port = 443 },
        new BmcEndpointCandidate { Scheme = "http", Port = 80 }
    };

    private sealed class CompatibilityRequestResult
    {
        public bool Success { get; set; }
        public BmcEndpointProbeRequest Request { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Strict direct probe: route interface, ARP neighbor, source bind and a
    /// final BMC-oriented HTTP response must all succeed before an endpoint
    /// is returned. A TCP handshake, an informational response, a proxy
    /// challenge, or a server error is not enough to claim that the BMC
    /// management page is reachable.
    /// </summary>
    public static async Task<BmcEndpointProbeOutcome> ProbeForEndpointAsync(
        BmcEndpointProbeRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string> logger = null,
        IReadOnlyList<BmcEndpointCandidate> candidates = null,
        IEndpointNetworkEvidenceProvider networkEvidenceProvider = null)
    {
        var endpoints = candidates ?? DefaultCandidates;
        ValidateCandidates(endpoints, nameof(candidates));
        var initialEvidence = CreateEvidence(request);
        string requestError;
        if (!TryValidateStrictRequest(request, out requestError))
        {
            initialEvidence.FailureStage = EndpointProbeFailureStage.InvalidRequest;
            initialEvidence.FailureDetail = requestError;
            logger?.Invoke("BMC direct endpoint probe rejected: " + requestError);
            return new BmcEndpointProbeOutcome { Evidence = initialEvidence, Attempts = 0 };
        }

        if (timeout <= TimeSpan.Zero)
        {
            initialEvidence.FailureStage = EndpointProbeFailureStage.InvalidRequest;
            initialEvidence.FailureDetail = "A positive probe timeout is required.";
            return new BmcEndpointProbeOutcome { Evidence = initialEvidence, Attempts = 0 };
        }

        var provider = networkEvidenceProvider ?? NativeEndpointNetworkEvidenceProvider.Instance;
        var deadline = DateTime.UtcNow + timeout;
        var attempts = 0;
        var lastEvidence = initialEvidence;
        var lastSlowEvidenceLogUtc = DateTime.MinValue;
        logger?.Invoke("BMC direct endpoint probe started: target=" + request.TargetAddress +
            ", source=" + request.SourceAddress +
            ", interface=" + request.InterfaceIndex +
            ", timeout=" + timeout.TotalSeconds + "s");

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            var evidence = CreateEvidence(request);
            EndpointNetworkEvidence networkEvidence;
            try
            {
                networkEvidence = await InspectNetworkEvidenceAsync(
                    provider,
                    request,
                    deadline,
                    cancellationToken,
                    elapsed =>
                    {
                        var now = DateTime.UtcNow;
                        if (elapsed >= TimeSpan.FromMilliseconds(500)
                            && now - lastSlowEvidenceLogUtc >= TimeSpan.FromSeconds(5))
                        {
                            lastSlowEvidenceLogUtc = now;
                            logger?.Invoke("BMC network evidence inspection slow: " +
                                elapsed.TotalMilliseconds.ToString("0") + "ms");
                        }
                    }).ConfigureAwait(false);
                if (networkEvidence == null)
                {
                    evidence.FailureStage = EndpointProbeFailureStage.NetworkEvidenceUnavailable;
                    evidence.FailureDetail = "Network evidence inspection timed out.";
                    lastEvidence = evidence;
                    break;
                }
            }
            catch (Exception ex)
            {
                evidence.FailureStage = EndpointProbeFailureStage.NetworkEvidenceUnavailable;
                evidence.FailureDetail = ex.GetType().Name + ": " + ex.Message;
                lastEvidence = evidence;
                await DelayBetweenAttemptsAsync(deadline, cancellationToken).ConfigureAwait(false);
                continue;
            }

            ApplyNetworkEvidence(evidence, networkEvidence);
            if (!networkEvidence.RouteResolved)
            {
                evidence.FailureStage = EndpointProbeFailureStage.RouteLookupFailed;
                evidence.FailureDetail = networkEvidence.Error;
                lastEvidence = evidence;
                await DelayBetweenAttemptsAsync(deadline, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (networkEvidence.BestRouteInterfaceIndex != request.InterfaceIndex)
            {
                evidence.FailureStage = EndpointProbeFailureStage.RouteInterfaceMismatch;
                evidence.FailureDetail = "Best route interface " + networkEvidence.BestRouteInterfaceIndex +
                    " does not match selected interface " + request.InterfaceIndex + ".";
                lastEvidence = evidence;
                await DelayBetweenAttemptsAsync(deadline, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!networkEvidence.NeighborResolved)
            {
                evidence.FailureStage = EndpointProbeFailureStage.NeighborUnresolved;
                evidence.FailureDetail = string.IsNullOrWhiteSpace(networkEvidence.Error)
                    ? "No ARP neighbor was resolved for the target."
                    : networkEvidence.Error;
                lastEvidence = evidence;
                await DelayBetweenAttemptsAsync(deadline, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!NativeEndpointNetworkEvidenceProvider.IsUsableMac(networkEvidence.NeighborMac))
            {
                evidence.FailureStage = EndpointProbeFailureStage.NeighborInvalid;
                evidence.FailureDetail = "The ARP neighbor MAC is invalid.";
                lastEvidence = evidence;
                await DelayBetweenAttemptsAsync(deadline, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var expectedMac = NativeEndpointNetworkEvidenceProvider.NormalizeMac(request.ExpectedPeerMac);
            evidence.PeerMacMatchesExpected = expectedMac.Length == 0
                ? (bool?)null
                : string.Equals(expectedMac, evidence.NeighborMac, StringComparison.OrdinalIgnoreCase);
            if (evidence.PeerMacMatchesExpected == false)
            {
                evidence.FailureStage = EndpointProbeFailureStage.PeerMacMismatch;
                evidence.FailureDetail = "DHCP client MAC does not match the ARP neighbor.";
                lastEvidence = evidence;
                await DelayBetweenAttemptsAsync(deadline, cancellationToken).ConfigureAwait(false);
                continue;
            }

            BmcEndpointProbeEvidence unexpectedStatusEvidence = null;
            foreach (var candidate in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateEvidence = CopyEvidence(evidence);
                var result = await ProbeCandidateAsync(request, candidate, deadline, cancellationToken, candidateEvidence).ConfigureAwait(false);
                if (result != null)
                {
                    result.PeerMac = candidateEvidence.NeighborMac;
                    result.Evidence = candidateEvidence;
                    logger?.Invoke("BMC direct management service response: " + result.Url +
                        " status=" + result.HttpStatusCode +
                        " mac=" + result.PeerMac +
                        " attempt=" + attempts);
                    return new BmcEndpointProbeOutcome
                    {
                        VerifiedEndpoint = result,
                        Evidence = candidateEvidence,
                        Attempts = attempts
                    };
                }

                // Let the other configured scheme have a chance (for example
                // an unusual HTTPS root plus a normal HTTP root), but do not
                // spin until timeout and overwrite an actionable final HTTP
                // response with a later connection-refused diagnostic.
                if (candidateEvidence.FailureStage == EndpointProbeFailureStage.UnexpectedHttpStatus)
                    unexpectedStatusEvidence = candidateEvidence;
                lastEvidence = candidateEvidence;
            }

            if (unexpectedStatusEvidence != null)
            {
                logger?.Invoke("BMC direct endpoint probe rejected HTTP " +
                    unexpectedStatusEvidence.HttpStatusCode + " as a non-management response.");
                return new BmcEndpointProbeOutcome
                {
                    Evidence = unexpectedStatusEvidence,
                    Attempts = attempts
                };
            }

            await DelayBetweenAttemptsAsync(deadline, cancellationToken).ConfigureAwait(false);
        }

        logger?.Invoke("BMC direct endpoint probe timed out after " + attempts +
            " attempt(s); stage=" + lastEvidence.FailureStage +
            (string.IsNullOrWhiteSpace(lastEvidence.FailureDetail) ? string.Empty : " detail=" + lastEvidence.FailureDetail));
        return new BmcEndpointProbeOutcome { Evidence = lastEvidence, Attempts = attempts };
    }

    public static async Task<BmcEndpointProbeResult> WaitForEndpointAsync(
        BmcEndpointProbeRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string> logger = null,
        IReadOnlyList<BmcEndpointCandidate> candidates = null,
        IEndpointNetworkEvidenceProvider networkEvidenceProvider = null)
    {
        var outcome = await ProbeForEndpointAsync(
            request, timeout, cancellationToken, logger, candidates, networkEvidenceProvider).ConfigureAwait(false);
        return outcome.VerifiedEndpoint;
    }

    /// <summary>
    /// Compatibility overload for previous address-only callers. It resolves
    /// the OS route/source and still performs strict validation. New callers
    /// should pass BmcEndpointProbeRequest with their selected adapter.
    /// </summary>
    public static async Task<BmcEndpointProbeResult> WaitForEndpointAsync(
        IPAddress ipAddress,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string> logger = null,
        IReadOnlyList<BmcEndpointCandidate> candidates = null)
    {
        var compatibility = await Task.Run(() =>
        {
            BmcEndpointProbeRequest resolvedRequest;
            string resolvedError;
            var success = TryCreateCompatibilityRequest(ipAddress, out resolvedRequest, out resolvedError);
            return new CompatibilityRequestResult
            {
                Success = success,
                Request = resolvedRequest,
                Error = resolvedError
            };
        }, cancellationToken).ConfigureAwait(false);
        if (!compatibility.Success)
        {
            logger?.Invoke("BMC endpoint compatibility probe unavailable: " + compatibility.Error);
            return null;
        }

        return await WaitForEndpointAsync(compatibility.Request, timeout, cancellationToken, logger, candidates).ConfigureAwait(false);
    }

    private static async Task<BmcEndpointProbeResult> ProbeCandidateAsync(
        BmcEndpointProbeRequest request,
        BmcEndpointCandidate candidate,
        DateTime deadline,
        CancellationToken cancellationToken,
        BmcEndpointProbeEvidence evidence)
    {
        Socket socket = null;
        Stream stream = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(new IPEndPoint(request.SourceAddress, 0));
                evidence.ActualLocalAddress = ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
            }
            catch (Exception ex)
            {
                evidence.FailureStage = EndpointProbeFailureStage.SocketBindFailed;
                evidence.FailureDetail = ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            try
            {
                // Windows IP_UNICAST_IF expects the index in network byte order.
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    (SocketOptionName)31 /* IP_UNICAST_IF */,
                    IPAddress.HostToNetworkOrder(request.InterfaceIndex));
            }
            catch (Exception ex)
            {
                evidence.FailureStage = EndpointProbeFailureStage.SocketInterfaceBindingFailed;
                evidence.FailureDetail = ex.GetType().Name + ": " + ex.Message;
                return null;
            }

            if (!await ConnectAsync(socket, new IPEndPoint(request.TargetAddress, candidate.Port), CreatePhaseDeadline(deadline), cancellationToken).ConfigureAwait(false))
            {
                evidence.FailureStage = EndpointProbeFailureStage.TcpConnectFailed;
                evidence.FailureDetail = "TCP connection did not complete before the attempt timeout.";
                return null;
            }
            evidence.TcpConnected = true;
            evidence.ActualLocalAddress = ((IPEndPoint)socket.LocalEndPoint).Address.ToString();

            stream = new NetworkStream(socket, false);
            if (candidate.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                var ssl = new SslStream(stream, false, (_, __, ___, ____) => true);
                stream = ssl;
                try
                {
                    if (!await AuthenticateTlsAsync(ssl, request.TargetAddress.ToString(), CreatePhaseDeadline(deadline), cancellationToken).ConfigureAwait(false))
                    {
                        evidence.FailureStage = EndpointProbeFailureStage.TlsHandshakeFailed;
                        evidence.FailureDetail = "TLS handshake did not complete before the attempt timeout.";
                        return null;
                    }
                }
                catch (AuthenticationException ex)
                {
                    evidence.FailureStage = EndpointProbeFailureStage.TlsHandshakeFailed;
                    evidence.FailureDetail = ex.GetType().Name + ": " + ex.Message;
                    return null;
                }
                catch (IOException ex)
                {
                    evidence.FailureStage = EndpointProbeFailureStage.TlsHandshakeFailed;
                    evidence.FailureDetail = ex.GetType().Name + ": " + ex.Message;
                    return null;
                }

                evidence.TlsEstablished = true;
                evidence.TlsProtocol = ssl.SslProtocol.ToString();
            }
            else if (!candidate.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                evidence.FailureStage = EndpointProbeFailureStage.InvalidRequest;
                evidence.FailureDetail = "Only HTTP and HTTPS endpoint candidates are supported.";
                return null;
            }

            var requestText = "GET / HTTP/1.1\r\nHost: " + request.TargetAddress +
                "\r\nConnection: close\r\nUser-Agent: ezgetBMCIP/1.5.7\r\n\r\n";
            var requestBytes = Encoding.ASCII.GetBytes(requestText);
            if (!await WriteAsync(stream, requestBytes, CreatePhaseDeadline(deadline), cancellationToken).ConfigureAwait(false))
            {
                evidence.FailureStage = EndpointProbeFailureStage.HttpRequestFailed;
                evidence.FailureDetail = "HTTP request could not be written before the attempt timeout.";
                return null;
            }

            var responseHead = await ReadFinalResponseHeadAsync(stream, CreatePhaseDeadline(deadline), cancellationToken).ConfigureAwait(false);
            if (responseHead == null)
            {
                evidence.FailureStage = EndpointProbeFailureStage.HttpResponseMissing;
                evidence.FailureDetail = "No complete HTTP response header was received.";
                return null;
            }

            int statusCode;
            if (!TryParseHttpStatus(responseHead, out statusCode))
            {
                evidence.FailureStage = EndpointProbeFailureStage.InvalidHttpResponse;
                evidence.FailureDetail = "The received response did not contain a valid HTTP status line.";
                return null;
            }

            evidence.HttpResponseReceived = true;
            evidence.HttpStatusCode = statusCode;
            if (!IsAcceptedManagementHttpStatus(statusCode))
            {
                evidence.FailureStage = EndpointProbeFailureStage.UnexpectedHttpStatus;
                evidence.FailureDetail = "HTTP " + statusCode +
                    " is not an accepted BMC management response (expected 2xx, 3xx, 401, or 403).";
                return null;
            }

            evidence.FailureStage = EndpointProbeFailureStage.None;
            evidence.FailureDetail = string.Empty;
            return new BmcEndpointProbeResult
            {
                Url = BuildUrl(candidate.Scheme, request.TargetAddress, candidate.Port),
                Scheme = candidate.Scheme.ToLowerInvariant(),
                Port = candidate.Port,
                VerificationVersion = StrictVerificationVersion,
                VerificationKind = StrictVerificationKind,
                HttpStatusCode = statusCode
            };
        }
        catch (SocketException ex)
        {
            evidence.FailureStage = evidence.TcpConnected
                ? EndpointProbeFailureStage.HttpResponseMissing
                : EndpointProbeFailureStage.TcpConnectFailed;
            evidence.FailureDetail = ex.SocketErrorCode + ": " + ex.Message;
            return null;
        }
        catch (IOException ex)
        {
            evidence.FailureStage = evidence.TlsEstablished
                ? EndpointProbeFailureStage.HttpResponseMissing
                : EndpointProbeFailureStage.TlsHandshakeFailed;
            evidence.FailureDetail = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
        finally
        {
            try { stream?.Dispose(); } catch { }
            try { socket?.Dispose(); } catch { }
        }
    }

    private static async Task<bool> ConnectAsync(Socket socket, EndPoint target, DateTime deadline, CancellationToken cancellationToken)
    {
        // Socket.ConnectAsync(EndPoint) was only added after .NET Framework
        // 4.6. Begin/EndConnect keeps the shared probe compatible with Legacy.
        var connectTask = Task.Factory.FromAsync(
            (callback, state) => socket.BeginConnect(target, callback, state),
            socket.EndConnect,
            null);
        return await CompleteBeforeDeadlineAsync(connectTask, deadline, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> AuthenticateTlsAsync(SslStream stream, string targetHost, DateTime deadline, CancellationToken cancellationToken)
    {
        var authenticateTask = stream.AuthenticateAsClientAsync(targetHost);
        return await CompleteBeforeDeadlineAsync(authenticateTask, deadline, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> WriteAsync(Stream stream, byte[] bytes, DateTime deadline, CancellationToken cancellationToken)
    {
        var writeTask = stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        return await CompleteBeforeDeadlineAsync(writeTask, deadline, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a final HTTP response header. Informational 1xx headers are
    /// explicitly skipped: they acknowledge protocol progress, but do not
    /// answer the management request. The terminator search is intentionally
    /// not limited to the tail of a socket read because a response header and
    /// body can arrive in one receive buffer.
    /// </summary>
    private static async Task<string> ReadFinalResponseHeadAsync(Stream stream, DateTime deadline, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        var buffer = new byte[512];
        while (bytes.Count < 16384)
        {
            int headerLength;
            while ((headerLength = FindHeaderLength(bytes)) > 0)
            {
                var responseHead = Encoding.ASCII.GetString(bytes.GetRange(0, headerLength).ToArray());
                bytes.RemoveRange(0, headerLength);

                int statusCode;
                if (!TryParseHttpStatus(responseHead, out statusCode))
                    return responseHead;

                // A server is allowed to send informational headers before
                // its final reply. Do not let 100 Continue become an endpoint
                // success, and do keep reading for the final response.
                if (statusCode >= 100 && statusCode <= 199)
                    continue;

                return responseHead;
            }

            var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            if (!await CompleteBeforeDeadlineAsync(readTask, deadline, cancellationToken).ConfigureAwait(false))
                return null;
            var count = readTask.Result;
            if (count <= 0)
                return null;
            for (var i = 0; i < count; i++)
                bytes.Add(buffer[i]);
        }
        return null;
    }

    private static int FindHeaderLength(List<byte> bytes)
    {
        for (var i = 0; i <= bytes.Count - 4; i++)
        {
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                return i + 4;
        }

        return 0;
    }

    private static async Task<bool> CompleteBeforeDeadlineAsync(Task task, DateTime deadline, CancellationToken cancellationToken)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return false;
        var timeoutTask = Task.Delay(remaining, cancellationToken);
        var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
        if (completed != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveFault(task);
            return false;
        }
        await task.ConfigureAwait(false);
        return true;
    }

    private static async Task DelayBetweenAttemptsAsync(DateTime deadline, CancellationToken cancellationToken)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(800) ? remaining : TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<EndpointNetworkEvidence> InspectNetworkEvidenceAsync(
        IEndpointNetworkEvidenceProvider provider,
        BmcEndpointProbeRequest request,
        DateTime deadline,
        CancellationToken cancellationToken,
        Action<TimeSpan> slowInspectionLogger)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return null;

        var acquired = await NetworkEvidenceGate.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        if (!acquired)
            return null;
        var releaseWhenFinished = true;
        Task<EndpointNetworkEvidence> inspectionTask = null;
        var inspectionStopwatch = Stopwatch.StartNew();
        var slowReported = false;
        Action reportSlow = () =>
        {
            if (slowReported)
                return;
            slowReported = true;
            slowInspectionLogger?.Invoke(inspectionStopwatch.Elapsed);
        };
        try
        {
            // The provider interface is intentionally synchronous for Legacy
            // and existing tests.  The call itself must never run on WPF's
            // dispatcher, and cannot be cancelled safely once native code has
            // entered Windows, so the gate is held until it naturally returns.
            inspectionTask = Task.Run(() => provider.Inspect(request));
            var timeoutTask = Task.Delay(remaining, cancellationToken);
            var completed = await Task.WhenAny(inspectionTask, timeoutTask).ConfigureAwait(false);
            if (completed != inspectionTask)
            {
                ObserveFault(inspectionTask);
                releaseWhenFinished = false;
                _ = inspectionTask.ContinueWith(
                    _ =>
                    {
                        reportSlow();
                        NetworkEvidenceGate.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            var result = await inspectionTask.ConfigureAwait(false);
            reportSlow();
            return result;
        }
        finally
        {
            if (releaseWhenFinished)
                NetworkEvidenceGate.Release();
        }
    }

    private static DateTime CreatePhaseDeadline(DateTime overallDeadline)
    {
        var phaseDeadline = DateTime.UtcNow + MaximumPhaseDuration;
        return phaseDeadline < overallDeadline ? phaseDeadline : overallDeadline;
    }

    private static void ObserveFault(Task task)
    {
        task.ContinueWith(
            completed => { var ignored = completed.Exception; },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    private static bool TryParseHttpStatus(string responseHead, out int statusCode)
    {
        statusCode = 0;
        if (string.IsNullOrWhiteSpace(responseHead))
            return false;
        var lineEnd = responseHead.IndexOf("\r\n", StringComparison.Ordinal);
        var line = lineEnd < 0 ? responseHead : responseHead.Substring(0, lineEnd);
        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out statusCode)
            && statusCode >= 100 && statusCode <= 599;
    }

    /// <summary>
    /// Status codes that constitute a final BMC management-service response
    /// for the root GET used by this tool. Authentication responses are
    /// expected for many BMCs; proxy authentication and server errors are
    /// deliberately not accepted as proof of a usable management page.
    /// </summary>
    public static bool IsAcceptedManagementHttpStatus(int statusCode)
    {
        return (statusCode >= 200 && statusCode <= 399)
            || statusCode == 401
            || statusCode == 403;
    }

    private static void ApplyNetworkEvidence(BmcEndpointProbeEvidence destination, EndpointNetworkEvidence source)
    {
        destination.RouteResolved = source != null && source.RouteResolved;
        destination.BestRouteInterfaceIndex = source?.BestRouteInterfaceIndex ?? 0;
        destination.NeighborResolved = source != null && source.NeighborResolved;
        destination.NeighborMac = NativeEndpointNetworkEvidenceProvider.NormalizeMac(source?.NeighborMac);
    }

    private static BmcEndpointProbeEvidence CreateEvidence(BmcEndpointProbeRequest request)
    {
        return new BmcEndpointProbeEvidence
        {
            TargetAddress = request?.TargetAddress?.ToString() ?? string.Empty,
            SourceAddress = request?.SourceAddress?.ToString() ?? string.Empty,
            AdapterName = request?.AdapterName ?? string.Empty,
            AdapterId = request?.AdapterId ?? string.Empty,
            RequestedInterfaceIndex = request?.InterfaceIndex ?? 0
        };
    }

    private static BmcEndpointProbeEvidence CopyEvidence(BmcEndpointProbeEvidence source)
    {
        return new BmcEndpointProbeEvidence
        {
            TargetAddress = source.TargetAddress,
            SourceAddress = source.SourceAddress,
            AdapterName = source.AdapterName,
            AdapterId = source.AdapterId,
            RequestedInterfaceIndex = source.RequestedInterfaceIndex,
            RouteResolved = source.RouteResolved,
            BestRouteInterfaceIndex = source.BestRouteInterfaceIndex,
            NeighborResolved = source.NeighborResolved,
            NeighborMac = source.NeighborMac,
            PeerMacMatchesExpected = source.PeerMacMatchesExpected
        };
    }

    private static bool TryValidateStrictRequest(BmcEndpointProbeRequest request, out string error)
    {
        error = string.Empty;
        if (request == null || request.TargetAddress == null || request.SourceAddress == null)
        {
            error = "TargetAddress and SourceAddress are required.";
            return false;
        }
        if (request.TargetAddress.AddressFamily != AddressFamily.InterNetwork || request.SourceAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Only IPv4 management probing is supported.";
            return false;
        }
        if (request.InterfaceIndex <= 0)
        {
            error = "A positive selected adapter IPv4 interface index is required.";
            return false;
        }
        var target = request.TargetAddress.GetAddressBytes();
        var source = request.SourceAddress.GetAddressBytes();
        if (target[0] != source[0] || target[1] != source[1] || target[2] != source[2])
        {
            error = "The target address is outside the source address /24 subnet.";
            return false;
        }
        var expectedMac = NativeEndpointNetworkEvidenceProvider.NormalizeMac(request.ExpectedPeerMac);
        if (!string.IsNullOrWhiteSpace(request.ExpectedPeerMac) && !NativeEndpointNetworkEvidenceProvider.IsUsableMac(expectedMac))
        {
            error = "ExpectedPeerMac is not a usable MAC address.";
            return false;
        }
        return true;
    }

    private static void ValidateCandidates(IReadOnlyList<BmcEndpointCandidate> candidates, string parameterName)
    {
        if (candidates == null || candidates.Count == 0)
            throw new ArgumentException("At least one endpoint candidate is required.", parameterName);
        foreach (var candidate in candidates)
        {
            if (candidate == null || candidate.Port < 1 || candidate.Port > 65535 ||
                (!string.Equals(candidate.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(candidate.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Endpoint candidates must be HTTP/HTTPS with a valid port.", parameterName);
            }
        }
    }

    private static bool TryCreateCompatibilityRequest(IPAddress target, out BmcEndpointProbeRequest request, out string error)
    {
        request = null;
        error = string.Empty;
        if (target == null || target.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "An IPv4 target is required.";
            return false;
        }

        try
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Connect(new IPEndPoint(target, 9));
                var source = ((IPEndPoint)socket.LocalEndPoint).Address;
                var provisional = new BmcEndpointProbeRequest { TargetAddress = target, SourceAddress = source };
                var network = NativeEndpointNetworkEvidenceProvider.Instance.Inspect(provisional);
                if (!network.RouteResolved || network.BestRouteInterfaceIndex <= 0)
                {
                    error = string.IsNullOrWhiteSpace(network.Error)
                        ? "The best IPv4 route could not be resolved."
                        : network.Error;
                    return false;
                }
                request = new BmcEndpointProbeRequest
                {
                    TargetAddress = target,
                    SourceAddress = source,
                    InterfaceIndex = network.BestRouteInterfaceIndex,
                    AdapterName = "OS route compatibility"
                };
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static string BuildUrl(string scheme, IPAddress ipAddress, int port)
    {
        var isDefaultPort = (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && port == 443)
            || (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && port == 80);
        return scheme.ToLowerInvariant() + "://" + ipAddress + (isDefaultPort ? string.Empty : ":" + port);
    }
}

}
