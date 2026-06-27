using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace EzGetBmcIp;

public sealed class SubnetConfig : INotifyPropertyChanged
{
    private byte _octet1 = 10;
    private byte _octet2 = 77;
    private byte _octet3 = 77;
    private byte _octet4 = 1;

    public byte Octet1
    {
        get => _octet1;
        set { if (_octet1 != value) { _octet1 = value; OnChanged(); } }
    }

    public byte Octet2
    {
        get => _octet2;
        set { if (_octet2 != value) { _octet2 = value; OnChanged(); } }
    }

    public byte Octet3
    {
        get => _octet3;
        set { if (_octet3 != value) { _octet3 = value; OnChanged(); } }
    }

    public byte Octet4
    {
        get => _octet4;
        set { if (_octet4 != value) { _octet4 = value; OnChanged(); } }
    }

    public string ServerIp => $"{Octet1}.{Octet2}.{Octet3}.{Octet4}";
    public string SubnetPrefix => $"{Octet1}.{Octet2}.{Octet3}";
    public string Mask => "255.255.255.0";
    public string PoolStart => $"{Octet1}.{Octet2}.{Octet3}.100";
    public string PoolDisplay => $"{Octet1}.{Octet2}.{Octet3}.100";
    public string ServerDisplay => ServerIp + " / 24";

    public string Octet1Text { get => _octet1.ToString(); set => TryParseOctet(value, v => Octet1 = v, 1, 223); }
    public string Octet2Text { get => _octet2.ToString(); set => TryParseOctet(value, v => Octet2 = v, 0, 255); }
    public string Octet3Text { get => _octet3.ToString(); set => TryParseOctet(value, v => Octet3 = v, 0, 255); }
    public string Octet4Text { get => _octet4.ToString(); set => TryParseOctet(value, v => { if (v == 100) v = 101; Octet4 = v; }, 1, 254); }

    private void TryParseOctet(string text, Action<byte> setter, byte min, byte max)
    {
        if (byte.TryParse(text, out var val) && val >= min && val <= max)
            setter(val);
        OnPropertyChanged(nameof(ServerIp));
        OnPropertyChanged(nameof(SubnetPrefix));
        OnPropertyChanged(nameof(PoolStart));
        OnPropertyChanged(nameof(PoolDisplay));
        OnPropertyChanged(nameof(ServerDisplay));
    }

    private void OnChanged()
    {
        OnPropertyChanged(nameof(Octet1));
        OnPropertyChanged(nameof(Octet1Text));
        OnPropertyChanged(nameof(Octet2));
        OnPropertyChanged(nameof(Octet2Text));
        OnPropertyChanged(nameof(Octet3));
        OnPropertyChanged(nameof(Octet3Text));
        OnPropertyChanged(nameof(Octet4));
        OnPropertyChanged(nameof(Octet4Text));
        OnPropertyChanged(nameof(ServerIp));
        OnPropertyChanged(nameof(SubnetPrefix));
        OnPropertyChanged(nameof(Mask));
        OnPropertyChanged(nameof(PoolStart));
        OnPropertyChanged(nameof(ServerDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

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
