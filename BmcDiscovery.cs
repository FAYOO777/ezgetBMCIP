using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace EzGetBmcIp
{
    public enum BmcDiscoverySource
    {
        Dhcp,
        ExistingConfiguredAddress
    }

    public sealed class BmcDiscoveryResult
    {
        public IPAddress IpAddress { get; set; } = IPAddress.None;
        public BmcDiscoverySource Source { get; set; }

        // Present only when the retained-configured-address race already
        // completed a strict management-service probe. Callers can reuse this
        // proof rather than issuing a second probe to the same endpoint.
        public BmcEndpointProbeResult VerifiedEndpoint { get; set; } = null!;

        // Present when the retained-configured-address race completed the
        // lightweight Ping/TCP reachability probe. This is intentionally
        // independent from HTTP response verification.
        public BmcReachabilityResult Reachability { get; set; } = null!;
    }

    public static class BmcDiscovery
    {
        // A BMC can retain the lease issued by a previous ezgetBMCIP run and
        // therefore not broadcast DHCP again when the next run starts. Race a
        // configured-address validation with DHCP so that path is found without
        // waiting for lease renewal. Callers decide the validation strength and
        // duration; the normal discovery flow uses the bounded lightweight probe.
        public static async Task<BmcDiscoveryResult> WaitForAddressAsync(
            IPAddress configuredAddress,
            Func<CancellationToken, Task<IPAddress>> waitForDhcpAddressAsync,
            Func<IPAddress, CancellationToken, Task<bool>> probeConfiguredAddressAsync,
            CancellationToken cancellationToken)
        {
            if (configuredAddress == null)
                throw new ArgumentNullException(nameof(configuredAddress));
            if (waitForDhcpAddressAsync == null)
                throw new ArgumentNullException(nameof(waitForDhcpAddressAsync));
            if (probeConfiguredAddressAsync == null)
                throw new ArgumentNullException(nameof(probeConfiguredAddressAsync));

            using (var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    var dhcpTask = waitForDhcpAddressAsync(raceCts.Token);
                    var configuredAddressTask = probeConfiguredAddressAsync(configuredAddress, raceCts.Token);
                    var completed = await Task.WhenAny(dhcpTask, configuredAddressTask);

                    if (completed == configuredAddressTask && await configuredAddressTask)
                    {
                        raceCts.Cancel();
                        await ObserveCanceledTaskAsync(dhcpTask);
                        return new BmcDiscoveryResult
                        {
                            IpAddress = configuredAddress,
                            Source = BmcDiscoverySource.ExistingConfiguredAddress
                        };
                    }

                    if (completed == dhcpTask)
                    {
                        var dhcpAddress = await dhcpTask;
                        raceCts.Cancel();
                        await ObserveCanceledTaskAsync(configuredAddressTask);
                        return new BmcDiscoveryResult
                        {
                            IpAddress = dhcpAddress,
                            Source = BmcDiscoverySource.Dhcp
                        };
                    }

                    return new BmcDiscoveryResult
                    {
                        IpAddress = await dhcpTask,
                        Source = BmcDiscoverySource.Dhcp
                    };
                }
                finally
                {
                    if (!raceCts.IsCancellationRequested)
                        raceCts.Cancel();
                }
            }
        }

        /// <summary>
        /// Compatibility race for callers that still require the retained
        /// strict management-service evidence. The normal flow uses the
        /// lightweight reachability overload below.
        /// </summary>
        public static async Task<BmcDiscoveryResult> WaitForAddressAsync(
            IPAddress configuredAddress,
            Func<CancellationToken, Task<IPAddress>> waitForDhcpAddressAsync,
            Func<IPAddress, CancellationToken, Task<BmcEndpointProbeResult>> probeConfiguredAddressAsync,
            CancellationToken cancellationToken)
        {
            if (configuredAddress == null)
                throw new ArgumentNullException(nameof(configuredAddress));
            if (waitForDhcpAddressAsync == null)
                throw new ArgumentNullException(nameof(waitForDhcpAddressAsync));
            if (probeConfiguredAddressAsync == null)
                throw new ArgumentNullException(nameof(probeConfiguredAddressAsync));

            using (var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    var dhcpTask = waitForDhcpAddressAsync(raceCts.Token);
                    var configuredAddressTask = probeConfiguredAddressAsync(configuredAddress, raceCts.Token);
                    var completed = await Task.WhenAny(dhcpTask, configuredAddressTask);

                    if (completed == configuredAddressTask)
                    {
                        var endpoint = await configuredAddressTask;
                        if (endpoint != null)
                        {
                            raceCts.Cancel();
                            await ObserveCanceledTaskAsync(dhcpTask);
                            return new BmcDiscoveryResult
                            {
                                IpAddress = configuredAddress,
                                Source = BmcDiscoverySource.ExistingConfiguredAddress,
                                VerifiedEndpoint = endpoint
                            };
                        }
                    }

                    // A failed configured-address probe is not a discovery
                    // failure. Keep waiting for the real DHCP path.
                    var dhcpAddress = await dhcpTask;
                    raceCts.Cancel();
                    await ObserveCanceledTaskAsync(configuredAddressTask);
                    return new BmcDiscoveryResult
                    {
                        IpAddress = dhcpAddress,
                        Source = BmcDiscoverySource.Dhcp
                    };
                }
                finally
                {
                    if (!raceCts.IsCancellationRequested)
                        raceCts.Cancel();
                }
            }
        }

        /// <summary>
        /// Retained-address race using bounded lightweight reachability. A
        /// failed five-second check is not a discovery failure; DHCP continues
        /// to have the full normal wait window.
        /// </summary>
        public static async Task<BmcDiscoveryResult> WaitForAddressAsync(
            IPAddress configuredAddress,
            Func<CancellationToken, Task<IPAddress>> waitForDhcpAddressAsync,
            Func<IPAddress, CancellationToken, Task<BmcReachabilityResult>> probeConfiguredAddressAsync,
            CancellationToken cancellationToken)
        {
            if (configuredAddress == null)
                throw new ArgumentNullException(nameof(configuredAddress));
            if (waitForDhcpAddressAsync == null)
                throw new ArgumentNullException(nameof(waitForDhcpAddressAsync));
            if (probeConfiguredAddressAsync == null)
                throw new ArgumentNullException(nameof(probeConfiguredAddressAsync));

            using (var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    var dhcpTask = waitForDhcpAddressAsync(raceCts.Token);
                    var configuredAddressTask = probeConfiguredAddressAsync(configuredAddress, raceCts.Token);
                    var completed = await Task.WhenAny(dhcpTask, configuredAddressTask);

                    if (completed == configuredAddressTask)
                    {
                        var reachability = await configuredAddressTask;
                        if (reachability != null && reachability.IsReachable)
                        {
                            raceCts.Cancel();
                            await ObserveCanceledTaskAsync(dhcpTask);
                            return new BmcDiscoveryResult
                            {
                                IpAddress = configuredAddress,
                                Source = BmcDiscoverySource.ExistingConfiguredAddress,
                                Reachability = reachability
                            };
                        }
                    }

                    var dhcpAddress = await dhcpTask;
                    raceCts.Cancel();
                    await ObserveCanceledTaskAsync(configuredAddressTask);
                    return new BmcDiscoveryResult
                    {
                        IpAddress = dhcpAddress,
                        Source = BmcDiscoverySource.Dhcp
                    };
                }
                finally
                {
                    if (!raceCts.IsCancellationRequested)
                        raceCts.Cancel();
                }
            }
        }

        private static async Task ObserveCanceledTaskAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected when the other discovery path has already won.
            }
            catch
            {
                // The successful discovery path is sufficient; only observe
                // the losing task so it cannot become an unobserved fault.
            }
        }
    }
}
