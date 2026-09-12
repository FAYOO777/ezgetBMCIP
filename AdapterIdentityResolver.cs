#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EzGetBmcIp
{
    internal enum AdapterIdentityMatchKind
    {
        None,
        NormalizedGuid,
        Mac,
        UniqueNameAndDescription
    }

    // A small, testable snapshot of one currently enumerated interface. The native
    // NetworkInterface is optional so tests can exercise the identity rules without
    // touching the host's network stack.
    internal sealed class AdapterIdentityCandidate
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string MacAddress { get; set; } = string.Empty;
        public int InterfaceIndex { get; set; }
        public bool HasSourceAddress { get; set; }
        public NetworkInterface NativeInterface { get; set; }
    }

    internal sealed class AdapterIdentityResolution
    {
        public NetworkInterface NetworkInterface { get; set; }
        public AdapterIdentityCandidate Candidate { get; set; }
        public AdapterIdentityMatchKind MatchKind { get; set; }
        public int Attempts { get; set; }
        public string Diagnostic { get; set; } = string.Empty;
        public string FailureReason { get; set; } = string.Empty;

        public bool Success { get { return Candidate != null && NetworkInterface != null; } }
    }

    internal sealed class EndpointAdapterUnavailableException : InvalidOperationException
    {
        public EndpointAdapterUnavailableException(AdapterIdentityResolution resolution)
            : base(BuildMessage(resolution))
        {
            Resolution = resolution;
        }

        public AdapterIdentityResolution Resolution { get; }

        private static string BuildMessage(AdapterIdentityResolution resolution)
        {
            var reason = string.IsNullOrWhiteSpace(resolution == null ? null : resolution.FailureReason)
                ? "Windows 未能重新枚举出带有本次临时 IPv4 地址的所选网卡。"
                : resolution.FailureReason;
            return reason + " 已保留候选管理地址；可以重新验证、导出支持包，或恢复网卡并退出。";
        }
    }

    internal static class AdapterIdentityResolver
    {
        private const int MaxAttempts = 8;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

        internal static AdapterIdentityCandidate Select(
            WiredAdapter expected,
            IPAddress sourceAddress,
            IEnumerable<AdapterIdentityCandidate> candidates,
            out AdapterIdentityMatchKind matchKind,
            out string diagnostic)
        {
            matchKind = AdapterIdentityMatchKind.None;
            var all = (candidates ?? Enumerable.Empty<AdapterIdentityCandidate>()).ToList();
            var usable = all.Where(item => item != null && item.HasSourceAddress && item.InterfaceIndex > 0).ToList();

            if (expected == null)
            {
                diagnostic = BuildDiagnostic(expected, sourceAddress, all, usable, "expected-adapter-missing");
                return null;
            }

            var expectedId = NormalizeId(expected.Id);
            if (!string.IsNullOrWhiteSpace(expectedId))
            {
                var idMatches = usable.Where(item => string.Equals(NormalizeId(item.Id), expectedId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (idMatches.Count == 1)
                {
                    matchKind = AdapterIdentityMatchKind.NormalizedGuid;
                    diagnostic = BuildDiagnostic(expected, sourceAddress, all, usable, "normalized-guid");
                    return idMatches[0];
                }

                if (idMatches.Count > 1)
                {
                    diagnostic = BuildDiagnostic(expected, sourceAddress, all, usable, "ambiguous-normalized-guid");
                    return null;
                }
            }

            var expectedMac = NormalizeMac(expected.MacAddress);
            if (!string.IsNullOrWhiteSpace(expectedMac))
            {
                var macMatches = usable.Where(item => string.Equals(NormalizeMac(item.MacAddress), expectedMac, StringComparison.OrdinalIgnoreCase)).ToList();
                if (macMatches.Count == 1)
                {
                    matchKind = AdapterIdentityMatchKind.Mac;
                    diagnostic = BuildDiagnostic(expected, sourceAddress, all, usable, "mac");
                    return macMatches[0];
                }

                if (macMatches.Count > 1)
                {
                    diagnostic = BuildDiagnostic(expected, sourceAddress, all, usable, "ambiguous-mac");
                    return null;
                }
            }

            var expectedName = (expected.Name ?? string.Empty).Trim();
            var expectedDescription = (expected.Description ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(expectedName) || !string.IsNullOrWhiteSpace(expectedDescription))
            {
                var nameDescriptionMatches = usable.Where(item =>
                    string.Equals((item.Name ?? string.Empty).Trim(), expectedName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals((item.Description ?? string.Empty).Trim(), expectedDescription, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (nameDescriptionMatches.Count == 1)
                {
                    matchKind = AdapterIdentityMatchKind.UniqueNameAndDescription;
                    diagnostic = BuildDiagnostic(expected, sourceAddress, all, usable, "unique-name-description");
                    return nameDescriptionMatches[0];
                }

                if (nameDescriptionMatches.Count > 1)
                {
                    diagnostic = BuildDiagnostic(expected, sourceAddress, all, usable, "ambiguous-name-description");
                    return null;
                }
            }

            diagnostic = BuildDiagnostic(expected, sourceAddress, all, usable, "no-safe-match");
            return null;
        }

        internal static async Task<AdapterIdentityResolution> ResolveAsync(
            WiredAdapter expected,
            IPAddress sourceAddress,
            CancellationToken cancellationToken,
            Action<string> logger = null)
        {
            AdapterIdentityResolution last = null;
            var lastSlowEnumerationLogUtc = DateTime.MinValue;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // NetworkInterface enumeration and GetIPProperties can block
                // while Windows is re-enumerating an adapter. Keep the entire
                // snapshot off the WPF dispatcher; matching remains pure and
                // deterministic once the snapshot is available.
                var enumerationStopwatch = Stopwatch.StartNew();
                var candidates = await Task.Run(
                    () => EnumerateCandidates(sourceAddress),
                    cancellationToken).ConfigureAwait(false);
                if (enumerationStopwatch.Elapsed >= TimeSpan.FromMilliseconds(500)
                    && DateTime.UtcNow - lastSlowEnumerationLogUtc >= TimeSpan.FromSeconds(5))
                {
                    lastSlowEnumerationLogUtc = DateTime.UtcNow;
                    logger?.Invoke("Endpoint adapter enumeration slow: " +
                        enumerationStopwatch.Elapsed.TotalMilliseconds.ToString("0") + "ms");
                }
                AdapterIdentityMatchKind matchKind;
                string diagnostic;
                var selected = Select(expected, sourceAddress, candidates, out matchKind, out diagnostic);
                if (selected != null)
                {
                    diagnostic += "; actualId=" + NormalizeId(selected.Id)
                        + "; actualMac=" + NormalizeMac(selected.MacAddress)
                        + "; actualName=" + selected.Name
                        + "; actualDescription=" + selected.Description
                        + "; interfaceIndex=" + selected.InterfaceIndex;
                }
                else
                {
                    diagnostic += BuildCandidateSummary(candidates);
                }
                var resolution = new AdapterIdentityResolution
                {
                    Candidate = selected,
                    NetworkInterface = selected == null ? null : selected.NativeInterface,
                    MatchKind = matchKind,
                    Attempts = attempt,
                    Diagnostic = diagnostic
                };

                if (selected != null && resolution.NetworkInterface != null)
                {
                    logger?.Invoke("Endpoint adapter resolved: " + diagnostic + "; attempts=" + attempt);
                    return resolution;
                }

                resolution.FailureReason = BuildFailureReason(candidates, expected, sourceAddress);
                last = resolution;
                logger?.Invoke("Endpoint adapter resolution attempt " + attempt + "/" + MaxAttempts + ": " + diagnostic);
                if (attempt < MaxAttempts)
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }

            if (last == null)
            {
                last = new AdapterIdentityResolution
                {
                    Attempts = MaxAttempts,
                    FailureReason = "无法重新找到所选网卡。"
                };
            }

            return last;
        }

        private static List<AdapterIdentityCandidate> EnumerateCandidates(IPAddress sourceAddress)
        {
            var result = new List<AdapterIdentityCandidate>();
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return result;
            }

            foreach (var networkInterface in interfaces)
            {
                try
                {
                    var properties = networkInterface.GetIPProperties();
                    var ipv4 = properties.GetIPv4Properties();
                    if (ipv4 == null || ipv4.Index <= 0)
                        continue;

                    var hasSource = sourceAddress != null
                        && properties.UnicastAddresses.Any(item => item.Address.Equals(sourceAddress));
                    var physicalAddress = networkInterface.GetPhysicalAddress();
                    result.Add(new AdapterIdentityCandidate
                    {
                        Id = networkInterface.Id ?? string.Empty,
                        Name = networkInterface.Name ?? string.Empty,
                        Description = networkInterface.Description ?? string.Empty,
                        MacAddress = physicalAddress == null ? string.Empty : physicalAddress.ToString(),
                        InterfaceIndex = ipv4.Index,
                        HasSourceAddress = hasSource,
                        NativeInterface = networkInterface
                    });
                }
                catch
                {
                    // One interface can disappear while Windows is re-enumerating it.
                    // Ignore that snapshot and retry the complete enumeration shortly.
                }
            }

            return result;
        }

        internal static string NormalizeId(string value)
        {
            return (value ?? string.Empty).Trim().Trim('{', '}');
        }

        internal static string NormalizeMac(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var builder = new StringBuilder();
            foreach (var character in value)
            {
                if ((character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F'))
                    builder.Append(char.ToUpperInvariant(character));
            }

            return builder.ToString();
        }

        private static string BuildFailureReason(
            IEnumerable<AdapterIdentityCandidate> candidates,
            WiredAdapter expected,
            IPAddress sourceAddress)
        {
            var list = (candidates ?? Enumerable.Empty<AdapterIdentityCandidate>()).ToList();
            var withSource = list.Count(item => item != null && item.HasSourceAddress);
            if (withSource == 0)
                return "无法重新找到带有本次临时 IPv4 地址 " + sourceAddress + " 的所选网卡。";

            return "无法安全确认当前网卡就是所选网卡（GUID、MAC 或唯一名称/描述均未形成唯一匹配）。";
        }

        private static string BuildDiagnostic(
            WiredAdapter expected,
            IPAddress sourceAddress,
            IList<AdapterIdentityCandidate> all,
            IList<AdapterIdentityCandidate> usable,
            string result)
        {
            var builder = new StringBuilder();
            builder.Append("result=").Append(result);
            builder.Append("; source=").Append(sourceAddress == null ? "none" : sourceAddress.ToString());
            builder.Append("; expectedId=").Append(NormalizeId(expected == null ? string.Empty : expected.Id));
            builder.Append("; expectedMac=").Append(NormalizeMac(expected == null ? string.Empty : expected.MacAddress));
            builder.Append("; expectedName=").Append(expected == null ? string.Empty : expected.Name);
            builder.Append("; expectedDescription=").Append(expected == null ? string.Empty : expected.Description);
            builder.Append("; candidates=").Append(all == null ? 0 : all.Count);
            builder.Append("; candidatesWithSource=").Append(usable == null ? 0 : usable.Count);
            return builder.ToString();
        }

        private static string BuildCandidateSummary(IEnumerable<AdapterIdentityCandidate> candidates)
        {
            var list = (candidates ?? Enumerable.Empty<AdapterIdentityCandidate>())
                .Where(item => item != null && item.HasSourceAddress)
                .Take(8)
                .Select(item => "[id=" + NormalizeId(item.Id)
                    + ",mac=" + NormalizeMac(item.MacAddress)
                    + ",name=" + item.Name
                    + ",index=" + item.InterfaceIndex + "]")
                .ToList();
            return "; candidateDetails=" + (list.Count == 0 ? "none" : string.Join(",", list));
        }
    }
}
