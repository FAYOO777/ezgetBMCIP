#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace EzGetBmcIp
{
    internal sealed class RepairRule
    {
        internal string Name = "", Program = "", Ports = "", Interfaces = "", LocalRecord = "", Fingerprint = "";
        // RegistryValueName is the local firewall-store identity. Display Name is deliberately not an identity:
        // Windows commonly creates a TCP and UDP rule with the same display name for one executable.
        internal string RegistryValueName = "", StableIdentity = "";
        internal string ServiceName = "", LocalAddresses = "", RemoteAddresses = "", RemotePorts = "", InterfaceTypes = "", IcmpTypesAndCodes = "";
        internal int Protocol, Profiles, Action, Direction, EdgeTraversal, EdgeTraversalOptions;
        internal bool Enabled;
        internal bool UnrestrictedPeer;

        internal bool Affects(int profile) => Enabled && Direction == 1 && (Profiles & profile) != 0 &&
            (Protocol == 17 || Protocol == 256) && Includes67(Ports);
        internal bool AffectsAdapter(string adapter) => string.IsNullOrWhiteSpace(Interfaces) || Interfaces == "*" ||
            Interfaces.Split(',').Any(value => string.Equals(value.Trim(), adapter, StringComparison.OrdinalIgnoreCase));
        internal bool HasVerifiedLocalIdentity => !string.IsNullOrWhiteSpace(RegistryValueName) && !string.IsNullOrWhiteSpace(LocalRecord) &&
            !string.IsNullOrWhiteSpace(StableIdentity);

        internal void RefreshIdentity()
        {
            var immutable = ImmutableSignature();
            StableIdentity = string.IsNullOrWhiteSpace(RegistryValueName) ? "" : RegistryValueName + "\u001f" + immutable;
            // Fingerprint intentionally includes mutable state and the raw local record. It rejects a post-consent
            // change before the first write. StableIdentity is used only after a known write during rollback.
            Fingerprint = StableIdentity + "\u001fprofiles=" + Profiles + "\u001fenabled=" + Enabled + "\u001fraw=" + (LocalRecord ?? "");
        }

        internal bool SameImmutableIdentity(RepairRule other)
        {
            return other != null && HasVerifiedLocalIdentity && other.HasVerifiedLocalIdentity &&
                string.Equals(RegistryValueName, other.RegistryValueName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ImmutableSignature(), other.ImmutableSignature(), StringComparison.Ordinal);
        }

        private string ImmutableSignature()
        {
            return string.Join("\u001f", new[]
            {
                Text(Name), PathText(Program), Protocol.ToString(), Action.ToString(), Direction.ToString(),
                ListText(Ports), ListText(RemotePorts), ListText(LocalAddresses), ListText(RemoteAddresses),
                ListText(Interfaces), Text(ServiceName), ListText(InterfaceTypes), ListText(IcmpTypesAndCodes),
                EdgeTraversal.ToString(), EdgeTraversalOptions.ToString(), UnrestrictedPeer.ToString()
            });
        }

        internal static bool Includes67(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "*") return true;
            foreach (var port in value.Split(','))
            {
                if (port.Trim() == "67") return true;
                var ends = port.Trim().Split('-');
                if (ends.Length == 2 && int.TryParse(ends[0], out var a) && int.TryParse(ends[1], out var b) && a <= 67 && b >= 67) return true;
            }
            return false;
        }

        internal static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || a == "*" || b == "*") return false;
            try
            {
                return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(a.Trim('"'))),
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(b.Trim('"'))), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        internal static bool TextEquals(string a, string b) => string.Equals(Text(a), Text(b), StringComparison.Ordinal);
        internal static bool ListEquals(string a, string b) => string.Equals(ListText(a), ListText(b), StringComparison.Ordinal);
        internal static bool IsWildcard(string value) => string.IsNullOrWhiteSpace(value) || value.Trim() == "*" ||
            string.Equals(value.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        private static string Text(string value) => (value ?? "").Trim().ToUpperInvariant();
        private static string PathText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim('"'))).ToUpperInvariant(); }
            catch { return Text(value); }
        }
        private static string ListText(string value)
        {
            if (IsWildcard(value)) return "*";
            return string.Join(",", (value ?? "").Split(',').Select(item => Text(item)).Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal));
        }
    }

    internal sealed class FirewallRepairPlan
    {
        internal string ExecutablePath, AdapterName, Category, Evidence;
        internal int Profile;
        internal bool CategoryUnknown;
        internal bool AddRequired = true;
        internal List<RepairRule> Blocks = new List<RepairRule>();
        internal string RuleName = "ezgetBMCIP DHCP repair " + Guid.NewGuid().ToString("N");
        internal ConsentNotice Notice()
        {
            var items = new List<string>
            {
                "目标网卡：" + AdapterName + "。先停止本轮 DHCP 并恢复本机网卡；恢复成功后才处理防火墙。",
                (AddRequired ? "添加允许规则：当前程序 " : "复用现有允许规则：当前程序 ") + ExecutablePath + "；" + Category + "；仅入站 UDP/67；仅目标网卡。",
                "规则将保留供后续使用，退出工具时不自动撤销。变更前保存备份，处理失败时尝试回滚本次防火墙变更。"
            };
            foreach (var rule in Blocks)
                items.Add("处理本机阻止规则“" + rule.Name + "”：取消它在" + Category + "下的阻止；原协议=" + rule.Protocol +
                    "，原端口=" + (string.IsNullOrEmpty(rule.Ports) ? "全部" : rule.Ports) +
                    "。这会影响该规则在此网络类型下覆盖的所有端口和接口，不仅是 UDP/67；其他网络类型保留。");
            items.Add("完成后保留本次网段，返回选择页，由你手动点击开始。不修改 BMC、不关闭防火墙；公司策略或无法确认来源的规则需管理员处理。");
            return new ConsentNotice("处理防火墙并准备重试", "请先关闭 Windows 防火墙访问提示，再核对下列变更。", items,
                "我已核对规则及影响，确认处理并恢复网卡", "确认处理并准备重试",
                CategoryUnknown ? "未能确认目标网卡的网络类别。本次拟按公用网络（Public）处理；仅在确认该直连网卡使用公用网络时继续。" : "");
        }
    }

    // Injectable policy boundary: regression tests never write the host firewall.
    internal interface IFirewallRepairPolicy : IDisposable
    {
        void CheckWritable(int profile);
        List<RepairRule> ReadRules();
        // expectedRule is a registry-backed identity of one exact COM rule. It must never be looked up by display name.
        void SetScope(RepairRule expectedRule, int profiles, bool enabled);
        void AddAllow(FirewallRepairPlan plan);
        void Remove(string name);
    }

    internal static class FirewallRepair
    {
        internal static FirewallRepairPlan Prepare(IFirewallRepairPolicy policy, string exe, string adapter, string category)
        {
            if (string.IsNullOrWhiteSpace(adapter) || !Path.IsPathRooted(exe)) throw new InvalidOperationException("当前程序路径或目标网卡无法确认。");
            if (category == "Domain" || category == "DomainAuthenticated") throw new InvalidOperationException("域网络防火墙请由管理员处理。");
            var plan = new FirewallRepairPlan { ExecutablePath = exe, AdapterName = adapter,
                Profile = category == "Private" ? 2 : 4, Category = category == "Private" ? "专用网络（Private）" : "公用网络（Public）",
                CategoryUnknown = category != "Private" && category != "Public" };
            policy.CheckWritable(plan.Profile);
            var rules = policy.ReadRules();
            var existing = rules.FirstOrDefault(r => r.Enabled && r.Direction == 1 && r.Action == 1 && r.Protocol == 17 &&
                r.UnrestrictedPeer && r.Name.StartsWith("ezgetBMCIP DHCP repair ", StringComparison.Ordinal) &&
                r.Ports == "67" && r.Profiles == plan.Profile && r.Interfaces == adapter && RepairRule.SamePath(r.Program, exe));
            if (existing != null) { plan.RuleName = existing.Name; plan.AddRequired = false; }
            foreach (var rule in rules.Where(r => r.Affects(plan.Profile) && r.AffectsAdapter(adapter) && r.Action == 0))
            {
                // Global blocks may cover UDP/67. Never edit them or claim an Allow overrides them.
                if (string.IsNullOrWhiteSpace(rule.Program) || rule.Program == "*")
                    throw new InvalidOperationException("发现覆盖 UDP/67 的通用阻止规则：" + rule.Name + "。请由管理员处理后再试。");
                if (!RepairRule.SamePath(rule.Program, exe)) continue;
                // A display name is not unique. Require the local registry value and complete immutable identity.
                if (!rule.HasVerifiedLocalIdentity || rules.Count(r => r.SameImmutableIdentity(rule)) != 1)
                    throw new InvalidOperationException("无法确认阻止规则的本机来源或唯一性：" + rule.Name + "。请由管理员处理。");
                plan.Blocks.Add(rule);
            }
            if (plan.Blocks.GroupBy(r => r.StableIdentity, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
                throw new InvalidOperationException("无法唯一确认待处理的阻止规则，请由管理员处理。");
            plan.Evidence = Evidence(rules, plan);
            return plan;
        }

        private static string Evidence(List<RepairRule> rules, FirewallRepairPlan plan) => string.Join("\n",
            rules.Where(r => r.Affects(plan.Profile) && r.AffectsAdapter(plan.AdapterName) && (string.IsNullOrWhiteSpace(r.Program) || r.Program == "*" || RepairRule.SamePath(r.Program, plan.ExecutablePath)))
            .Select(r => r.Fingerprint).OrderBy(s => s, StringComparer.Ordinal));

        private static RepairRule RequireCurrentRule(IFirewallRepairPolicy policy, RepairRule original, bool requireExactSnapshot)
        {
            var candidates = policy.ReadRules().Where(rule => rule.SameImmutableIdentity(original)).ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException("待处理的阻止规则已变化或无法唯一确认：" + original.Name);
            if (requireExactSnapshot && !string.Equals(candidates[0].Fingerprint, original.Fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("待处理的阻止规则已变化：" + original.Name);
            return candidates[0];
        }

        internal static void Apply(IFirewallRepairPolicy policy, FirewallRepairPlan plan, Action<FirewallRepairPlan> backup, Action<string> log)
        {
            policy.CheckWritable(plan.Profile);
            if (Evidence(policy.ReadRules(), plan) != plan.Evidence)
                throw new InvalidOperationException("确认后防火墙规则发生变化，未执行修改。请再次点击处理并核对新规则。");
            backup(plan); // Durable record must exist before the first mutation.
            var changed = new List<RepairRule>();
            var addAttempted = false;
            try
            {
                foreach (var rule in plan.Blocks)
                {
                    var current = RequireCurrentRule(policy, rule, true);
                    changed.Add(rule);
                    var remaining = (rule.Profiles & 7) & ~plan.Profile;
                    policy.SetScope(current, remaining == 0 ? rule.Profiles : remaining, remaining != 0);
                    log("Firewall repair adjusted block: " + rule.Name + "; registry=" + rule.RegistryValueName);
                }
                if (plan.AddRequired)
                {
                    addAttempted = true;
                    policy.AddAllow(plan);
                }
                policy.CheckWritable(plan.Profile);
                var result = policy.ReadRules();
                if (result.Count(r => r.Name == plan.RuleName && r.Enabled && r.Direction == 1 && r.Action == 1 && r.Protocol == 17 &&
                    r.UnrestrictedPeer && r.Ports == "67" && r.Profiles == plan.Profile && r.Interfaces == plan.AdapterName && RepairRule.SamePath(r.Program, plan.ExecutablePath)) != 1 ||
                    result.Any(r => r.Affects(plan.Profile) && r.AffectsAdapter(plan.AdapterName) && r.Action == 0 &&
                        (string.IsNullOrWhiteSpace(r.Program) || r.Program == "*" || RepairRule.SamePath(r.Program, plan.ExecutablePath))))
                    throw new InvalidOperationException("规则复查未通过，不能确认 UDP/67 授权已生效。");
                log("Firewall repair verified: " + plan.RuleName + "; profile=" + plan.Profile + "; app=" + plan.ExecutablePath);
            }
            catch (Exception original)
            {
                var errors = new List<string>();
                if (addAttempted)
                    try
                    {
                        policy.Remove(plan.RuleName); // generated GUID name is owned by this transaction only.
                        if (policy.ReadRules().Any(r => r.Name == plan.RuleName)) throw new InvalidOperationException("新增允许规则仍存在，未确认回滚。");
                    }
                    catch (Exception ex) { errors.Add(ex.Message); }
                foreach (var rule in changed.AsEnumerable().Reverse())
                    try
                    {
                        var current = RequireCurrentRule(policy, rule, false);
                        policy.SetScope(current, rule.Profiles, rule.Enabled);
                        var restored = RequireCurrentRule(policy, rule, false);
                        if (restored.Profiles != rule.Profiles || restored.Enabled != rule.Enabled)
                            throw new InvalidOperationException("无法确认规则已回滚：" + rule.Name);
                    }
                    catch (Exception ex) { errors.Add(ex.Message); }
                var rollback = errors.Count == 0 ? "本次防火墙变更已回滚。" : "防火墙回滚未完成，请交由管理员处理：" + string.Join("；", errors);
                log("Firewall repair failed: " + original.GetBaseException().Message + " " + rollback);
                throw new InvalidOperationException(original.GetBaseException().Message + " " + rollback, original);
            }
        }

        internal static void SaveBackup(FirewallRepairPlan plan, Action<string> log)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ezgetBMCIP", "FirewallBackups");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".txt");
            var lines = new List<string> { "UTC: " + DateTime.UtcNow.ToString("O"), "EXE: " + plan.ExecutablePath,
                "Adapter: " + plan.AdapterName, "Profile: " + plan.Profile, "NewAllowRule: " + plan.RuleName };
            foreach (var rule in plan.Blocks)
            {
                lines.Add("Original rule: " + rule.Name);
                lines.Add("RegistryValueName=" + rule.RegistryValueName + "; StableIdentity=" + rule.StableIdentity + "; Fingerprint=" + rule.Fingerprint);
                lines.Add("Profiles=" + rule.Profiles + "; Enabled=" + rule.Enabled);
                lines.Add(rule.LocalRecord);
            }
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            log("Firewall repair backup: " + path);
        }
    }

    internal sealed class NativeFirewallRepairPolicy : IFirewallRepairPolicy
    {
        private sealed class LocalFirewallRecord
        {
            internal string ValueName;
            internal string Raw;
        }

        private readonly object policy;
        internal NativeFirewallRepairPolicy() { policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)); }
        private static object Get(object instance, string name, params object[] args) => instance.GetType().InvokeMember(name,
            BindingFlags.GetProperty, null, instance, args);
        private static void Set(object instance, string name, object value) => instance.GetType().InvokeMember(name,
            BindingFlags.SetProperty, null, instance, new[] { value });
        private static object Call(object instance, string name, params object[] args) => instance.GetType().InvokeMember(name,
            BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, instance, args);
        private static void Release(object instance) { if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance); }
        public void Dispose() { Release(policy); }

        public void CheckWritable(int profile)
        {
            if (Convert.ToInt32(Get(policy, "LocalPolicyModifyState")) != 0 || Convert.ToBoolean(Get(policy, "BlockAllInboundTraffic", profile)))
                throw new InvalidOperationException("本机防火墙规则受策略限制或已设置阻止所有入站连接，请由管理员处理。");
            var profileKey = profile == 2 ? "StandardProfile" : "PublicProfile";
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\WindowsFirewall\" + profileKey))
            {
                if (key != null && (Convert.ToString(key.GetValue("AllowLocalPolicyMerge", 1)) == "0" ||
                    Convert.ToString(key.GetValue("DoNotAllowExceptions", 0)) == "1"))
                    throw new InvalidOperationException("组织策略不允许本机入站例外，请由管理员处理。");
            }
        }

        public List<RepairRule> ReadRules()
        {
            var result = new List<RepairRule>();
            var local = ReadLocalRecords();
            var collection = Get(policy, "Rules");
            try
            {
                foreach (var item in (IEnumerable)collection)
                {
                    try { result.Add(ReadRule(item, local)); }
                    finally { Release(item); }
                }
            }
            finally { Release(collection); }
            return result;
        }

        private static List<LocalFirewallRecord> ReadLocalRecords()
        {
            var local = new List<LocalFirewallRecord>();
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"))
            {
                if (key != null)
                    foreach (var valueName in key.GetValueNames())
                    {
                        var raw = key.GetValue(valueName) as string;
                        if (!string.IsNullOrEmpty(raw)) local.Add(new LocalFirewallRecord { ValueName = valueName, Raw = raw });
                    }
            }
            return local;
        }

        private static RepairRule ReadRule(object item, List<LocalFirewallRecord> local)
        {
            var protocol = Convert.ToInt32(Get(item, "Protocol"));
            var rule = new RepairRule
            {
                Name = Convert.ToString(Get(item, "Name")),
                Program = Convert.ToString(Get(item, "ApplicationName")),
                Protocol = protocol,
                Profiles = Convert.ToInt32(Get(item, "Profiles")),
                Action = Convert.ToInt32(Get(item, "Action")),
                Direction = Convert.ToInt32(Get(item, "Direction")),
                Enabled = Convert.ToBoolean(Get(item, "Enabled")),
                Ports = protocol == 17 || protocol == 6 ? Convert.ToString(Get(item, "LocalPorts")) : "*",
                RemotePorts = protocol == 17 || protocol == 6 ? Convert.ToString(Get(item, "RemotePorts")) : "*",
                LocalAddresses = Convert.ToString(Get(item, "LocalAddresses")),
                RemoteAddresses = Convert.ToString(Get(item, "RemoteAddresses")),
                ServiceName = Convert.ToString(Get(item, "ServiceName")),
                InterfaceTypes = Convert.ToString(Get(item, "InterfaceTypes")),
                IcmpTypesAndCodes = protocol == 1 || protocol == 58 ? Convert.ToString(Get(item, "IcmpTypesAndCodes")) : "*",
                EdgeTraversal = Convert.ToInt32(Get(item, "EdgeTraversal")),
                EdgeTraversalOptions = OptionalInt(item, "EdgeTraversalOptions"),
                Interfaces = ReadInterfaces(item)
            };
            rule.UnrestrictedPeer = protocol == 17 && string.IsNullOrEmpty(rule.ServiceName) &&
                RepairRule.IsWildcard(rule.LocalAddresses) && RepairRule.IsWildcard(rule.RemoteAddresses) && RepairRule.IsWildcard(rule.RemotePorts);
            var matches = local.Where(record => MatchesLocalRecord(record, rule)).ToList();
            if (matches.Count == 1)
            {
                rule.RegistryValueName = matches[0].ValueName;
                rule.LocalRecord = matches[0].Raw;
            }
            rule.RefreshIdentity();
            return rule;
        }

        private static string ReadInterfaces(object item)
        {
            var raw = Get(item, "Interfaces");
            if (raw == null) return "*";
            var text = raw as string;
            if (text != null) return string.IsNullOrWhiteSpace(text) ? "*" : text;
            var enumerable = raw as IEnumerable;
            if (enumerable == null) return "*";
            var values = enumerable.Cast<object>().Select(Convert.ToString).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            return values.Length == 0 ? "*" : string.Join(",", values);
        }

        // Match a COM rule to one local registry value by traffic semantics, not by display Name. This separates
        // Windows' paired same-name TCP and UDP rules. Ambiguous matches intentionally get no identity.
        private static bool MatchesLocalRecord(LocalFirewallRecord record, RepairRule rule)
        {
            if (string.IsNullOrWhiteSpace(rule.Program)) return false;
            if (!RepairRule.TextEquals(Field(record.Raw, "Name"), rule.Name) || !RepairRule.SamePath(Field(record.Raw, "App"), rule.Program)) return false;
            if (!RepairRule.TextEquals(Field(record.Raw, "Action"), rule.Action == 0 ? "Block" : "Allow") ||
                !RepairRule.TextEquals(Field(record.Raw, "Active"), rule.Enabled ? "TRUE" : "FALSE") ||
                !RepairRule.TextEquals(Field(record.Raw, "Dir"), rule.Direction == 1 ? "In" : "Out")) return false;
            if (!IntegerFieldMatches(record.Raw, "Protocol", rule.Protocol) || !ProfileFieldMatches(record.Raw, rule.Profiles)) return false;
            if (!ListFieldMatches(record.Raw, rule.Ports, "LPort", "LPort2_10") || !ListFieldMatches(record.Raw, rule.RemotePorts, "RPort")) return false;
            if (!AddressFieldMatches(record.Raw, rule.LocalAddresses, "LA4", "LA6") || !AddressFieldMatches(record.Raw, rule.RemoteAddresses, "RA4", "RA6")) return false;
            if (!TextFieldMatches(record.Raw, rule.ServiceName, "Svc") || !ListFieldMatches(record.Raw, rule.Interfaces, "IF") ||
                !ListFieldMatches(record.Raw, rule.InterfaceTypes, "IFType", "InterfaceTypes")) return false;
            if (!EdgeFieldMatches(record.Raw, rule)) return false;
            return true;
        }

        private static int OptionalInt(object item, string property)
        {
            try { return Convert.ToInt32(Get(item, property)); }
            catch (MissingMethodException) { return 0; }
            catch (TargetInvocationException) { return 0; }
        }

        private static bool EdgeFieldMatches(string raw, RepairRule rule)
        {
            var value = Field(raw, "Edge");
            if (string.IsNullOrEmpty(value)) return rule.EdgeTraversal == 0 && rule.EdgeTraversalOptions == 0;
            if (!bool.TryParse(value, out var expected)) return false;
            // The registry's Edge=TRUE form represents the normal edge-traversal option (1). A different
            // option cannot be tied to this local value safely, so leave it for an administrator.
            return expected ? rule.EdgeTraversal != 0 && rule.EdgeTraversalOptions == 1 :
                rule.EdgeTraversal == 0 && rule.EdgeTraversalOptions == 0;
        }

        private static bool IntegerFieldMatches(string raw, string key, int actual)
        {
            var value = Field(raw, key);
            return !string.IsNullOrEmpty(value) && int.TryParse(value, out var expected) && expected == actual;
        }

        private static bool ProfileFieldMatches(string raw, int actual)
        {
            var values = Fields(raw, "Profile").ToArray();
            if (values.Length == 0) return actual == int.MaxValue || (actual & 7) == 7;
            var expected = 0;
            foreach (var value in values)
            {
                if (int.TryParse(value, out var number)) expected |= number & 7;
                else if (string.Equals(value, "Domain", StringComparison.OrdinalIgnoreCase)) expected |= 1;
                else if (string.Equals(value, "Private", StringComparison.OrdinalIgnoreCase)) expected |= 2;
                else if (string.Equals(value, "Public", StringComparison.OrdinalIgnoreCase)) expected |= 4;
                else if (string.Equals(value, "All", StringComparison.OrdinalIgnoreCase)) expected |= 7;
                else return false;
            }
            return (actual & 7) == expected;
        }

        private static bool TextFieldMatches(string raw, string actual, params string[] keys)
        {
            var values = keys.Select(key => Field(raw, key)).Where(value => !string.IsNullOrEmpty(value)).ToArray();
            return values.Length == 0 ? string.IsNullOrEmpty(actual) : values.All(value => RepairRule.TextEquals(value, actual));
        }

        private static bool ListFieldMatches(string raw, string actual, params string[] keys)
        {
            var values = keys.Select(key => Field(raw, key)).Where(value => !string.IsNullOrEmpty(value)).ToArray();
            if (values.Length == 0) return RepairRule.IsWildcard(actual);
            return RepairRule.ListEquals(string.Join(",", values), actual);
        }

        private static bool AddressFieldMatches(string raw, string actual, params string[] keys) => ListFieldMatches(raw, actual, keys);
        private static IEnumerable<string> Fields(string raw, string key) => raw.Split('|')
            .Where(section => section.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            .Select(section => section.Substring(key.Length + 1));
        private static string Field(string raw, string key) => Fields(raw, key).FirstOrDefault() ?? "";

        public void SetScope(RepairRule expectedRule, int profiles, bool enabled)
        {
            if (expectedRule == null || !expectedRule.HasVerifiedLocalIdentity)
                throw new InvalidOperationException("无法确认待修改的本机防火墙规则身份。");
            var local = ReadLocalRecords();
            var rules = Get(policy, "Rules");
            object selected = null;
            try
            {
                foreach (var item in (IEnumerable)rules)
                {
                    var current = ReadRule(item, local);
                    if (current.SameImmutableIdentity(expectedRule))
                    {
                        if (selected != null)
                        {
                            Release(item);
                            throw new InvalidOperationException("找到多条同一身份的防火墙规则，拒绝修改。");
                        }
                        selected = item;
                    }
                    else Release(item);
                }
                if (selected == null) throw new InvalidOperationException("未找到待修改的防火墙规则，拒绝修改。");
                Set(selected, "Enabled", false);
                Set(selected, "Profiles", profiles);
                Set(selected, "Enabled", enabled);
            }
            finally { Release(selected); Release(rules); }
        }

        public void AddAllow(FirewallRepairPlan plan)
        {
            var rules = Get(policy, "Rules"); object rule = null;
            try { rule = CreateDetachedAllow(plan); Call(rules, "Add", rule); }
            finally { Release(rule); Release(rules); }
        }

        private static object CreateDetachedAllow(FirewallRepairPlan plan)
        {
            var rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true));
            try
            {
                Set(rule, "Name", plan.RuleName); Set(rule, "Description", "用户确认的 BMC 直连 DHCP 接收许可，仅当前程序、目标网卡及 UDP/67。");
                Set(rule, "ApplicationName", plan.ExecutablePath); Set(rule, "Protocol", 17); Set(rule, "LocalPorts", "67");
                Set(rule, "RemotePorts", "*"); Set(rule, "LocalAddresses", "*"); Set(rule, "RemoteAddresses", "*");
                Set(rule, "Direction", 1); Set(rule, "Profiles", plan.Profile);
                // INetFwRule expects SAFEARRAY(VARIANT), not SAFEARRAY(BSTR).
                // https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ics/c-adding-a-per-interface-rule
                Set(rule, "Interfaces", new object[] { plan.AdapterName });
                Set(rule, "Action", 1); Set(rule, "Enabled", true);
                return rule;
            }
            catch { Release(rule); throw; }
        }

        // Constructs an unregistered COM rule only. Never calls Rules.Add or changes policy.
        internal static void ValidateDetachedAllow(FirewallRepairPlan plan)
        {
            var rule = CreateDetachedAllow(plan);
            try
            {
                var interfaces = (IEnumerable)Get(rule, "Interfaces");
                if (Convert.ToInt32(Get(rule, "Protocol")) != 17 || Convert.ToString(Get(rule, "LocalPorts")) != "67" ||
                    Convert.ToInt32(Get(rule, "Profiles")) != plan.Profile ||
                    string.Join(",", interfaces.Cast<object>().Select(Convert.ToString)) != plan.AdapterName)
                    throw new InvalidOperationException("Unregistered native rule property round-trip failed.");
            }
            finally { Release(rule); }
        }

        public void Remove(string name)
        {
            var rules = Get(policy, "Rules");
            try { Call(rules, "Remove", name); } finally { Release(rules); }
        }
    }
}
