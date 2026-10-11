using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace mirr.Engine;

public sealed partial class DevicePreparer
{
    private readonly EngineCommand _command;
    private readonly ConcurrentDictionary<string, byte> _prepared = new();

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiPattern();

    public event Action<string>? LogLine;

    public DevicePreparer(EngineCommand command)
    {
        _command = command;
    }

    public void Invalidate(string udid)
    {
        _prepared.TryRemove(udid, out _);
    }

    public async Task<(bool Ok, string? Message)> PrepareAsync(string udid, CancellationToken cancellation)
    {
        if (_prepared.ContainsKey(udid))
        {
            return (true, null);
        }

        try
        {
            ProcessStartInfo startInfo = _command.ToStartInfo(["mounter", "auto-mount"]);
            startInfo.RedirectStandardOutput = true;
            startInfo.EnvironmentVariables["PYMOBILEDEVICE3_UDID"] = udid;
            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

            using Process process = new() { StartInfo = startInfo };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            string stdout = await output;
            string stderr = await error;
            string combined = stderr + stdout;
            LogLine?.Invoke($"mount: {LastLine(combined)}");

            bool mounted = process.ExitCode == 0
                || combined.Contains("already", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("mounted successfully", StringComparison.OrdinalIgnoreCase);
            if (mounted)
            {
                _prepared[udid] = 0;
                return (true, null);
            }
            return (false, LastLine(combined) ?? "Could not mount the Developer Disk Image");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private static string? LastLine(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = AnsiPattern().Replace(lines[i], string.Empty).Trim();
            if (line.Length == 0)
            {
                continue;
            }
            return line.Length > 140 ? line[..140] + "…" : line;
        }
        return null;
    }
}
