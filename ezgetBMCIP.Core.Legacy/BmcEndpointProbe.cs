using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EzGetBmcIp
{
    public sealed class BmcEndpointProbeResult
    {
        public string Url { get; set; } = string.Empty;
        public string Scheme { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    public sealed class BmcEndpointCandidate
    {
        public string Scheme { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    public static class BmcEndpointProbe
    {
        private static readonly IReadOnlyList<BmcEndpointCandidate> DefaultCandidates = new[]
        {
            new BmcEndpointCandidate { Scheme = "https", Port = 443 },
            new BmcEndpointCandidate { Scheme = "http", Port = 80 }
        };

        public static async Task<BmcEndpointProbeResult> WaitForEndpointAsync(
            IPAddress ipAddress,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Action<string> logger = null,
            IReadOnlyList<BmcEndpointCandidate> candidates = null)
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
                    if (candidate.Port < 1 || candidate.Port > 65535 || string.IsNullOrWhiteSpace(candidate.Scheme))
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

        private static async Task<bool> CanConnectAsync(IPAddress ipAddress, int port, CancellationToken cancellationToken)
        {
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                try
                {
                    var connectTask = client.ConnectAsync(ipAddress, port);
                    var timeoutTask = Task.Delay(900, cancellationToken);
                    var completed = await Task.WhenAny(connectTask, timeoutTask);
                    if (completed != connectTask)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        client.Close();
                        var ignored = connectTask.ContinueWith(
                            task => { var observed = task.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted);
                        return false;
                    }

                    await connectTask;
                    return client.Connected;
                }
                catch (SocketException)
                {
                    return false;
                }
            }
        }
    }
}
