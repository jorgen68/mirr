using System.Diagnostics;
using System.Text.RegularExpressions;
using mirr.Engine;
using mirr.Media;

namespace mirr.Services;

public sealed partial class MirrorSession : IAsyncDisposable
{
    private static readonly TimeSpan DevicePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartIdleThreshold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TrustRetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinStateChangeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinStreamingDuration = TimeSpan.FromSeconds(10);

    private readonly DecoderPipeline _pipeline = new();
    private readonly object _statusLock = new();
    private readonly Action<string> _diagnosticForwarder;
    private CancellationTokenSource _lifetime = new();
    private Task? _sessionTask;
    private ScreenEngine? _engine;
    private DeviceMonitor? _monitor;
    private DevicePreparer? _preparer;
    private volatile bool _firstFrameSeen;
    private string _lastLabel = string.Empty;
    private DateTime _lastStatusChange = DateTime.MinValue;
    private DateTime _streamingStartedAt = DateTime.MinValue;

    public MirrorSession()
    {
        _diagnosticForwarder = line => LogAppended?.Invoke(line);
    }

    [GeneratedRegex(@"not\s*paired|PairingDialog|pairing\s*(dialog|error)|untrusted", RegexOptions.IgnoreCase)]
    private static partial Regex TrustPattern();

    [GeneratedRegex(@"no\s*device|device\s*not\s*found|no\s*tunnel", RegexOptions.IgnoreCase)]
    private static partial Regex NoDevicePattern();

    [GeneratedRegex(@"developer\s*mode", RegexOptions.IgnoreCase)]
    private static partial Regex DeveloperModePattern();

