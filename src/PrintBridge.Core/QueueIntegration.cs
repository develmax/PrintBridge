using System;
using System.Collections.Generic;
using System.Linq;

namespace PrintBridge;

public sealed record PrintPort(string Name, string? Host, uint Number, uint Protocol, bool? SnmpEnabled, string? Monitor)
{
    public bool Matches(PrintPlan plan)
    {
        if (Name.Equals(plan.PortName, StringComparison.OrdinalIgnoreCase) && Host == "127.0.0.1" && Number == plan.LocalPort && Protocol == 1)
        {
            return SnmpEnabled == false;
        }
        return false;
    }
}

public sealed record SpoolerState(IReadOnlyList<PrinterQueue> Queues, IReadOnlyList<PrintPort> Ports)
{
    public PrinterQueue? Queue(string name)
    {
        return Queues.FirstOrDefault((PrinterQueue queue) => Same(queue.Name, name));
    }

    public PrintPort? Port(string name)
    {
        return Ports.FirstOrDefault((PrintPort port) => Same(port.Name, name));
    }

    public static bool Same(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Uses(PrinterQueue queue, string port)
    {
        return queue.Port.Split(',', StringSplitOptions.TrimEntries).Any((string value) => Same(value, port));
    }
}

public interface ISpooler
{
    SpoolerState Read();

    void AddPort(PrintPlan plan);

    void DeletePort(string name);

    void AddQueue(PrintPlan plan);

    void DeleteQueue(string name);

    void SetPort(string queue, string port);

    bool HasJobs(string queue);
}

public sealed class WindowsPrinterIntegrationService(ISpooler spooler)
{
    public static bool Eligible(PrinterQueue queue)
    {
        if (queue.Local && queue.Port.Length > 0 && !queue.Port.Contains(',') && queue.RawPort != false)
        {
            return !new string[7] { "IPP Class", "Internet Printing", "Mopria", "XPS Document Writer", "Print to PDF", "Fax", "OneNote" }.Any((string value) => queue.Driver.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    public static bool Verify(PrintPlan plan, SpoolerState state)
    {
        PrintPort? printPort = state.Port(plan.PortName);
        if (printPort != null && printPort.Matches(plan))
        {
            PrinterQueue? queue = state.Queue(plan.Queue);
            if (queue != null)
            {
                return QueueMatches(plan, queue);
            }
        }
        return false;
    }

    private static bool QueueMatches(PrintPlan plan, PrinterQueue queue)
    {
        if (queue.Local && SpoolerState.Same(queue.Port, plan.PortName) && SpoolerState.Same(queue.Driver, plan.Driver))
        {
            if (plan.Separate)
            {
                if (!queue.Shared)
                {
                    return queue.Comment == plan.Marker;
                }
                return false;
            }
            return true;
        }
        return false;
    }

    public void Connect(PrintPlan plan)
    {
        Validate(plan);
        SpoolerState before = spooler.Read();
        if (Verify(plan, before))
        {
            return;
        }
        if (before.Port(plan.PortName) != null || (plan.Separate && before.Queue(plan.Queue) != null))
        {
            throw new InvalidOperationException(T("The queue or port name is already in use. Restore the existing configuration before trying again."));
        }
        PrinterQueue? source = before.Queue(plan.SourceQueue);
        if (source == null || !Eligible(source) || !SpoolerState.Same(source.Port, plan.OriginalPort) || !SpoolerState.Same(source.Driver, plan.Driver))
        {
            throw new InvalidOperationException(T("The original queue changed or its driver is incompatible with RAW printing."));
        }
        if (!plan.Separate && (source.Shared || spooler.HasJobs(source.Name)))
        {
            throw new InvalidOperationException(T("The original queue is shared or has pending jobs. Use a separate queue or wait for printing to finish."));
        }
        try
        {
            spooler.AddPort(plan);
            PrintPort? printPort = spooler.Read().Port(plan.PortName);
            if (printPort == null || !printPort.Matches(plan))
            {
                throw new InvalidOperationException(T("Windows did not confirm a RAW port with SNMP disabled."));
            }
            if (plan.Separate)
            {
                spooler.AddQueue(plan);
            }
            else
            {
                spooler.SetPort(plan.Queue, plan.PortName);
            }
            if (!Verify(plan, spooler.Read()))
            {
                throw new InvalidOperationException(T("Windows did not confirm the queue's bridge port."));
            }
        }
        catch (Exception ex)
        {
            try
            {
                Restore(plan);
            }
            catch (Exception ex2)
            {
                throw new InvalidOperationException(ex.Message + $" {T("Restoration needs attention.")} " + ex2.Message, ex);
            }
            throw;
        }
    }

    public void Restore(PrintPlan plan)
    {
        Validate(plan);
        SpoolerState state = spooler.Read();
        PrinterQueue? queue = state.Queue(plan.Queue);
        PrintPort? port = state.Port(plan.PortName);
        if (port != null && !port.Matches(plan))
        {
            throw new InvalidOperationException(T("The bridge port settings changed. The operation was stopped."));
        }
        if (queue != null && SpoolerState.Uses(queue, plan.PortName))
        {
            if (!QueueMatches(plan, queue) || queue.Shared)
            {
                throw new InvalidOperationException(T("The queue changed after setup. The operation was stopped."));
            }
            if (spooler.HasJobs(queue.Name))
            {
                throw new InvalidOperationException(T("Wait for the queued jobs to finish or cancel them before trying again."));
            }
            if (plan.Separate)
            {
                spooler.DeleteQueue(queue.Name);
            }
            else
            {
                if (state.Port(plan.OriginalPort) == null)
                {
                    throw new InvalidOperationException(T("The original port is missing. The queue was left unchanged."));
                }
                spooler.SetPort(queue.Name, plan.OriginalPort);
                if (!SpoolerState.Same(spooler.Read().Queue(queue.Name)?.Port, plan.OriginalPort))
                {
                    throw new InvalidOperationException(T("Windows did not confirm restoration of the original port."));
                }
            }
        }
        state = spooler.Read();
        if (state.Queues.Any((PrinterQueue item) => SpoolerState.Uses(item, plan.PortName)))
        {
            throw new InvalidOperationException(T("A queue still uses the bridge port, so the port was kept."));
        }
        PrintPort? printPort = state.Port(plan.PortName);
        if (printPort != null && printPort.Matches(plan))
        {
            spooler.DeletePort(plan.PortName);
        }
        if (spooler.Read().Port(plan.PortName) != null)
        {
            throw new InvalidOperationException(T("Windows did not confirm removal of the bridge port."));
        }
    }

    private static void Validate(PrintPlan plan)
    {
        bool flag = plan.Id == Guid.Empty;
        if (!flag)
        {
            int localPort = plan.LocalPort;
            bool flag2 = ((localPort < 1024 || localPort > 65535) ? true : false);
            flag = flag2;
        }
        if (flag || new string[4] { plan.SourceQueue, plan.Queue, plan.Driver, plan.OriginalPort }.Any((string value) => string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) || (plan.Separate ? SpoolerState.Same(plan.SourceQueue, plan.Queue) : (!SpoolerState.Same(plan.SourceQueue, plan.Queue))) || SpoolerState.Same(plan.OriginalPort, plan.PortName))
        {
            throw new InvalidOperationException(T("Invalid queue operation parameters."));
        }
    }
}
