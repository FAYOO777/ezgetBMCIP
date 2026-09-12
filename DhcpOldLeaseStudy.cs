#nullable disable
using System;
using System.Net;

namespace EzGetBmcIp
{
    /// <summary>
    /// Test-build-only controls for the old-lease causality study. The formal
    /// product build has no environment-variable input and keeps its 1-hour
    /// lease values.
    /// </summary>
    internal static class DhcpOldLeaseStudy
    {
#if DHCP_OLD_LEASE_STUDY
        private const string ActionVariable = "EZGETBMCIP_TEST_OLD_LEASE_ACTION";
        private static DhcpOldLeaseAction? _action;

        internal const bool IsEnabled = true;
        internal const int LeaseSeconds = 120;
        internal const int RenewalSeconds = 60;
        internal const int RebindingSeconds = 105;

        internal static void EnsureConfigured()
        {
            _ = Action;
        }

        internal static DhcpOldLeaseAction Action
        {
            get
            {
                if (_action.HasValue)
                {
                    return _action.Value;
                }

                var value = Environment.GetEnvironmentVariable(ActionVariable);
                if (string.Equals(value, "Nak", StringComparison.OrdinalIgnoreCase))
                {
                    _action = DhcpOldLeaseAction.Nak;
                }
                else if (string.Equals(value, "Ignore", StringComparison.OrdinalIgnoreCase))
                {
                    _action = DhcpOldLeaseAction.Ignore;
                }
                else
                {
                    throw new InvalidOperationException(
                        "This test build requires " + ActionVariable + "=Nak or Ignore.");
                }

                return _action.Value;
            }
        }

        internal static bool ShouldSuppressOldCiaddrNak(DhcpRequestDecision decision)
        {
            return Action == DhcpOldLeaseAction.Ignore
                && decision != null
                && decision.Disposition == DhcpRequestDisposition.Nak
                && decision.Ciaddr != null
                && !decision.Ciaddr.Equals(IPAddress.Any);
        }

        internal static string DiagnosticText
        {
            get
            {
                return "enabled; action=" + Action +
                    "; leaseSeconds=" + LeaseSeconds +
                    "; renewalSeconds=" + RenewalSeconds +
                    "; rebindingSeconds=" + RebindingSeconds;
            }
        }
#else
        internal const bool IsEnabled = false;
        internal const int LeaseSeconds = 3600;
        internal const int RenewalSeconds = 1800;
        internal const int RebindingSeconds = 3150;

        internal static void EnsureConfigured()
        {
        }

        internal static bool ShouldSuppressOldCiaddrNak(DhcpRequestDecision decision)
        {
            return false;
        }

        internal static string DiagnosticText => "disabled";
#endif
    }

#if DHCP_OLD_LEASE_STUDY
    internal enum DhcpOldLeaseAction
    {
        Nak,
        Ignore
    }
#endif
}
