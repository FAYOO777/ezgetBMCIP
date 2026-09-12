using EzGetBmcIp;

internal static class FirewallRepairTests
{
    private const string Exe = @"C:\Program Files\ezgetBMCIP\ezgetBMCIP.exe";
    private static int _registrySequence;

    internal static void RunAll()
    {
        SameNameTcpAndUdpAreDisambiguated();

        using var policy = new FakePolicy();
        policy.Rules.Add(Block("user block", Exe, 6));
        policy.Rules.Add(Block("other application", @"C:\Other\app.exe", 4));
        var plan = FirewallRepair.Prepare(policy, Exe, "I350-左2", "Public");
        Check(plan.Blocks.Count == 1, "Only the current executable can be modified.");
        var text = string.Join("\n", plan.Notice().Items);
        Check(text.Contains("所有端口和接口") && text.Contains("规则将保留"),
            "Consent must disclose broader block scope and retained rules.");
        bool backedUp = false;
        FirewallRepair.Apply(policy, plan, _ => backedUp = true, _ => { });
        Check(backedUp && policy.Rules[0].Profiles == 2 && policy.Rules[0].Enabled, "Other profiles must remain blocked.");
        Check(policy.Rules[1].Profiles == 4 && policy.Rules[1].Enabled, "Other applications must remain untouched.");
        var allow = policy.Rules.Single(r => r.Name == plan.RuleName);
        Check(allow.Program == Exe && allow.Ports == "67" && allow.Protocol == 17 && allow.Profiles == 4 && allow.Interfaces == "I350-左2",
            "Allow must be constrained to the executable, adapter, profile and UDP/67.");
        var secondPlan = FirewallRepair.Prepare(policy, Exe, "I350-左2", "Public");
        Check(!secondPlan.AddRequired, "Repeated repair must reuse the exact allow instead of creating duplicates.");
        allow.UnrestrictedPeer = false;
        Check(FirewallRepair.Prepare(policy, Exe, "I350-左2", "Public").AddRequired,
            "An allow restricted to a peer, local address or service must not be mistaken for a DHCP permission.");

        using var anyProfile = new FakePolicy(); anyProfile.Rules.Add(Block("all profiles", Exe, int.MaxValue));
        var anyPlan = FirewallRepair.Prepare(anyProfile, Exe, "adapter", "Public");
        FirewallRepair.Apply(anyProfile, anyPlan, _ => { }, _ => { });
        Check(anyProfile.Rules[0].Profiles == 3, "Any profile must retain valid Domain and Private bits.");

        using var global = new FakePolicy(); global.Rules.Add(Block("global", "", 4));
        Throws(() => FirewallRepair.Prepare(global, Exe, "adapter", "Public"));
        using var foreign = new FakePolicy(); var foreignRule = Block("managed", Exe, 4); foreignRule.LocalRecord = ""; foreign.Rules.Add(foreignRule);
        Throws(() => FirewallRepair.Prepare(foreign, Exe, "adapter", "Public"));
        AmbiguousDuplicateIsRefused();
        using var restricted = new FakePolicy { Denied = true };
        Throws(() => FirewallRepair.Prepare(restricted, Exe, "adapter", "Public"));
        Throws(() => FirewallRepair.Prepare(policy, Exe, "adapter", "DomainAuthenticated"));

        using var unknown = new FakePolicy();
        var unknownPlan = FirewallRepair.Prepare(unknown, Exe, "adapter", "Unknown");
        Check(unknownPlan.CategoryUnknown && unknownPlan.Profile == 4 && unknownPlan.Notice().WarningText.Contains("未能确认"), "Unknown category requires explicit proposed Public scope.");
        Check(FirewallRepair.Prepare(unknown, Exe, "adapter", "Private").Profile == 2, "Known Private must not grant Public.");

        using var race = new FakePolicy();
        var racePlan = FirewallRepair.Prepare(race, Exe, "adapter", "Public");
        race.Rules.Add(Block("new rule", Exe, 4));
        Throws(() => FirewallRepair.Apply(race, racePlan, _ => { }, _ => { }));
        Check(race.Writes == 0, "Changes after consent must prevent writes.");

        using var backupFail = new FakePolicy(); backupFail.Rules.Add(Block("block", Exe, 4));
        var backupPlan = FirewallRepair.Prepare(backupFail, Exe, "adapter", "Public");
        Throws(() => FirewallRepair.Apply(backupFail, backupPlan, _ => throw new InvalidOperationException("backup failure"), _ => { }));
        Check(backupFail.Writes == 0, "Backup failure must prevent all mutations.");

        using var rollback = new FakePolicy { FailAdd = true }; rollback.Rules.Add(Block("block", Exe, 4));
        var rollbackPlan = FirewallRepair.Prepare(rollback, Exe, "adapter", "Public");
        Throws(() => FirewallRepair.Apply(rollback, rollbackPlan, _ => { }, _ => { }));
        Check(rollback.Rules.Count == 1 && rollback.Rules[0].Enabled && rollback.Rules[0].Profiles == 4, "Partial failure must restore the prior block.");
    }

