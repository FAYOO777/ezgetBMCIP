namespace EzGetBmcIp
{
    internal static class BmcHistoryRetryPolicy
    {
        internal static bool ShouldOffer(
            WiredAdapter adapter,
            SubnetConfig currentSubnet,
            BmcHistoryRecord history,
            bool historyRetryUsed,
            FirewallRiskLevel? timeoutFirewallRisk)
        {
            return adapter != null
                && currentSubnet != null
                && history != null
                // IsValid accepts current address-reachability evidence and
                // legacy HTTP-response records; older TCP-only records remain
                // ineligible for a suggested subnet retry.
                && history.IsValid()
                && history.IsForAdapter(adapter)
                && !history.IsSameSubnet(currentSubnet)
                && !historyRetryUsed
                && timeoutFirewallRisk != FirewallRiskLevel.High;
        }
    }
}
