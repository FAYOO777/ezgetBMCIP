using System.Net;
using System.Net.Sockets;

namespace EzGetBmcIp;

public sealed class BmcEndpointProbeResult
{
    public string Url { get; init; } = string.Empty;
    public string Scheme { get; init; } = string.Empty;
    public int Port { get; init; }
}

public sealed class BmcEndpointCandidate
{
    public string Scheme { get; init; } = string.Empty;
    public int Port { get; init; }
}

public static class BmcEndpointProbe
{
    private static readonly IReadOnlyList<BmcEndpointCandidate> DefaultCandidates = new[]
    {
        new BmcEndpointCandidate { Scheme = "https", Port = 443 },
        new BmcEndpointCandidate { Scheme = "http", Port = 80 }
    };

    public static async Task<BmcEndpointProbeResult?> WaitForEndpointAsync(
        IPAddress ipAddress,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? logger = null,
        IReadOnlyList<BmcEndpointCandidate>? candidates = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;
        var endpoints = candidates ?? DefaultCandidates;
        if (endpoints.Count == 0)
            throw new ArgumentException("At least one endpoint candidate is required.", nameof(candidates));
        logger?.Invoke("BMC endpoint probe started: ip=" + ipAddress + ", timeout=" + timeout.TotalSeconds + "s");

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            foreach (var candidate in endpoints)
            {
                if (candidate.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(candidate.Scheme))
                    throw new ArgumentException("Endpoint candidates must contain a scheme and a valid port.", nameof(candidates));

                if (await CanConnectAsync(ipAddress, candidate.Port, cancellationToken))
                {
                    logger?.Invoke("BMC endpoint reachable on " + candidate.Scheme.ToUpperInvariant() +
                        " after attempt " + attempt);
                    return new BmcEndpointProbeResult
                    {
                        Url = BuildUrl(candidate.Scheme, ipAddress, candidate.Port),
                        Scheme = candidate.Scheme,
                        Port = candidate.Port
                    };
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(800)
                ? remaining
                : TimeSpan.FromMilliseconds(800), cancellationToken);
        }

        logger?.Invoke("BMC endpoint probe timed out after " + attempt + " attempt(s)");
        return null;
    }

    private static string BuildUrl(string scheme, IPAddress ipAddress, int port)
    {
        var isDefaultPort = (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && port == 443)
            || (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && port == 80);
        return scheme + "://" + ipAddress + (isDefaultPort ? string.Empty : ":" + port);
    }

    private static async Task<bool> CanConnectAsync(
        IPAddress ipAddress,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(TimeSpan.FromMilliseconds(900));
        try
        {
            await client.ConnectAsync(ipAddress, port, attemptCts.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
