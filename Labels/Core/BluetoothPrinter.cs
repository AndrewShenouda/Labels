using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;

namespace Labels.Core;

public enum PrinterState { Unknown, Connecting, Ready, Printing, PaperOut, CoverOpen, Paused, Error, Offline, NotPaired, LabelEdgeNotFound }

/// <summary>A paired Bluetooth device. Address is 12 hex digits, e.g. 001A2B3C4D5E.</summary>
public sealed record PairedDevice(string Address, string Name)
{
    public string Display => $"{Name}  ·  {FormatAddress(Address)}";
    public string ShortName
    {
        get
        {
            // "AE240-bt_4D5E" -> "AE240-BT"
            int us = Name.IndexOf('_');
            return (us > 0 ? Name[..us] : Name).ToUpperInvariant();
        }
    }
    static string FormatAddress(string a) => string.Join(":", Enumerable.Range(0, 6).Select(i => a.Substring(i * 2, 2)));
}

/// <summary>Winsock endpoint for SOCKADDR_BTH (address family 32, packed 30 bytes).</summary>
sealed class BluetoothEndPoint(ulong address, Guid service) : EndPoint
{
    const AddressFamily AF_BTH = (AddressFamily)32;
    public override AddressFamily AddressFamily => AF_BTH;

    public override SocketAddress Serialize()
    {
        var sa = new SocketAddress(AF_BTH, 30);
        for (int i = 0; i < 8; i++) sa[2 + i] = (byte)(address >> (8 * i));
        var g = service.ToByteArray();
        for (int i = 0; i < 16; i++) sa[10 + i] = g[i];
        // port (4 bytes) stays 0: Windows looks the channel up via SDP using the service id.
        return sa;
    }

    public override EndPoint Create(SocketAddress socketAddress) => this;
}

/// <summary>
/// Talks to the printer over a direct Bluetooth RFCOMM (Serial Port Profile) connection.
/// No COM port is involved, so missing or renumbered virtual ports and the Windows spooler can't get in the way.
/// </summary>
public sealed class BluetoothPrinter : IPrinterTransport
{
    static readonly Guid SerialPortService = new("00001101-0000-1000-8000-00805F9B34FB");
    readonly SemaphoreSlim _gate = new(1, 1);
    Socket? _socket;
    string? _connectedTo;

    public string? Address { get; set; }

    // ---------- discovery ----------

    /// <summary>Paired Bluetooth devices, likely label printers first.</summary>
    public static List<PairedDevice> PairedDevices()
    {
        var list = new List<PairedDevice>();
        try
        {
            using var bth = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM");
            foreach (var key in bth?.GetSubKeyNames() ?? [])
            {
                if (!key.StartsWith("Dev_", StringComparison.OrdinalIgnoreCase) || key.Length != 16) continue;
                using var dev = bth!.OpenSubKey(key);
                string? name = null;
                foreach (var inst in dev?.GetSubKeyNames() ?? [])
                {
                    using var k = dev!.OpenSubKey(inst);
                    if (k?.GetValue("FriendlyName") is string fn && fn.Length > 0) { name = fn; break; }
                }
                list.Add(new PairedDevice(key[4..].ToUpperInvariant(), name ?? key[4..]));
            }
        }
        catch { }
        return list
            .OrderByDescending(d => LooksLikePrinter(d.Name))
            .ThenBy(d => d.Name)
            .ToList();
    }

