namespace PrintBridge.Tests;

public sealed class QueueTests
{
    [Fact]
    public void LegacyProfileKeepsItsPortAndNewProfileUsesShortPort()
    {
        const string json = """
            {"Id":"abcdef12-3456-4789-8abc-def012345678","SourceQueue":"Canon",
             "Queue":"Canon","Driver":"Canon driver","OriginalPort":"WSD-old","LocalPort":19100,"Separate":false}
            """;
        var legacy = System.Text.Json.JsonSerializer.Deserialize<PrintPlan>(json)!;
        Assert.Equal("PrintBridge_abcdef12345647898abcdef012345678", legacy.PortName);

        var spool = new Spooler();
        var plan = PrintPlan.Create(spool.Queues["Canon"], 19100, separate: false, spool.Read());
        Assert.Matches("^PrintBridge_[a-f0-9]{6}$", plan.PortName);
        Assert.Equal(plan.PortName, System.Text.Json.JsonSerializer.Deserialize<PrintPlan>(
            System.Text.Json.JsonSerializer.Serialize(plan))!.PortName);
        var integration = new WindowsPrinterIntegrationService(spool);
        integration.Connect(plan);
        Assert.True(WindowsPrinterIntegrationService.Verify(plan, spool.Read()));
        integration.Restore(plan);
        Assert.Equal(legacy.OriginalPort, spool.Read().Queue("Canon")!.Port);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectAndRestoreAreIdempotent(bool separate)
    {
        var spool = new Spooler();
        var plan = Create(separate);
        var service = new WindowsPrinterIntegrationService(spool);
        service.Connect(plan);
        service.Connect(plan);
        Assert.True(WindowsPrinterIntegrationService.Verify(plan, spool.Read()));
        if (separate)
            Assert.Equal("WSD-old", spool.Read().Queue("Canon")!.Port);
        service.Restore(plan);
        service.Restore(plan);
        Assert.Null(spool.Read().Port(plan.PortName));
        Assert.Equal("WSD-old", spool.Read().Queue("Canon")!.Port);
        if (separate)
            Assert.Null(spool.Read().Queue(plan.Queue));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailureAfterQueueMutationRollsBack(bool separate)
    {
        var spool = new Spooler { FailAfterChange = true };
        var plan = Create(separate);
        var service = new WindowsPrinterIntegrationService(spool);
        Assert.Throws<IOException>(() => service.Connect(plan));
        Assert.Null(spool.Read().Port(plan.PortName));
        Assert.Equal("WSD-old", spool.Read().Queue("Canon")!.Port);
        if (separate)
            Assert.Null(spool.Read().Queue(plan.Queue));
    }

    [Fact]
    public void UserChangedDriverPreventsDestructiveRestore()
    {
        var spool = new Spooler();
        var plan = Create(true);
        var service = new WindowsPrinterIntegrationService(spool);
        service.Connect(plan);
        spool.Queues[plan.Queue] = spool.Queues[plan.Queue] with { Driver = "Other" };
        Assert.Throws<InvalidOperationException>(() => service.Restore(plan));
        Assert.NotNull(spool.Read().Port(plan.PortName));
        Assert.NotNull(spool.Read().Queue(plan.Queue));
    }

    [Fact]
    public void AnUnrelatedQueuePreventsPortDeletion()
    {
        var spool = new Spooler();
        var plan = Create(false);
        var service = new WindowsPrinterIntegrationService(spool);
        service.Connect(plan);
        spool.Queues["Other"] = spool.Queues["Canon"] with { Name = "Other", Port = "LPT1:," + plan.PortName };
        Assert.Throws<InvalidOperationException>(() => service.Restore(plan));
        Assert.Equal("WSD-old", spool.Read().Queue("Canon")!.Port);
        Assert.NotNull(spool.Read().Port(plan.PortName));
    }

    [Fact]
    public void ActiveJobsPreventDisconnect()
    {
        var spool = new Spooler();
        var plan = Create(false);
        var service = new WindowsPrinterIntegrationService(spool);
        service.Connect(plan);
        spool.Jobs = true;
        Assert.Throws<InvalidOperationException>(() => service.Restore(plan));
        Assert.True(WindowsPrinterIntegrationService.Verify(plan, spool.Read()));
    }

    private static PrintPlan Create(bool separate) =>
        new(Guid.NewGuid(), "Canon", separate ? "Canon (PrintBridge)" : "Canon", "Canon driver", "WSD-old", 19100, separate);

    private sealed class Spooler : ISpooler
    {
        public bool FailAfterChange;
        public bool Jobs;
        public Dictionary<string, PrinterQueue> Queues { get; } = new()
        {
            ["Canon"] = new("Canon", "Canon driver", "WSD-old", null, null, "Normal", true, false, null, null)
        };
        private readonly Dictionary<string, PrintPort> ports = new()
        {
            ["WSD-old"] = new("WSD-old", null, 0, 0, null, "WSD")
        };
        public SpoolerState Read() => new(Queues.Values.ToArray(), ports.Values.ToArray());
        public void AddPort(PrintPlan plan) => ports.Add(plan.PortName, new(plan.PortName, "127.0.0.1", (uint)plan.LocalPort, 1, false, null));
        public void DeletePort(string name) => ports.Remove(name);
        public void AddQueue(PrintPlan plan)
        {
            Queues.Add(plan.Queue, Queues[plan.SourceQueue] with { Name = plan.Queue, Port = plan.PortName, Comment = plan.Marker });
            MaybeFail();
        }
        public void DeleteQueue(string name) => Queues.Remove(name);
        public void SetPort(string queue, string port)
        {
            Queues[queue] = Queues[queue] with { Port = port };
            MaybeFail();
        }
        public bool HasJobs(string queue) => Jobs;
        private void MaybeFail()
        {
            if (FailAfterChange)
            {
                FailAfterChange = false;
                throw new IOException("Simulated spooler error after mutation");
            }
        }
    }
}
