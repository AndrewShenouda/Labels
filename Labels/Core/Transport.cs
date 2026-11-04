using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Labels.Core;

/// <summary>A way of getting bytes to the printer.</summary>
public interface IPrinterTransport : IDisposable
{
    Task<PrinterState> QueryStatusAsync(CancellationToken ct = default);
    Task SendAsync(byte[] job, IProgress<double>? progress, CancellationToken ct = default);
    string? LastError { get; }
}

public enum PrinterKind { Bluetooth, WindowsPrinter }

/// <summary>A printer the user can pick: a paired Bluetooth device, or a Windows printer (USB, network…).</summary>
public sealed record PrinterChoice(PrinterKind Kind, string Id, string Name, string Detail)
{
    public string Display => $"{Name}  ·  {Detail}";
    public string ShortName
    {
        get
        {
            int us = Name.IndexOf('_');
            return (us > 0 ? Name[..us] : Name).ToUpperInvariant();
        }
    }
    public string KindText => Kind == PrinterKind.Bluetooth ? "Bluetooth" : Detail.Contains("USB") ? "USB" : "Windows printer";

    public IPrinterTransport CreateTransport() => Kind == PrinterKind.Bluetooth
        ? new BluetoothPrinter { Address = Id }
        : new WindowsPrinter(Id);

    /// <summary>Bluetooth devices and Windows printers, likely label printers first.</summary>
    public static List<PrinterChoice> Discover()
    {
        var list = new List<PrinterChoice>();
        foreach (var d in BluetoothPrinter.PairedDevices())
            list.Add(new PrinterChoice(PrinterKind.Bluetooth, d.Address, d.Name, "Bluetooth"));
        foreach (var q in WindowsPrinter.Queues())
            list.Add(new PrinterChoice(PrinterKind.WindowsPrinter, q.Name, q.Name, q.Port.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ? $"USB ({q.Port.TrimEnd(':')})" : $"Windows printer ({q.Port.TrimEnd(':')})"));
        static int Rank(PrinterChoice c)
        {
            bool label = c.Name.Contains("240") || c.Name.Contains("label", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("thermal", StringComparison.OrdinalIgnoreCase);
            bool virt = c.Name.Contains("PDF", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("OneNote", StringComparison.OrdinalIgnoreCase) ||
                        c.Name.Contains("XPS", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("Fax", StringComparison.OrdinalIgnoreCase);
            if (virt) return 9;
            if (label && c.Detail.StartsWith("USB")) return 0;
            if (label && c.Kind == PrinterKind.Bluetooth) return 1;
            if (label) return 2;
            return c.Kind == PrinterKind.Bluetooth ? 5 : 6;
        }
        return list.OrderBy(Rank).ThenBy(c => c.Name).ToList();
    }
}

/// <summary>
/// Sends jobs through a Windows printer queue as RAW data, so the printer's own driver (and whatever port it
/// uses: USB, network, serial) delivers our commands untouched.
/// </summary>
public sealed class WindowsPrinter(string queueName) : IPrinterTransport
{
    public string QueueName { get; } = queueName;
    public string? LastError { get; private set; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DOC_INFO_1 { public string pDocName; public string? pOutputFile; public string pDatatype; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PRINTER_INFO_2
    {
        public string pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation;
        public IntPtr pDevMode;
        public string pSepFile, pPrintProcessor, pDatatype, pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool OpenPrinter(string name, out IntPtr h, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool ClosePrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] static extern int StartDocPrinter(IntPtr h, int level, ref DOC_INFO_1 di);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool EndDocPrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool StartPagePrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool EndPagePrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool WritePrinter(IntPtr h, byte[] buf, int count, out int written);
    [DllImport("winspool.drv", SetLastError = true)] static extern bool AbortPrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool GetPrinter(IntPtr h, int level, IntPtr buf, int size, out int needed);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool EnumPrinters(int flags, string? name, int level, IntPtr buf, int size, out int needed, out int returned);

    /// <summary>Local and connected Windows printers with their port names.</summary>
    public static List<(string Name, string Port)> Queues()
    {
        var result = new List<(string, string)>();
        const int LOCAL = 2, CONNECTIONS = 4;
        EnumPrinters(LOCAL | CONNECTIONS, null, 2, IntPtr.Zero, 0, out int needed, out _);
        if (needed <= 0) return result;
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(LOCAL | CONNECTIONS, null, 2, buf, needed, out _, out int count)) return result;
            int size = Marshal.SizeOf<PRINTER_INFO_2>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buf + i * size);
                result.Add((info.pPrinterName, info.pPortName ?? ""));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    public Task<PrinterState> QueryStatusAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        if (!OpenPrinter(QueueName, out var h, IntPtr.Zero)) { LastError = $"Windows printer \"{QueueName}\" wasn't found."; return PrinterState.Offline; }
        try
        {
            GetPrinter(h, 2, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return PrinterState.Unknown;
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinter(h, 2, buf, needed, out _)) return PrinterState.Unknown;
                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buf);
                const uint PAUSED = 0x1, ERROR = 0x2, PAPER_JAM = 0x8, PAPER_OUT = 0x10, OFFLINE = 0x80, PRINTING = 0x400,
                           NOT_AVAILABLE = 0x1000, DOOR_OPEN = 0x400000, ATTR_WORK_OFFLINE = 0x400;
                uint s = info.Status;
                LastError = null;
                if ((s & (OFFLINE | NOT_AVAILABLE)) != 0 || (info.Attributes & ATTR_WORK_OFFLINE) != 0)
                {
                    LastError = "Windows reports this printer as offline — is it plugged in and switched on?";
                    return PrinterState.Offline;
                }
                if ((s & DOOR_OPEN) != 0) return PrinterState.CoverOpen;
                if ((s & PAPER_OUT) != 0) return PrinterState.PaperOut;
                if ((s & PAPER_JAM) != 0) return PrinterState.LabelEdgeNotFound;
                if ((s & PAUSED) != 0) return PrinterState.Paused;
                if ((s & ERROR) != 0) return PrinterState.Error;
                if ((s & PRINTING) != 0 || info.cJobs > 0) return PrinterState.Printing;
                return PrinterState.Ready;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { ClosePrinter(h); }
    }, ct);

    public Task SendAsync(byte[] job, IProgress<double>? progress, CancellationToken ct = default) => Task.Run(() =>
    {
        if (!OpenPrinter(QueueName, out var h, IntPtr.Zero))
            throw new IOException($"Windows printer \"{QueueName}\" wasn't found.");
        try
        {
            var di = new DOC_INFO_1 { pDocName = "Labels", pDatatype = "RAW" };
            if (StartDocPrinter(h, 1, ref di) == 0)
                throw new IOException("Windows wouldn't start the print job. " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            bool ok = false;
            try
            {
                StartPagePrinter(h);
                const int chunk = 16384;
                var buf = new byte[chunk];
                for (int off = 0; off < job.Length; off += chunk)
                {
                    if (ct.IsCancellationRequested) { AbortPrinter(h); ct.ThrowIfCancellationRequested(); }
                    int len = Math.Min(chunk, job.Length - off);
                    Buffer.BlockCopy(job, off, buf, 0, len);
                    if (!WritePrinter(h, buf, len, out int written) || written != len)
                        throw new IOException("Sending to the printer failed. " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    progress?.Report((double)(off + len) / job.Length);
                }
                EndPagePrinter(h);
                ok = true;
            }
            finally { if (ok) EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }, ct);

    public void Dispose() { }
}
