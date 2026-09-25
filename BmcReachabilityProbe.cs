#nullable disable

using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EzGetBmcIp
{
    /// <summary>
    /// Lightweight address reachability evidence. It deliberately does not
    /// inspect TLS, HTTP headers, redirects, status codes, or page content.
    /// </summary>
    public sealed class BmcReachabilityResult
    {
        public IPAddress TargetAddress { get; set; }
        public bool PingSucceeded { get; set; }
        public bool HttpsPortOpen { get; set; }
        public bool HttpPortOpen { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string FailureDetail { get; set; }

        public bool IsReachable
        {
            get { return PingSucceeded || HttpsPortOpen || HttpPortOpen; }
        }

        public string PreferredScheme
        {
            get
            {
                if (HttpsPortOpen) return "https";
                if (HttpPortOpen) return "http";
                return "https";
            }
        }

        public string PreferredUrl
        {
            get
            {
                if (TargetAddress == null || (!HttpsPortOpen && !HttpPortOpen))
                    return string.Empty;
                return PreferredScheme + "://" + TargetAddress;
            }
        }
    }

    public static class BmcReachabilityProbe
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

        internal sealed class AttemptResult
        {
            public bool PingSucceeded { get; set; }
            public bool HttpsPortOpen { get; set; }
            public bool HttpPortOpen { get; set; }
        }

        internal delegate Task<AttemptResult> AttemptProbe(
            int attempt,
            bool probePing,
            bool probeHttps,
            bool probeHttp,
            DateTime deadline,
            CancellationToken cancellationToken);

        public static async Task<BmcReachabilityResult> ProbeAsync(
            IPAddress targetAddress,
            IPAddress sourceAddress,
            CancellationToken cancellationToken,
            Action<string> logger = null,
            TimeSpan? timeout = null)
        {
            if (targetAddress == null)
                throw new ArgumentNullException(nameof(targetAddress));
            if (sourceAddress == null)
                throw new ArgumentNullException(nameof(sourceAddress));
            if (targetAddress.AddressFamily != AddressFamily.InterNetwork ||
                sourceAddress.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Only IPv4 reachability probing is supported.");

            var totalTimeout = timeout ?? DefaultTimeout;
            if (totalTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            return await ProbeCoreAsync(
                targetAddress,
                sourceAddress,
                cancellationToken,
                logger,
                totalTimeout,
                RetryDelay,
                (attempt, probePing, probeHttps, probeHttp, attemptDeadline, token) =>
                    ProbeAttemptAsync(
                        targetAddress,
                        sourceAddress,
                        probePing,
                        probeHttps,
                        probeHttp,
                        attemptDeadline,
                        token))
                .ConfigureAwait(false);
        }

        internal static async Task<BmcReachabilityResult> ProbeCoreAsync(
            IPAddress targetAddress,
            IPAddress sourceAddress,
            CancellationToken cancellationToken,
            Action<string> logger,
            TimeSpan totalTimeout,
            TimeSpan retryDelay,
            AttemptProbe attemptProbe)
        {
            if (targetAddress == null)
                throw new ArgumentNullException(nameof(targetAddress));
            if (sourceAddress == null)
                throw new ArgumentNullException(nameof(sourceAddress));
            if (totalTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(totalTimeout));
            if (retryDelay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(retryDelay));
            if (attemptProbe == null)
                throw new ArgumentNullException(nameof(attemptProbe));

            var stopwatch = Stopwatch.StartNew();
            var deadline = DateTime.UtcNow + totalTimeout;
            var result = new BmcReachabilityResult
            {
                TargetAddress = targetAddress,
                FailureDetail = string.Empty
            };
            var attempt = 0;

            logger?.Invoke("BMC lightweight reachability probe started: target=" +
                targetAddress + ", source=" + sourceAddress + ", timeout=" +
                totalTimeout.TotalSeconds.ToString("0.##") + "s");

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;
                var remaining = deadline - DateTime.UtcNow;
                var attemptDeadline = DateTime.UtcNow +
                    (remaining < AttemptTimeout ? remaining : AttemptTimeout);

                var probePing = !result.PingSucceeded;
                var probeHttps = !result.HttpsPortOpen;
                var probeHttp = !result.HttpPortOpen;
                var attemptResult = await attemptProbe(
                        attempt,
                        probePing,
                        probeHttps,
                        probeHttp,
                        attemptDeadline,
                        cancellationToken)
                    .ConfigureAwait(false);
                result.PingSucceeded |= attemptResult.PingSucceeded;
                result.HttpsPortOpen |= attemptResult.HttpsPortOpen;
                result.HttpPortOpen |= attemptResult.HttpPortOpen;

                logger?.Invoke("BMC lightweight reachability attempt=" + attempt +
                    " ping=" + attemptResult.PingSucceeded +
                    " tcp443=" + attemptResult.HttpsPortOpen +
                    " tcp80=" + attemptResult.HttpPortOpen +
                    " cumulativePing=" + result.PingSucceeded +
                    " cumulativeTcp443=" + result.HttpsPortOpen +
                    " cumulativeTcp80=" + result.HttpPortOpen);

                if (result.HttpsPortOpen)
                    break;

                remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;
                await Task.Delay(remaining < retryDelay ? remaining : retryDelay, cancellationToken)
                    .ConfigureAwait(false);
            }

            result.Elapsed = stopwatch.Elapsed;
            if (!result.IsReachable)
                result.FailureDetail = "No ICMP reply or successful TCP 80/443 connection was observed within the probe window.";

            var completionReason = result.HttpsPortOpen
                ? "https-detected"
                : result.HttpPortOpen
                    ? "deadline-http-fallback"
                    : result.PingSucceeded
                        ? "deadline-ping-only"
                        : "deadline-unreachable";

            logger?.Invoke("BMC lightweight reachability probe finished: reachable=" +
                result.IsReachable + " ping=" + result.PingSucceeded +
                " tcp443=" + result.HttpsPortOpen + " tcp80=" + result.HttpPortOpen +
                " completionReason=" + completionReason +
                " elapsedMs=" + result.Elapsed.TotalMilliseconds.ToString("0"));
            return result;
        }

        private static async Task<AttemptResult> ProbeAttemptAsync(
            IPAddress targetAddress,
            IPAddress sourceAddress,
            bool probePing,
            bool probeHttps,
            bool probeHttp,
            DateTime deadline,
            CancellationToken cancellationToken)
        {
            var pingTask = probePing
                ? ProbePingAsync(targetAddress, deadline, cancellationToken)
                : Task.FromResult(false);
            var httpsTask = probeHttps
                ? ProbeTcpAsync(targetAddress, sourceAddress, 443, deadline, cancellationToken)
                : Task.FromResult(false);
            var httpTask = probeHttp
                ? ProbeTcpAsync(targetAddress, sourceAddress, 80, deadline, cancellationToken)
                : Task.FromResult(false);

            await Task.WhenAll(pingTask, httpsTask, httpTask).ConfigureAwait(false);
            return new AttemptResult
            {
                PingSucceeded = pingTask.Result,
                HttpsPortOpen = httpsTask.Result,
                HttpPortOpen = httpTask.Result
            };
        }

        private static async Task<bool> ProbePingAsync(
            IPAddress targetAddress,
            DateTime deadline,
            CancellationToken cancellationToken)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return false;

            try
            {
                using (var ping = new Ping())
                {
                    var timeoutMs = (int)Math.Max(1, Math.Min(1000, remaining.TotalMilliseconds));
                    var pingTask = ping.SendPingAsync(targetAddress, timeoutMs);
                    if (!await CompleteBeforeDeadlineAsync(pingTask, deadline, cancellationToken)
                        .ConfigureAwait(false))
                        return false;
                    return pingTask.Result != null && pingTask.Result.Status == IPStatus.Success;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PingException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static async Task<bool> ProbeTcpAsync(
            IPAddress targetAddress,
            IPAddress sourceAddress,
            int port,
            DateTime deadline,
            CancellationToken cancellationToken)
        {
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                try
                {
                    client.Client.Bind(new IPEndPoint(sourceAddress, 0));
                    var connectTask = client.ConnectAsync(targetAddress, port);
                    if (!await CompleteBeforeDeadlineAsync(connectTask, deadline, cancellationToken)
                        .ConfigureAwait(false))
                        return false;
                    return client.Connected;
                }
                catch (SocketException)
                {
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private static async Task<bool> CompleteBeforeDeadlineAsync(
            Task task,
            DateTime deadline,
            CancellationToken cancellationToken)
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

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(
                completed => { var ignored = completed.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