    static bool LooksLikePrinter(string n) =>
        n.Contains("240", StringComparison.OrdinalIgnoreCase) || n.Contains("print", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("label", StringComparison.OrdinalIgnoreCase);

    // ---------- connection ----------

    async Task<Socket> EnsureConnectedAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Address)) throw new InvalidOperationException("Choose your printer in Settings.");
        if (_socket is { Connected: true } && _connectedTo == Address) return _socket;
        Close();

        var s = new Socket((AddressFamily)32, SocketType.Stream, (ProtocolType)3 /* BTHPROTO_RFCOMM */);
        var ep = new BluetoothEndPoint(Convert.ToUInt64(Address, 16), SerialPortService);
        // Bluetooth sockets don't support ConnectEx, so connect synchronously on a worker thread.
        var connect = Task.Run(() => s.Connect(ep));
        var winner = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(12), ct)).ConfigureAwait(false);
        try
        {
            if (winner != connect)
            {
                s.Dispose(); // aborts the pending connect
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
            await connect.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            s.Dispose();
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw new IOException(Explain(ex), ex);
        }
        _socket = s;
        _connectedTo = Address;
        return s;
    }

    static string Explain(Exception ex)
    {
        if (ex is OperationCanceledException or TimeoutException) return "The printer didn't answer. Make sure it's on and nearby.";
        if (ex is SocketException se)
        {
            return se.ErrorCode switch
            {
                10060 or 10064 or 10065 or 10051 => "The printer didn't answer. Make sure it's on and nearby.",
                10061 or 10048 => "The printer is busy — it may be connected to your phone.",
                10050 => "Bluetooth is turned off on this PC.",
                _ => $"Couldn't connect to the printer ({se.SocketErrorCode}).",
            };
        }
        return "Couldn't connect to the printer. " + ex.Message;
    }

    void Close()
    {
        try { _socket?.Dispose(); } catch { }
        _socket = null; _connectedTo = null;
    }

    static async Task SendAllAsync(Socket s, ReadOnlyMemory<byte> data, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (data.Length > 0)
            {
                int n = await s.SendAsync(data, SocketFlags.None, cts.Token).ConfigureAwait(false);
                data = data[n..];
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The printer stopped responding.");
        }
    }

    // ---------- operations ----------

    /// <summary>Asks the printer for its status byte (TSPL "ESC ! ?").</summary>
    public async Task<PrinterState> QueryStatusAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Address)) return PrinterState.NotPaired;
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return PrinterState.Printing;
        try
        {
            var s = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            while (s.Available > 0) { var junk = new byte[s.Available]; s.Receive(junk); }
            await SendAllAsync(s, new byte[] { 0x1B, 0x21, 0x3F }, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var buf = new byte[16];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2500);
            int n;
            try { n = await s.ReceiveAsync(buf, SocketFlags.None, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { n = -1; }
            if (n == 0) { Close(); return PrinterState.Offline; } // remote closed
            if (n < 0) return PrinterState.Ready;                 // connected; firmware doesn't answer status queries
            LastStatusByte = buf[n - 1];
            return Decode(buf[n - 1]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Close();
            return PrinterState.Offline;
        }
        finally { _gate.Release(); }
    }

    public string? LastError { get; private set; }
    public byte? LastStatusByte { get; private set; }

    static PrinterState Decode(byte b)
    {
        if (b == 0x00) return PrinterState.Ready;
        if ((b & 0x01) != 0 || (b & 0x40) != 0) return PrinterState.CoverOpen;
        if ((b & 0x04) != 0) return PrinterState.PaperOut;
        // "Paper jam" in TSPL terms: the sensor fed a whole label length without finding a gap.
        if ((b & 0x02) != 0) return PrinterState.LabelEdgeNotFound;
        if ((b & 0x10) != 0) return PrinterState.Paused;
        if ((b & 0x20) != 0) return PrinterState.Printing;
        return PrinterState.Error;
    }

    /// <summary>Sends a job in chunks, reporting progress from 0 to 1.</summary>
    public async Task SendAsync(byte[] job, IProgress<double>? progress, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Socket s;
            try { s = await EnsureConnectedAsync(ct).ConfigureAwait(false); }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                // One retry: the first connect after the printer wakes up sometimes fails.
                await Task.Delay(800, ct).ConfigureAwait(false);
                s = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            }
            const int chunk = 4096;
            for (int off = 0; off < job.Length; off += chunk)
            {
                int len = Math.Min(chunk, job.Length - off);
                await SendAllAsync(s, job.AsMemory(off, len), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                progress?.Report((double)(off + len) / job.Length);
            }
        }
        catch
        {
            Close();
            throw;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => Close();
}
