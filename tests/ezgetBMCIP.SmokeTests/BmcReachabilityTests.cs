using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EzGetBmcIp;

internal static class BmcReachabilityTests
{
    public static async Task RunAllAsync()
    {
        ResultSemanticsAreIndependentFromWebContent();
        await DelayedHttpsWinsOverEarlyHttpAsync();
        await HttpOnlyUsesTheFullWindowAsync();
        await PingOnlyUsesTheFullWindowWithoutInventingAUrlAsync();
        await UnreachableUsesTheBoundedWindowAsync();
        await ImmediateHttpsFinishesEarlyAsync();
        await CancellationStopsProbeAsync();
        await RetainedAddressRaceCarriesReachabilityAsync();
    }

    private static async Task DelayedHttpsWinsOverEarlyHttpAsync()
    {
        var requests = new List<(bool Ping, bool Https, bool Http)>();
        var logs = new List<string>();
        var result = await RunScriptedProbeAsync(
            TimeSpan.FromSeconds(2),
            (attempt, probePing, probeHttps, probeHttp) =>
            {
                requests.Add((probePing, probeHttps, probeHttp));
                return new BmcReachabilityProbe.AttemptResult
                {
                    HttpPortOpen = attempt == 1,
                    HttpsPortOpen = attempt >= 2
                };
            },
            logs.Add);

        Assert(result.HttpsPortOpen && result.HttpPortOpen &&
               result.PreferredUrl == "https://10.77.77.100",
            "A delayed HTTPS result did not replace the earlier HTTP fallback.");
        Assert(requests.Count == 2 && !requests[1].Http,
            "A successful HTTP probe was repeated instead of retaining cumulative evidence.");
        Assert(logs.Exists(line => line.Contains("completionReason=https-detected", StringComparison.Ordinal)),
            "The HTTPS completion reason was not logged.");
    }

    private static async Task HttpOnlyUsesTheFullWindowAsync()
    {
        var logs = new List<string>();
        var timeout = TimeSpan.FromMilliseconds(250);
        var result = await RunScriptedProbeAsync(
            timeout,
            (attempt, probePing, probeHttps, probeHttp) =>
            {
                return new BmcReachabilityProbe.AttemptResult { HttpPortOpen = probeHttp };
            },
            logs.Add);

        Assert(result.HttpPortOpen && !result.HttpsPortOpen &&
               result.PreferredUrl == "http://10.77.77.100",
            "HTTP-only evidence did not wait for later HTTPS before falling back.");
        Assert(result.Elapsed >= TimeSpan.FromMilliseconds(150),
            "HTTP-only probing returned before using the bounded preference window.");
        Assert(logs.Exists(line => line.Contains("completionReason=deadline-http-fallback", StringComparison.Ordinal)),
            "The HTTP fallback completion reason was not logged.");
    }

    private static async Task PingOnlyUsesTheFullWindowWithoutInventingAUrlAsync()
    {
        var logs = new List<string>();
        var result = await RunScriptedProbeAsync(
            TimeSpan.FromMilliseconds(250),
            (attempt, probePing, probeHttps, probeHttp) =>
            {
                return new BmcReachabilityProbe.AttemptResult { PingSucceeded = probePing };
            },
            logs.Add);

        Assert(result.IsReachable && result.PingSucceeded &&
               result.PreferredUrl == string.Empty,
            "Ping-only evidence did not preserve the full port-probing window and empty URL.");
        Assert(result.Elapsed >= TimeSpan.FromMilliseconds(150),
            "Ping-only probing returned before using the bounded port preference window.");
        Assert(logs.Exists(line => line.Contains("completionReason=deadline-ping-only", StringComparison.Ordinal)),
            "The Ping-only completion reason was not logged.");
    }

    private static async Task ImmediateHttpsFinishesEarlyAsync()
    {
        var attempts = 0;
        var timeout = TimeSpan.FromSeconds(2);
        var result = await RunScriptedProbeAsync(
            timeout,
            (attempt, probePing, probeHttps, probeHttp) =>
            {
                attempts++;
                return new BmcReachabilityProbe.AttemptResult { HttpsPortOpen = true };
            });

        Assert(attempts == 1 && result.HttpsPortOpen && result.Elapsed < timeout,
            "An immediate HTTPS result did not finish the probe promptly.");
    }