    [GeneratedRegex(@"coredevice\.displayservice", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayServicePattern();

    public event Action<EngineStatus>? StatusChanged;
    public event Action<string>? LogAppended;

    public DecoderPipeline Pipeline => _pipeline;

    public EngineStatus Current { get; private set; } = EngineStatus.Locating;

    public async Task StartAsync()
    {
        if (_sessionTask is { IsCompleted: false })
        {
            return;
        }
        _lifetime = new CancellationTokenSource();
        _firstFrameSeen = false;
        _pipeline.FrameReady += OnFirstFrame;
        _pipeline.Start();
        _sessionTask = Task.Run(SessionLoop, CancellationToken.None);
        await Task.CompletedTask;
    }

    public async Task RestartAsync()
    {
        await StopCoreAsync();
        await StartAsync();
    }

    public async Task StopAsync()
    {
        await StopCoreAsync();
    }

    private void OnFirstFrame(FramePresentation frame)
    {
        if (_firstFrameSeen)
        {
            return;
        }
        _firstFrameSeen = true;
        SetStatus(new EngineStatus(EngineState.Streaming, "Live", _lastLabel));
    }

    private async Task SessionLoop()
    {
        CancellationToken token = _lifetime.Token;
        try
        {
            SetStatus(EngineStatus.Locating);

            EngineCommand? command = await EngineLocator.Resolve(token);
            if (command is null)
            {
                SetStatus(EngineStatus.Missing);
                return;
            }
            EngineLocator.Diagnostic += _diagnosticForwarder;

            EngineCommand? python = await EngineLocator.ResolvePython(token);
            if (python is null)
            {
                SetStatus(new EngineStatus(EngineState.Failed, "PyAV is required for decoding — run: pip install av"));
                return;
            }
            _pipeline.SetInterpreter(python);

            _engine = new ScreenEngine(command);
            _engine.LogAppended += line => LogAppended?.Invoke(line);
            _monitor = new DeviceMonitor(command);
            _preparer = new DevicePreparer(command);
            _preparer.LogLine += line => LogAppended?.Invoke(line);

            string? activeUdid = null;
            while (!token.IsCancellationRequested)
            {
                UsbDevice? device;
                try
                {
                    device = (await _monitor.ListUsbDevices(token)).FirstOrDefault();
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (device is null)
                {
                    activeUdid = null;
                    _lastLabel = string.Empty;
                    await StopEngineAsync();
                    _pipeline.ClearFrame();
                    // Use Reconnecting for temporary loss, Disconnected for prolonged absence
                    if (Current.State != EngineState.Disconnected || DateTime.UtcNow - _lastStatusChange > TimeSpan.FromSeconds(15))
                    {
                        SetStatus(EngineStatus.Reconnecting);
                    }
                    await WaitAsync(DevicePollInterval, token);
                    continue;
                }

                if (device.Udid != activeUdid)
                {
                    activeUdid = device.Udid;
                    _lastLabel = device.Label;
                    await StopEngineAsync();
                }

                try
                {
                    await RunEngineCycle(device, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogAppended?.Invoke($"engine cycle: {exception.Message}");
                    await StopEngineAsync();
                    SetStatus(new EngineStatus(EngineState.Failed, Shorten(exception.Message), device.Label));
                    await WaitAsync(RetryDelay, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                LogAppended?.Invoke($"session loop: {exception.Message}");
                SetStatus(new EngineStatus(EngineState.Failed, Shorten(exception.Message)));
            }
        }
    }

    private async Task RunEngineCycle(UsbDevice device, CancellationToken token)
    {
        SetStatus(new EngineStatus(EngineState.Preparing, "Mounting the Developer Disk Image", device.Label));
        (bool prepared, string? prepareMessage) = await _preparer!.PrepareAsync(device.Udid, token);
        if (!prepared)
        {
            SetStatus(new EngineStatus(EngineState.Failed, prepareMessage ?? "Could not prepare the device", device.Label));
            await WaitAsync(RetryDelay, token);
            return;
        }

        SetStatus(new EngineStatus(EngineState.Connecting, "Linking with the device", device.Label));
        await StopEngineAsync();

        EngineHandle handle;
        try
        {
            handle = await _engine!.StartAsync(device.Udid, device.Label, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            SetStatus(new EngineStatus(EngineState.Failed, "The screen engine could not start", device.Label));
            await WaitAsync(RetryDelay, token);
            return;
        }

        using var client = new ScreenStreamClient(handle);
        using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        void RequestKeyframe() => _ = client.RequestKeyframeAsync(cycleCts.Token);
        _pipeline.KeyframeNeeded += RequestKeyframe;
        try
        {
            CodecDescription? codec = await client.FetchCodecAsync(cycleCts.Token);
            if (codec is null)
            {
                await ClassifyEngineFailure(device, token);
                return;
            }
            _pipeline.SetCodec(codec);

            long lastAccessUnit = Environment.TickCount64;

            Task readTask = Task.Run(() => client.ReadStreamAsync(
                message =>
                {
                    lastAccessUnit = Environment.TickCount64;
                    _pipeline.Enqueue(message);
                },
                cycleCts.Token));

            while (!token.IsCancellationRequested)
            {
                await WaitAsync(TimeSpan.FromMilliseconds(400), token);
                if (readTask.IsCompleted || EngineExited(handle) || token.IsCancellationRequested)
                {
                    break;
                }

                TimeSpan idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - lastAccessUnit);
                if (idle > RestartIdleThreshold)
                {
                    SetStatus(new EngineStatus(EngineState.Stalled, "Recovering the stream", device.Label));
                    break;
                }
            }

            cycleCts.Cancel();
            try
            {
                await readTask;
            }
            catch (Exception)
            {
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            bool engineExited = EngineExited(handle);
            await StopEngineAsync();
            if (engineExited)
            {
                await ClassifyEngineFailure(device, token);
                return;
            }

            _firstFrameSeen = false;
            SetStatus(new EngineStatus(EngineState.Connecting, "Reattaching to the stream", device.Label));
            await WaitAsync(RetryDelay, token);
        }
        finally
        {
            _pipeline.KeyframeNeeded -= RequestKeyframe;
        }
    }

    private async Task ClassifyEngineFailure(UsbDevice device, CancellationToken token)
    {
        await StopEngineAsync();

        if (token.IsCancellationRequested)
        {
            return;
        }

        string log = _engine?.LogSnapshot ?? string.Empty;
        string detail = LastMeaningfulLine(log) ?? "The screen engine stopped unexpectedly";
        EngineState state = EngineState.Failed;
        if (DisplayServicePattern().IsMatch(log))
        {
            detail = "The Developer Disk Image is not mounted — retrying";
            _preparer?.Invalidate(device.Udid);
        }
        else if (TrustPattern().IsMatch(log))
        {
            state = EngineState.WaitingForTrust;
            detail = "Unlock the iPhone and tap Trust";
        }
        else if (NoDevicePattern().IsMatch(log))
        {
            state = EngineState.Reconnecting;
            detail = "Reconnecting...";
        }
        else if (DeveloperModePattern().IsMatch(log))
        {
            state = EngineState.WaitingForTrust;
            detail = "Enable Developer Mode on the iPhone";
        }

        SetStatus(new EngineStatus(state, detail, device.Label));
        await WaitAsync(state == EngineState.WaitingForTrust ? TrustRetryDelay : RetryDelay, token);
    }

    private static string? LastMeaningfulLine(string log)
    {
        string[] lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i];
            if (line.Length == 0 || line.StartsWith("engine exited", StringComparison.Ordinal))
            {
                continue;
            }
            if (line.Length > 140)
            {
                line = line[..140] + "…";
            }
            return line;
        }
        return null;
    }

    private static bool EngineExited(EngineHandle handle)
    {
        try
        {
            return handle.Process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private async Task StopEngineAsync()
    {
        if (_engine is not null)
        {
            await _engine.StopAsync();
        }
        _pipeline.ClearCodec();
        _firstFrameSeen = false;
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

private void SetStatus(EngineStatus status)
    {
        lock (_statusLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastStatusChange < MinStateChangeInterval && Current.State != EngineState.Streaming && status.State != EngineState.Streaming)
            {
                return;
            }
            if (Current.State == status.State && Current.Detail == status.Detail)
            {
                return;
            }

            // Track when Streaming starts
            if (status.State == EngineState.Streaming && Current.State != EngineState.Streaming)
            {
                _streamingStartedAt = DateTime.UtcNow;
            }

            // Prevent leaving Streaming state too quickly (minimum 10 seconds)
            if (Current.State == EngineState.Streaming && status.State != EngineState.Streaming)
            {
                if (DateTime.UtcNow - _streamingStartedAt < TimeSpan.FromSeconds(10))
                {
                    return;
                }
            }

            if (Current.State == status.State && Current.Detail == status.Detail)
            {
                return;
            }
            Current = status;
            _lastStatusChange = DateTime.UtcNow;
        }
        StatusChanged?.Invoke(status);
    }

    private static string Shorten(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 140 ? text : text[..140] + "…";
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource lifetime = _lifetime;
        _lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        lifetime.Dispose();

        if (_sessionTask is { } sessionTask)
        {
            try
            {
                await sessionTask;
            }
            catch (Exception)
            {
            }
            _sessionTask = null;
        }
        _pipeline.FrameReady -= OnFirstFrame;
        await StopEngineAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync();
        _pipeline.Dispose();
    }
}
