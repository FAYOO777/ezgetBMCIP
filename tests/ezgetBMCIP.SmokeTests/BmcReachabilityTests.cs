using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EzGetBmcIp;

internal static class BmcReachabilityTests
{
    public static async Task RunAllAsync()
    {
        ResultSemanticsAreIndependentFromWebContent();
        await LoopbackProbeUsesTheBoundedWindowAsync();
        await CancellationStopsProbeAsync();
        await RetainedAddressRaceCarriesReachabilityAsync();
    }

    private static async Task LoopbackProbeUsesTheBoundedWindowAsync()
    {
        var result = await BmcReachabilityProbe.ProbeAsync(
            IPAddress.Loopback,
            IPAddress.Loopback,
            CancellationToken.None);

        Assert(result.IsReachable && result.PingSucceeded,
            "Loopback reachability probe did not observe the expected ICMP response.");
        Assert(result.Elapsed <= TimeSpan.FromSeconds(5.5),
            "Reachability probe exceeded its five-second overall window.");
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
