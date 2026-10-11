using System.Threading.Channels;
using mirr.Engine;

namespace mirr.Media;

public sealed record FramePresentation(byte[] Pixels, int Width, int Height, int Stride, bool HasAlpha);

public sealed class DecoderPipeline : IDisposable
{
    private static readonly TimeSpan MinimumSpawnInterval = TimeSpan.FromMilliseconds(700);

    private readonly object _frameLock = new();
    private readonly object _bridgeLock = new();
    private readonly Channel<AccessUnitMessage> _channel;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();

    private BridgeDecoder? _bridge;
    private EngineCommand? _pythonCommand;
    private CodecDescription? _codec;
    private FramePresentation? _latest;
    private long _frameVersion;
    private DateTime _lastSpawnUtc = DateTime.MinValue;
    private int _consecutiveSpawns;
    private bool _started;

    public DecoderPipeline()
    {
        _channel = Channel.CreateUnbounded<AccessUnitMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "mirr.decoder",
        };
    }

    public event Action<FramePresentation>? FrameReady;
    public event Action<string>? LogLine;
    public event Action? KeyframeNeeded;

    public long LatestVersion
    {
        get
        {
            lock (_frameLock)
            {
                return _frameVersion;
            }
        }
    }

    public FramePresentation? TakeLatest()
    {
        lock (_frameLock)
        {
            return _latest;
        }
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _thread.Start();
    }

    public void SetInterpreter(EngineCommand pythonCommand)
    {
        _pythonCommand = pythonCommand;
    }

    public void SetCodec(CodecDescription description)
    {
        _codec = description;
        lock (_bridgeLock)
        {
            StopBridge();
            EnsureBridge();
        }
    }

    public void ClearCodec()
    {
        _codec = null;
        lock (_bridgeLock)
        {
            StopBridge();
        }
    }

    public void ClearFrame()
    {
        lock (_frameLock)
        {
            _latest = null;
            _frameVersion++;
        }
        FrameReady?.Invoke(new FramePresentation([], 0, 0, 0, false));
    }

    public void Enqueue(AccessUnitMessage message)
    {
        _channel.Writer.TryWrite(message);
    }

    private void Run()
    {
        CancellationToken cancellation = _cancellation.Token;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                bool available = _channel.Reader.WaitToReadAsync(cancellation).AsTask().GetAwaiter().GetResult();
                if (!available)
                {
                    continue;
                }
                while (_channel.Reader.TryRead(out AccessUnitMessage? message))
                {
                    if (message is not null)
                    {
                        Dispatch(message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogLine?.Invoke($"decoder: {exception.Message}");
            }
        }
    }

    private void Dispatch(AccessUnitMessage message)
    {
        lock (_bridgeLock)
        {
            EnsureBridge();
            _bridge?.Feed(message.AnnexB);
        }
    }

    private void EnsureBridge()
    {
        if (_codec is null || _pythonCommand is null)
        {
            return;
        }
        if (_bridge is { } existing && existing.IsRunning)
        {
            return;
        }
        if (DateTime.UtcNow - _lastSpawnUtc < MinimumSpawnInterval)
        {
            return;
        }
        _lastSpawnUtc = DateTime.UtcNow;

        StopBridge();
        var bridge = new BridgeDecoder();
        bridge.FrameReady += OnFrame;
        bridge.LogLine += line => LogLine?.Invoke(line);
        bridge.Faulted += OnFaulted;
        try
        {
            bridge.Start(_pythonCommand, _codec.Family);
            _bridge = bridge;
            _consecutiveSpawns++;
            if (_consecutiveSpawns > 1)
            {
                KeyframeNeeded?.Invoke();
            }
        }
        catch (Exception exception)
        {
            bridge.Dispose();
            LogLine?.Invoke($"decoder: cannot start bridge ({exception.Message})");
            _bridge = null;
        }
    }

    private void OnFrame(FramePresentation frame)
    {
        _consecutiveSpawns = 0;
        lock (_frameLock)
        {
            _frameVersion++;
            _latest = frame;
        }
        FrameReady?.Invoke(frame);
    }

    private void OnFaulted(string reason)
    {
        LogLine?.Invoke($"decoder: {reason}");
        lock (_bridgeLock)
        {
            StopBridge();
        }
        KeyframeNeeded?.Invoke();
    }

    private void StopBridge()
    {
        BridgeDecoder? bridge = _bridge;
        _bridge = null;
        if (bridge is null)
        {
            return;
        }
        bridge.FrameReady -= OnFrame;
        bridge.Faulted -= OnFaulted;
        bridge.Dispose();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _channel.Writer.TryComplete();
        lock (_bridgeLock)
        {
            StopBridge();
        }
        if (_thread.IsAlive && _thread.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }
        _cancellation.Dispose();
    }
}