    private static void SameNameTcpAndUdpAreDisambiguated()
    {
        using var policy = new FakePolicy();
        // This is the exact Windows firewall-dialog pattern: all four entries share a display Name, but their
        // registry identities and protocol semantics differ. Only the Public UDP block may be changed.
        var privateTcpAllow = Rule("ezgetbmcip.exe", Exe, 2, 6, 1, "private-tcp-allow");
        var privateUdpAllow = Rule("ezgetbmcip.exe", Exe, 2, 17, 1, "private-udp-allow");
        var publicTcpBlock = Rule("ezgetbmcip.exe", Exe, 4, 6, 0, "public-tcp-block");
        var publicUdpBlock = Rule("ezgetbmcip.exe", Exe, 4, 17, 0, "public-udp-block");
        policy.Rules.AddRange(new[] { privateTcpAllow, privateUdpAllow, publicTcpBlock, publicUdpBlock });

        var plan = FirewallRepair.Prepare(policy, Exe, "I350-左2", "Public");
        Check(plan.Blocks.Count == 1 && plan.Blocks[0].RegistryValueName == "public-udp-block" && plan.Blocks[0].Protocol == 17,
            "Same-name TCP and UDP firewall entries must resolve to only the Public UDP block.");
        FirewallRepair.Apply(policy, plan, _ => { }, _ => { });

        Check(policy.ScopedRegistryNames.SequenceEqual(new[] { "public-udp-block" }),
            "Mutation must use the exact registry-backed UDP identity, not Rules.Item(display name).");
        Check(privateTcpAllow.Enabled && privateTcpAllow.Profiles == 2 && privateTcpAllow.Action == 1 && privateTcpAllow.Protocol == 6,
            "Same-name Private TCP allow must remain untouched.");
        Check(privateUdpAllow.Enabled && privateUdpAllow.Profiles == 2 && privateUdpAllow.Action == 1 && privateUdpAllow.Protocol == 17,
            "Same-name Private UDP allow must remain untouched.");
        Check(publicTcpBlock.Enabled && publicTcpBlock.Profiles == 4 && publicTcpBlock.Action == 0 && publicTcpBlock.Protocol == 6,
            "Same-name Public TCP block must remain untouched.");
        Check(!publicUdpBlock.Enabled && publicUdpBlock.Profiles == 4 && publicUdpBlock.Action == 0 && publicUdpBlock.Protocol == 17,
            "Only the target Public UDP block may be disabled when it has no remaining profiles.");

        using var rollback = new FakePolicy { FailAdd = true };
        var tcp = Rule("ezgetbmcip.exe", Exe, 4, 6, 0, "rollback-tcp");
        var udp = Rule("ezgetbmcip.exe", Exe, 4, 17, 0, "rollback-udp");
        rollback.Rules.AddRange(new[] { tcp, udp });
        var rollbackPlan = FirewallRepair.Prepare(rollback, Exe, "I350-左2", "Public");
        Throws(() => FirewallRepair.Apply(rollback, rollbackPlan, _ => { }, _ => { }));
        Check(tcp.Enabled && tcp.Profiles == 4 && udp.Enabled && udp.Profiles == 4,
            "Rollback must restore only the exact UDP identity and leave same-name TCP unchanged.");
    }

