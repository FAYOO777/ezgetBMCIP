using System;
using System.Linq;
using System.Net;
using EzGetBmcIp;

internal static class AdapterIdentityResolverTests
{
    public static void RunAll()
    {
        NormalizedGuidMatchIgnoresBraces();
        MacFallbackHandlesStaleGuid();
        UniqueNameAndDescriptionIsLastResort();
        NameOnlyAndAmbiguousMatchesAreRejected();
        SourceAddressIsRequired();
    }

    private static void NormalizedGuidMatchIgnoresBraces()
    {
        var expected = Adapter("Ethernet", "Intel I350", "{ABC-123}", "001122334455");
        var candidate = Candidate("ABC-123", "Ethernet", "Intel I350", "001122334455");
        var selected = Select(expected, candidate, out var kind);
        Assert(ReferenceEquals(selected, candidate) && kind == AdapterIdentityMatchKind.NormalizedGuid,
            "Normalized GUID matching did not accept brace differences.");
    }

    private static void MacFallbackHandlesStaleGuid()
    {
        var expected = Adapter("Ethernet", "Intel I350", "old-id", "00-11-22-33-44-55");
        var candidate = Candidate("new-id", "Ethernet 2", "Intel I350", "001122334455");
        var selected = Select(expected, candidate, out var kind);
        Assert(ReferenceEquals(selected, candidate) && kind == AdapterIdentityMatchKind.Mac,
            "MAC fallback did not recover from a stale adapter GUID.");
    }

    private static void UniqueNameAndDescriptionIsLastResort()
    {
        var expected = Adapter("Ethernet", "Intel I350", "old-id", "");
        var candidate = Candidate("new-id", "Ethernet", "Intel I350", "");
        var selected = Select(expected, candidate, out var kind);
        Assert(ReferenceEquals(selected, candidate) && kind == AdapterIdentityMatchKind.UniqueNameAndDescription,
            "Unique name+description fallback was not selected.");
    }

    private static void NameOnlyAndAmbiguousMatchesAreRejected()
    {
        var expected = Adapter("Ethernet", "Intel I350", "missing", "");
        var nameOnly = Candidate("other", "Ethernet", "Different", "");
        var selected = Select(expected, nameOnly, out _);
        Assert(selected == null, "Name-only matching must be rejected.");

        var first = Candidate("one", "Ethernet", "Intel I350", "");
        var second = Candidate("two", "Ethernet", "Intel I350", "");
        selected = Select(expected, first, second, out _);
        Assert(selected == null, "Ambiguous name+description matching must fail safe.");
    }

    private static void SourceAddressIsRequired()
    {
        var expected = Adapter("Ethernet", "Intel I350", "id", "001122334455");
        var candidate = Candidate("id", "Ethernet", "Intel I350", "001122334455");
        candidate.HasSourceAddress = false;
        var selected = Select(expected, candidate, out _);
        Assert(selected == null, "An interface without the temporary source address must be rejected.");
    }

    private static AdapterIdentityCandidate Select(
        WiredAdapter expected,
        AdapterIdentityCandidate candidate,
        out AdapterIdentityMatchKind kind)
    {
        return Select(expected, new[] { candidate }, out kind);
    }

    private static AdapterIdentityCandidate Select(
        WiredAdapter expected,
        AdapterIdentityCandidate first,
        AdapterIdentityCandidate second,
        out AdapterIdentityMatchKind kind)
    {
        return Select(expected, new[] { first, second }, out kind);
    }

    private static AdapterIdentityCandidate Select(
        WiredAdapter expected,
        AdapterIdentityCandidate[] candidates,
        out AdapterIdentityMatchKind kind)
    {
        string diagnostic;
        return AdapterIdentityResolver.Select(
            expected,
            IPAddress.Parse("10.77.77.1"),
            candidates,
            out kind,
            out diagnostic);
    }

    private static WiredAdapter Adapter(string name, string description, string id, string mac)
        => new WiredAdapter(name, description, id, mac);

    private static AdapterIdentityCandidate Candidate(string id, string name, string description, string mac)
    {
        return new AdapterIdentityCandidate
        {
            Id = id,
            Name = name,
            Description = description,
            MacAddress = mac,
            InterfaceIndex = 5,
            HasSourceAddress = true
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
