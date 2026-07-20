using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;

namespace EzGetBmcIp
{
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
        public bool IsPrivateSubnet => Octet1 == 10
            || (Octet1 == 172 && Octet2 >= 16 && Octet2 <= 31)
            || (Octet1 == 192 && Octet2 == 168);
        public string ValidationError => IsPrivateSubnet
            ? null
            : "自定义网段必须使用私有 IPv4 地址段：10.x.x.x、172.16-31.x.x 或 192.168.x.x。";

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
            OnPropertyChanged(nameof(IsPrivateSubnet));
            OnPropertyChanged(nameof(ValidationError));
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
            OnPropertyChanged(nameof(PoolDisplay));
            OnPropertyChanged(nameof(IsPrivateSubnet));
            OnPropertyChanged(nameof(ValidationError));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class WiredAdapter : IEquatable<WiredAdapter>
    {
        public string Name { get; }
        public string Description { get; }
        public string Id { get; }
        public string MacAddress { get; }

        public WiredAdapter(string name, string description, string id, string macAddress)
        {
            Name = name;
            Description = description;
            Id = id;
            MacAddress = macAddress;
        }

        public string DisplayName => string.IsNullOrWhiteSpace(Description)
            ? Name
            : Name + " - " + Description;

        public bool Equals(WiredAdapter other)
        {
            if (other == null) return false;
            return Name == other.Name && Description == other.Description
                && Id == other.Id && MacAddress == other.MacAddress;
        }

        public override bool Equals(object obj) => Equals(obj as WiredAdapter);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + (Name?.GetHashCode() ?? 0);
                hash = hash * 31 + (Description?.GetHashCode() ?? 0);
                hash = hash * 31 + (Id?.GetHashCode() ?? 0);
                hash = hash * 31 + (MacAddress?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }

    public sealed class AdapterOriginalConfig
    {
        public bool DhcpEnabled { get; set; }
        public bool DnsServersFromDhcp { get; set; }
        public List<(IPAddress Address, IPAddress Mask)> StaticAddresses { get; } = new List<(IPAddress, IPAddress)>();
        public List<IPAddress> Gateways { get; } = new List<IPAddress>();
        public List<int> GatewayMetrics { get; } = new List<int>();
        public List<IPAddress> DnsServers { get; } = new List<IPAddress>();

        public static AdapterOriginalConfig CreateDhcp()
        {
            return new AdapterOriginalConfig { DhcpEnabled = true, DnsServersFromDhcp = true };
        }
    }

    public sealed class NetworkConfigVerification
    {
        public bool IsSuccess { get; set; }
        public bool ModeMatches { get; set; }
        public bool AddressesMatch { get; set; }
        public bool GatewaysMatch { get; set; }
        public bool DnsMatches { get; set; }
        public bool ToolAddressesRemoved { get; set; }
        public string Details { get; set; } = string.Empty;
    }

    public sealed class DhcpLease
    {
        public IPAddress IpAddress { get; set; } = IPAddress.None;
        public byte[] MacAddress { get; set; } = new byte[0];
        public DateTime AssignedAt { get; set; } = DateTime.Now;

        public string MacAddressText => string.Join("-", MacAddress.Select(b => b.ToString("X2")));
    }
}
