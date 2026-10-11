using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace mirr.Engine;

public sealed record EngineHandle(Process Process, int Port, string Password, string Udid, string DeviceLabel)
{
    public string BasicAuthToken
    {
        get
        {
            string credential = $":{Password}";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(credential));
        }
    }
}

public sealed class ScreenEngine
{
    private readonly EngineCommand _command;
    private Process? _process;
    private readonly StringBuilder _log = new();
    private readonly object _logLock = new();

    public ScreenEngine(EngineCommand command)
    {
        _command = command;
    }

    public event Action<string>? LogAppended;

    public EngineHandle? Current { get; private set; }

    public string LogSnapshot
    {
        get
        {
            lock (_logLock)
            {
                return _log.ToString();
            }
        }
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task<EngineHandle> StartAsync(string udid, string deviceLabel, CancellationToken cancellation)
    {
        await StopAsync();

        int port = FindFreePort();
        string password = RandomToken();

        ProcessStartInfo startInfo = _command.ToStartInfo(new[]
        {
            "developer",
            "core-device",
            "display",
            "serve-web",
            "--http-port", port.ToString(),
            "--bind", "127.0.0.1",
            "--password", password,
        });
        startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        startInfo.EnvironmentVariables["PYMOBILEDEVICE3_UDID"] = udid;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += OnErrorData;
        process.Exited += OnExited;
        process.Start();
        process.BeginErrorReadLine();

        _process = process;
        var handle = new EngineHandle(process, port, password, udid, deviceLabel);
        Current = handle;
        return handle;
    }

    public async Task StopAsync()
    {
        Process? process = _process;
        _process = null;
        Current = null;
        if (process is null)
        {
            return;
        }
        process.ErrorDataReceived -= OnErrorData;
        process.Exited -= OnExited;
        if (!process.HasExited)
        {
            try
            {
                await Task.Run(() => process.Kill(entireProcessTree: true))
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }
        }
        process.Dispose();
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (sender is Process process)
        {
            AppendLog($"engine exited with code {process.ExitCode}");
        }
    }

    private void OnErrorData(object? sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            AppendLog(e.Data);
        }
    }

    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            if (_log.Length > 64 * 1024)
            {
                _log.Remove(0, _log.Length - 48 * 1024);
            }
            _log.AppendLine(line);
        }
        LogAppended?.Invoke(line);
    }

    private static int FindFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string RandomToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
