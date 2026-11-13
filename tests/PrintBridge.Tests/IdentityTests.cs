using System.Net;

namespace PrintBridge.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void AutomaticAdapterSelectionPrefersThePrinterSubnetAndTheLastWorkingAdapter()
    {
        var printer = IPAddress.Parse("192.0.2.42");
        var wifi = new LanAdapter(Guid.NewGuid(), "Wi-Fi", "Wireless", 1, IPAddress.Parse("192.0.2.10"), 24, null, null, 1);
        var ethernet = wifi with { Id = Guid.NewGuid(), Name = "Ethernet", Index = 2, IsWireless = false };
        var other = ethernet with { Id = Guid.NewGuid(), Index = 3, Address = IPAddress.Parse("198.51.100.10") };

        Assert.Equal(new[] { ethernet, wifi, other }, LanAdapter.OrderForPrinter([other, wifi, ethernet], null, ethernet.Id, printer));
        Assert.Equal(new[] { wifi, ethernet, other }, LanAdapter.OrderForPrinter([other, wifi, ethernet], null, other.Id, printer));
        Assert.Equal(new[] { other }, LanAdapter.OrderForPrinter([wifi, ethernet, other], other.Id, wifi.Id, printer));
        Assert.Empty(LanAdapter.OrderForPrinter([wifi, ethernet], other.Id, null, printer));
    }

    private readonly PrinterIdentityService service = new();
    private static NetworkPrinter Canon(string address, string? mac = "00AABB112233", string? serial = null, bool raw = true) =>
        new(IPAddress.Parse(address), "canon.local", "Canon", mac, serial, null, raw, true, false, "fixture");

    [Fact]
    public void ChangedAddressAndHostnamePreserveConfirmedMac()
    {
        var original = Canon("192.168.8.2");
        var moved = original with { Address = IPAddress.Parse("192.168.8.50"), Host = "new.local" };
        Assert.Equal(moved, service.Resolve(PrinterIdentity.Confirm(original),
            [Canon("192.168.8.2", "123456123456"), moved]));
    }

    [Fact]
    public void SameAddressAndModelAreInsufficient()
    {
        var original = Canon("192.168.8.2");
        Assert.Null(service.Resolve(PrinterIdentity.Confirm(original), [original with { Mac = null }]));
    }

    [Fact]
    public void SerialWorksWithoutMacButDuplicateOrContradictoryEvidenceFailsClosed()
    {
        var original = Canon("192.168.8.2", null, "CN1200");
        var moved = original with { Address = IPAddress.Parse("192.168.8.50") };
        Assert.Equal(moved, service.Resolve(PrinterIdentity.Confirm(original), [moved]));
        Assert.Null(service.Resolve(PrinterIdentity.Confirm(original), [original, moved]));
        Assert.Null(service.Resolve(PrinterIdentity.Confirm(original), [moved with { Raw = false }]));
        Assert.False(service.Matches(PrinterIdentity.Confirm(original with { Mac = "00AABB112233" }),
            moved with { Mac = "123456123456" }));
    }

    [Theory]
    [InlineData("10.40.7.17", 27, "10.40.7.29", true, 29)]
    [InlineData("10.40.7.17", 27, "10.40.7.39", false, 29)]
    [InlineData("10.40.7.17", 16, "10.40.19.45", true, 253)]
    [InlineData("10.40.7.16", 31, "10.40.7.17", true, 1)]
    public void ScanRespectsPrefixAndLimitsLargeNetworks(string local, int prefix, string peer, bool contains, int count)
    {
        var adapter = new LanAdapter(Guid.NewGuid(), "renamed", "Wi-Fi", 7, IPAddress.Parse(local), prefix, null, null, 1);
        Assert.Equal(contains, adapter.Contains(IPAddress.Parse(peer)));
        var addresses = adapter.ScanAddresses(254).ToArray();
        Assert.Equal(count, addresses.Length);
        Assert.All(addresses, value => Assert.True(adapter.Contains(value)));
        Assert.DoesNotContain(adapter.Address, addresses);
        Assert.Single(adapter.ScanAddresses(1));
    }
}
