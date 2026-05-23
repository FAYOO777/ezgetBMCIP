using System.Net;

namespace EzGetBmcIp;

public sealed record WiredAdapter(
    string Name,
    string Description,
    string Id,
    string MacAddress)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Description)
        ? Name
        : Name + " - " + Description;
}

public sealed class AdapterOriginalConfig
{
    public bool DhcpEnabled { get; init; }
    public List<(IPAddress Address, IPAddress Mask)> StaticAddresses { get; } = new();
    public List<IPAddress> Gateways { get; } = new();
    public List<IPAddress> DnsServers { get; } = new();

    public static AdapterOriginalConfig CreateDhcp()
    {
        return new AdapterOriginalConfig { DhcpEnabled = true };
    }
}

public sealed class DhcpLease
{
    public IPAddress IpAddress { get; init; } = IPAddress.None;
    public byte[] MacAddress { get; init; } = Array.Empty<byte>();
    public DateTime AssignedAt { get; init; } = DateTime.Now;

    public string MacAddressText => string.Join("-", MacAddress.Select(b => b.ToString("X2")));
}
