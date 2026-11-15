namespace PrintBridge.IntegrationTests;

public sealed class IppDiscoveryTests
{
    private static readonly Uri PrinterUri = new("ipp://192.0.2.8:631/ipp/print");

    [Fact]
    public void SuccessfulPrinterAttributesIdentifyTheProbedResource()
    {
        byte[] response = [2, 0, 0, 0, 0, 0, 0, 1, 1, 4, 3];

        var printer = IppDiscovery.Parse(response, PrinterUri);

        Assert.NotNull(printer);
        Assert.Equal("/ipp/print", printer.Resource);
        Assert.Equal(PrinterUri.Host, printer.Address.ToString());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    public void ResponsesWithoutSuccessfulPrinterAttributesAreRejected(byte statusClass, byte group)
    {
        byte[] response = [2, 0, statusClass, 0, 0, 0, 0, 1, 1, group, 3];

        Assert.Null(IppDiscovery.Parse(response, PrinterUri));
    }
}
