
namespace PrintBridge.IntegrationTests;

public sealed class WindowsSpoolerTests
{
    [SpoolerFact]
    [Trait("Category", "WindowsSpooler")]
    public void TemporaryQueuesRoundTrip()
    {
        var spooler = new WindowsPrinterService();
        var before = spooler.Read();
        string driver = before.Queues.First(queue => queue.RawPort == true && WindowsPrinterIntegrationService.Eligible(queue)).Driver;
        string original = $"PrintBridge_Test_{Guid.NewGuid():N}";
        var source = new PrintPlan(Guid.NewGuid(), original, original, driver, "PORTPROMPT:", 19100, false);
        // Seed a temporary source queue using its own loopback port. No jobs are submitted.
        try
        {
            spooler.AddPort(source);
            spooler.AddQueue(source);
            var service = new WindowsPrinterIntegrationService(spooler);
            foreach (bool separate in new[] { true, false })
            {
                var plan = new PrintPlan(Guid.NewGuid(), original, separate ? original + " copy" : original,
                    driver, source.PortName, 19101, separate);
                try
                {
                    service.Connect(plan);
                    Assert.True(WindowsPrinterIntegrationService.Verify(plan, spooler.Read()));
                }
                finally { service.Restore(plan); }
                Assert.Null(spooler.Read().Port(plan.PortName));
                Assert.Equal(source.PortName, spooler.Read().Queue(original)?.Port);
                if (separate)
                    Assert.Null(spooler.Read().Queue(plan.Queue));
            }
        }
        finally
        {
            if (spooler.Read().Queue(original)?.Comment == source.Marker)
                spooler.DeleteQueue(original);
            if (spooler.Read().Port(source.PortName)?.Matches(source) == true)
                spooler.DeletePort(source.PortName);
        }
    }
}

internal sealed class SpoolerFactAttribute : FactAttribute
{
    public SpoolerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PRINTBRIDGE_TEST_SPOOLER") != "1")
            Skip = "Requires Windows Spooler and an installed RAW printer driver.";
    }
}