    private static void AmbiguousDuplicateIsRefused()
    {
        using var duplicate = new FakePolicy();
        var first = Block("same", Exe, 4);
        var second = Block("same", Exe, 4);
        // Simulate two COM objects that both map to the same local value. This is unsafe even though the display
        // name alone is not the issue; the repair must conservatively refuse it.
        second.RegistryValueName = first.RegistryValueName;
        duplicate.Rules.Add(first);
        duplicate.Rules.Add(second);
        Throws(() => FirewallRepair.Prepare(duplicate, Exe, "adapter", "Public"));
    }

    private static RepairRule Block(string name, string program, int profiles) => Rule(name, program, profiles, 17, 0, "block-" + ++_registrySequence);

    private static RepairRule Rule(string name, string program, int profiles, int protocol, int action, string registryValueName)
    {
        return new RepairRule
        {
            Name = name,
            Program = program,
            Profiles = profiles,
            Enabled = true,
            Direction = 1,
            Action = action,
            Protocol = protocol,
            Ports = "*",
            RemotePorts = "*",
            LocalAddresses = "*",
            RemoteAddresses = "*",
            Interfaces = "*",
            InterfaceTypes = "*",
            LocalRecord = "v2.30|Name=" + name + "|App=" + program + "|Protocol=" + protocol,
            RegistryValueName = registryValueName
        };
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected a conservative refusal or rollback.");
    }

    private sealed class FakePolicy : IFirewallRepairPolicy
    {
        internal readonly List<RepairRule> Rules = new();
        internal readonly List<string> ScopedRegistryNames = new();
        internal bool Denied, FailAdd;
        internal int Writes;

        public void CheckWritable(int profile) { if (Denied) throw new InvalidOperationException("managed policy"); }

        public List<RepairRule> ReadRules() => Rules.Select(Clone).ToList();

        public void SetScope(RepairRule expectedRule, int profiles, bool enabled)
        {
            Writes++;
            var matching = Rules.Where(rule => SameIdentity(rule, expectedRule)).ToList();
            if (matching.Count != 1) throw new InvalidOperationException("ambiguous exact identity");
            matching[0].Profiles = profiles;
            matching[0].Enabled = enabled;
            ScopedRegistryNames.Add(matching[0].RegistryValueName);
        }

        public void AddAllow(FirewallRepairPlan plan)
        {
            Writes++;
            if (FailAdd) throw new InvalidOperationException("write failure");
            Rules.Add(new RepairRule
            {
                Name = plan.RuleName,
                Program = plan.ExecutablePath,
                Profiles = plan.Profile,
                Enabled = true,
                Direction = 1,
                Action = 1,
                Protocol = 17,
                Ports = "67",
                RemotePorts = "*",
                LocalAddresses = "*",
                RemoteAddresses = "*",
                Interfaces = plan.AdapterName,
                InterfaceTypes = "*",
                UnrestrictedPeer = true,
                LocalRecord = "fake generated allow",
                RegistryValueName = "allow-" + plan.RuleName
            });
        }

        public void Remove(string name) { Writes++; Rules.RemoveAll(rule => rule.Name == name); }
        public void Dispose() { }

        private static RepairRule Clone(RepairRule rule)
        {
            var clone = new RepairRule
            {
                Name = rule.Name,
                Program = rule.Program,
                Profiles = rule.Profiles,
                Enabled = rule.Enabled,
                Direction = rule.Direction,
                Action = rule.Action,
                Protocol = rule.Protocol,
                Ports = rule.Ports,
                RemotePorts = rule.RemotePorts,
                LocalAddresses = rule.LocalAddresses,
                RemoteAddresses = rule.RemoteAddresses,
                Interfaces = rule.Interfaces,
                InterfaceTypes = rule.InterfaceTypes,
                ServiceName = rule.ServiceName,
                IcmpTypesAndCodes = rule.IcmpTypesAndCodes,
                EdgeTraversal = rule.EdgeTraversal,
                EdgeTraversalOptions = rule.EdgeTraversalOptions,
                LocalRecord = rule.LocalRecord,
                RegistryValueName = rule.RegistryValueName,
                UnrestrictedPeer = rule.UnrestrictedPeer
            };
            clone.RefreshIdentity();
            return clone;
        }

        private static bool SameIdentity(RepairRule left, RepairRule right)
        {
            var leftSnapshot = Clone(left);
            return leftSnapshot.SameImmutableIdentity(right);
        }
    }
}
