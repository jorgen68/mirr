using System.Diagnostics;
using System.Text.Json;

namespace mirr.Engine;

public sealed record UsbDevice(string Udid, string Label);

public sealed class DeviceMonitor
{
    private readonly EngineCommand _command;
    private readonly Dictionary<string, string> _labels = [];

    private string? _lastSeenUdid;
    private DateTime _lastChangeTime = DateTime.MinValue;
    private const int DebounceMs = 3000;

    public DeviceMonitor(EngineCommand command)
    {
        _command = command;
    }

    public async Task<IReadOnlyList<UsbDevice>> ListUsbDevices(CancellationToken cancellation)
    {
        List<UsbDevice> labeled = await ListLabeled(cancellation);
        if (labeled.Count > 0)
        {
            return ApplyDebounce(labeled);
        }
        var simple = await ListSimple(cancellation);
        return ApplyDebounce(simple);
    }

    private List<UsbDevice> ApplyDebounce(List<UsbDevice> devices)
    {
        var currentUdid = devices.Count > 0 ? devices[0].Udid : null;
        var now = DateTime.UtcNow;

        if (currentUdid == _lastSeenUdid)
        {
            _lastChangeTime = now;
            return devices;
        }

        if (now - _lastChangeTime < TimeSpan.FromMilliseconds(DebounceMs))
        {
            // Return the previous state to debounce rapid changes
            if (_lastSeenUdid != null)
            {
                var label = _labels.GetValueOrDefault(_lastSeenUdid, "iPhone");
                return [new UsbDevice(_lastSeenUdid, label)];
            }
            return [];
        }

        _lastSeenUdid = currentUdid;
        _lastChangeTime = now;
        return devices;
    }

    private async Task<List<UsbDevice>> ListLabeled(CancellationToken cancellation)
    {
        try
        {
            string output = await DeviceMonitor.RunCapture(_command, ["usbmux", "list", "--usb"], cancellation);
            if (string.IsNullOrWhiteSpace(output))
            {
                return [];
            }
            using JsonDocument document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var devices = new List<UsbDevice>();
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                if (!entry.TryGetProperty("UniqueDeviceID", out JsonElement id) || id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string udid = id.GetString() ?? string.Empty;
                if (udid.Length == 0)
                {
                    continue;
                }
                string label = "iPhone";
                if (entry.TryGetProperty("DeviceName", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                {
                    label = name.GetString() ?? label;
                }
                devices.Add(new UsbDevice(udid, CacheLabel(udid, label)));
            }
            return devices;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<List<UsbDevice>> ListSimple(CancellationToken cancellation)
    {
        try
        {
            string output = await DeviceMonitor.RunCapture(_command, ["usbmux", "list", "--usb", "--simple"], cancellation);
            if (string.IsNullOrWhiteSpace(output))
            {
                return [];
            }
            using JsonDocument document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var devices = new List<UsbDevice>();
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                string udid = entry.GetString() ?? string.Empty;
                if (udid.Length == 0)
                {
                    continue;
                }
                devices.Add(new UsbDevice(udid, _labels.GetValueOrDefault(udid, "iPhone")));
            }
            return devices;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private string CacheLabel(string udid, string label)
    {
        _labels[udid] = label;
        return label;
    }

    public static async Task<string> RunCapture(
        EngineCommand command,
        string[] arguments,
        CancellationToken cancellation)
    {
        using Process process = new();
        process.StartInfo = command.ToStartInfo(arguments);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        string text = await output;
        _ = await error;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        return text;
    }
}