    private static async Task UnreachableUsesTheBoundedWindowAsync()
    {
        var logs = new List<string>();
        var result = await RunScriptedProbeAsync(
            TimeSpan.FromMilliseconds(250),
            (attempt, probePing, probeHttps, probeHttp) => new BmcReachabilityProbe.AttemptResult(),
            logs.Add);

        Assert(!result.IsReachable && result.Elapsed >= TimeSpan.FromMilliseconds(150) &&
               result.Elapsed < TimeSpan.FromSeconds(2),
            "An unreachable result did not respect the bounded probe window.");
        Assert(logs.Exists(line => line.Contains("completionReason=deadline-unreachable", StringComparison.Ordinal)),
            "The unreachable completion reason was not logged.");
    }

    private static void ResultSemanticsAreIndependentFromWebContent()
    {
        var pingOnly = new BmcReachabilityResult
        {
            TargetAddress = IPAddress.Parse("10.77.77.100"),
            PingSucceeded = true
        };
        Assert(pingOnly.IsReachable && pingOnly.PreferredUrl == string.Empty,
            "Ping-only reachability must succeed without inventing a web URL.");

        var https = new BmcReachabilityResult
        {
            TargetAddress = IPAddress.Parse("10.77.77.100"),
            HttpsPortOpen = true,
            HttpPortOpen = true
        };
        Assert(https.IsReachable && https.PreferredScheme == "https" &&
               https.PreferredUrl == "https://10.77.77.100",
            "HTTPS must be preferred when both web ports are reachable.");

        var http = new BmcReachabilityResult
        {
            TargetAddress = IPAddress.Parse("10.77.77.100"),
            HttpPortOpen = true
        };
        Assert(http.PreferredScheme == "http" && http.PreferredUrl == "http://10.77.77.100",
            "HTTP must be selected when HTTPS is unavailable.");

        var unreachable = new BmcReachabilityResult
        {
            TargetAddress = IPAddress.Parse("10.77.77.100")
        };
        Assert(!unreachable.IsReachable && unreachable.PreferredUrl == string.Empty,
            "An empty reachability result must remain unsuccessful.");
    }

    private static async Task CancellationStopsProbeAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            await BmcReachabilityProbe.ProbeAsync(
                IPAddress.Loopback,
                IPAddress.Loopback,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled, "Cancelling a reachability probe must stop it promptly.");
    }

    private static async Task RetainedAddressRaceCarriesReachabilityAsync()
    {
        var expected = new BmcReachabilityResult
        {
            TargetAddress = IPAddress.Parse("10.77.77.100"),
            PingSucceeded = true
        };
        var dhcpCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await BmcDiscovery.WaitForAddressAsync(
            expected.TargetAddress,
            async token =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return IPAddress.None;
                }
                catch (OperationCanceledException)
                {
                    dhcpCancelled.TrySetResult(true);
                    throw;
                }
            },
            (address, token) => Task.FromResult(expected),
            CancellationToken.None);

        Assert(result.Source == BmcDiscoverySource.ExistingConfiguredAddress &&
               ReferenceEquals(result.Reachability, expected),
            "The retained-address race did not carry lightweight reachability evidence.");
        await dhcpCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static Task<BmcReachabilityResult> RunScriptedProbeAsync(
        TimeSpan timeout,
        Func<int, bool, bool, bool, BmcReachabilityProbe.AttemptResult> script,
        Action<string>? logger = null)
    {
        return BmcReachabilityProbe.ProbeCoreAsync(
            IPAddress.Parse("10.77.77.100"),
            IPAddress.Parse("10.77.77.1"),
            CancellationToken.None,
            logger,
            timeout,
            TimeSpan.FromMilliseconds(1),
            (attempt, probePing, probeHttps, probeHttp, deadline, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(script(attempt, probePing, probeHttps, probeHttp));
            });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
