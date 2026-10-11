using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using mirr.Engine;
using mirr.Media;
using mirr.Services;

namespace mirr.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly int[] FpsOptions = [15, 30, 60];

    private static readonly Color DotLive = Color.FromRgb(0x3F, 0xB9, 0x50);
    private static readonly Color DotWarn = Color.FromRgb(0xF0, 0x88, 0x3E);
    private static readonly Color DotFail = Color.FromRgb(0xF8, 0x51, 0x49);
    private static readonly Color DotIdle = Color.FromRgb(0x87, 0x88, 0x8A);

    private readonly MirrorSession _session;
    private readonly Dispatcher _dispatcher;
    private readonly object _pendingLock = new();
    private readonly object _bitmapLock = new();

    private FramePresentation? _pending;
    private WriteableBitmap? _bitmap;
    private FramePresentation? _lastFrame;
    private DateTime _lastPresentUtc = DateTime.MinValue;
    private int _presentedFrames;
    private DateTime _fpsWindowStart = DateTime.UtcNow;

    private EngineStatus _status = EngineStatus.Locating;
    private int _selectedFps = 60;
    private bool _isTopmost;
    private bool _isFullscreen;
    private double _rotation;
    private bool _toastVisible;
    private string _toastText = string.Empty;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _session = new MirrorSession();
        _session.StatusChanged += OnStatusChanged;
        _session.LogAppended += line => _dispatcher.BeginInvoke(() =>
        {
            LogLines.Add(Cap(line));
            AppendLog(line);
        });
        _session.Pipeline.FrameReady += OnFrameReady;
        LogLines.CollectionChanged += (_, _) => RaisePropertyChanged(nameof(LogText));
        StartLogFile();
    }

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mirr",
        "mirr.log");

    private static void StartLogFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            File.WriteAllText(LogFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} mirr session start{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }

    private static void AppendLog(string line)
    {
        try
        {
            File.AppendAllText(LogFilePath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> LogLines { get; } = [];

    public string LogText => string.Join(Environment.NewLine, LogLines);

    public MirrorSession Session => _session;

    public EngineStatus Status
    {
        get => _status;
        private set
        {
            if (Equals(value, _status))
            {
                return;
            }
            _status = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(StatusLabel));
            RaisePropertyChanged(nameof(StatusDetail));
            RaisePropertyChanged(nameof(StatusDot));
            RaisePropertyChanged(nameof(PillText));
            RaisePropertyChanged(nameof(ShowIdle));
            RaisePropertyChanged(nameof(IdleTitle));
            RaisePropertyChanged(nameof(IdleHint));
            RaisePropertyChanged(nameof(DeviceName));
        }
    }

    public string StatusLabel => _status.State switch
    {
        EngineState.Streaming => $"Live · {MeasuredFps} fps",
        EngineState.LocatingEngine => "Starting engine",
        EngineState.EngineMissing => "Engine missing",
        EngineState.WaitingForDevice => "No device",
        EngineState.Preparing => "Preparing device",
        EngineState.Connecting => "Linking",
        EngineState.WaitingForTrust => "Trust needed",
        EngineState.Stalled => "Recovering",
        EngineState.Failed => "Failed",
        EngineState.Disconnected => "Disconnected",
        EngineState.Reconnecting => "Reconnecting…",
        _ => "Idle",
    };

    public string PillText => _status.State switch
    {
        EngineState.Streaming => $"{MeasuredFps} fps",
        EngineState.WaitingForDevice => "No device",
        EngineState.Preparing => "Prepare",
        EngineState.Connecting => "Linking",
        EngineState.WaitingForTrust => "Trust",
        EngineState.Stalled => "Stalled",
        EngineState.Failed => "Error",
        EngineState.Disconnected => "Disconnected",
        EngineState.Reconnecting => "Reconnecting…",
        _ => "Idle",
    };

    public bool ShowIdle => _status.State is not EngineState.Streaming and not EngineState.Connecting;

    public string IdleTitle => _status.State switch
    {
        EngineState.EngineMissing => "Engine not installed",
        EngineState.Failed => "Engine error",
        EngineState.LocatingEngine => "Starting engine",
        EngineState.Preparing => "Preparing device",
        EngineState.Disconnected => "Disconnected",
        EngineState.Reconnecting => "Reconnecting…",
        _ => "No device connected",
    };

    public string IdleHint => _status.State switch
    {
        EngineState.EngineMissing => "pip install pymobiledevice3 — see the README",
        EngineState.Failed => _status.Detail,
        EngineState.LocatingEngine => "Looking for the pymobiledevice3 engine",
        EngineState.Preparing => _status.Detail,
        EngineState.Disconnected => "iPhone was disconnected",
        EngineState.Reconnecting => "Attempting to reconnect…",
        _ => "Connect an iPhone over USB, then unlock it once to pair",
    };

    public string StatusDetail => _status.Detail;

    public Brush StatusDot => _status.State switch
    {
        EngineState.Streaming => SolidColorBrush(DotLive),
        EngineState.WaitingForTrust or EngineState.Stalled or EngineState.EngineMissing => SolidColorBrush(DotWarn),
        EngineState.Failed => SolidColorBrush(DotFail),
        EngineState.Disconnected => SolidColorBrush(DotWarn),
        EngineState.Reconnecting => SolidColorBrush(DotWarn),
        _ => SolidColorBrush(DotIdle),
    };

    public string DeviceName => _status.DeviceLabel;

    public string MeasuredFps { get; private set; } = "0";

    public IReadOnlyList<int> FpsChoices => FpsOptions;

    public int SelectedFps
    {
        get => _selectedFps;
        set
        {
            if (_selectedFps == value)
            {
                return;
            }
            _selectedFps = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(FpsLabel));
        }
    }

    public string FpsLabel => $"{SelectedFps} fps";

    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (_isFullscreen == value)
            {
                return;
            }
            _isFullscreen = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(FullscreenGlyph));
        }
    }

    public string FullscreenGlyph => IsFullscreen ? "\uE741" : "\uE740";

    public bool IsTopmost
    {
        get => _isTopmost;
        set
        {
            if (_isTopmost == value)
            {
                return;
            }
            _isTopmost = value;
            RaisePropertyChanged();
        }
    }

    public double Rotation
    {
        get => _rotation;
        set
        {
            if (Math.Abs(_rotation - value) < 0.01)
            {
                return;
            }
            _rotation = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(RotationLabel));
            RaisePropertyChanged(nameof(NextRotationLabel));
        }
    }

    public string RotationLabel => Rotation switch
    {
        90 => "90°",
        180 => "180°",
        270 => "270°",
        _ => "0°",
    };

    public string NextRotationLabel => Rotation switch
    {
        0 => "Rotate to 90°",
        90 => "Rotate to 180°",
        180 => "Rotate to 270°",
        _ => "Reset rotation",
    };

    public bool CanScreenshot => _lastFrame is not null;

    public bool ToastVisible
    {
        get => _toastVisible;
        private set
        {
            if (_toastVisible == value)
            {
                return;
            }
            _toastVisible = value;
            RaisePropertyChanged();
        }
    }

    public string ToastText
    {
        get => _toastText;
        private set
        {
            if (value == _toastText)
            {
                return;
            }
            _toastText = value;
            RaisePropertyChanged();
        }
    }

    public WriteableBitmap? Frame
    {
        get => _bitmap;
        private set
        {
            if (ReferenceEquals(value, _bitmap))
            {
                return;
            }
            _bitmap = value;
            RaisePropertyChanged();
        }
    }

    public async Task StartAsync()
    {
        await _session.StartAsync();
    }

    public async Task ShutdownAsync()
    {
        await _session.StopAsync();
    }

    public async Task RetryAsync()
    {
        await _session.RestartAsync();
    }

    public void OnRenderTick(DateTime nowUtc)
    {
        FramePresentation? frame = TakePending();
        if (frame is null)
        {
            return;
        }

        double minIntervalMs = 1000.0 / Math.Max(1, SelectedFps);
        if ((nowUtc - _lastPresentUtc).TotalMilliseconds < minIntervalMs - 1.0)
        {
            StorePending(frame);
            return;
        }
        _lastPresentUtc = nowUtc;

        lock (_bitmapLock)
        {
            if (Frame is null
                || Frame.PixelWidth != frame.Width
                || Frame.PixelHeight != frame.Height
                || (frame.HasAlpha && Frame.Format != PixelFormats.Bgra32)
                || (!frame.HasAlpha && Frame.Format != PixelFormats.Bgr32))
            {
                Frame = new WriteableBitmap(
                    frame.Width,
                    frame.Height,
                    96,
                    96,
                    frame.HasAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32,
                    null);
            }

            Frame.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Pixels,
                frame.Stride,
                0);
        }

        _lastFrame = frame;
        if (_lastFrame is not null && _presentedFrames == 0)
        {
            RaisePropertyChanged(nameof(CanScreenshot));
        }

        _presentedFrames++;
        if (nowUtc - _fpsWindowStart >= TimeSpan.FromSeconds(1))
        {
            double fps = _presentedFrames / (nowUtc - _fpsWindowStart).TotalSeconds;
            MeasuredFps = fps.ToString("0");
            _presentedFrames = 0;
            _fpsWindowStart = nowUtc;
            RaisePropertyChanged(nameof(StatusLabel));
            RaisePropertyChanged(nameof(PillText));
        }
    }

    private FramePresentation? TakePending()
    {
        lock (_pendingLock)
        {
            FramePresentation? frame = _pending;
            _pending = null;
            return frame;
        }
    }

    private void StorePending(FramePresentation frame)
    {
        lock (_pendingLock)
        {
            _pending = frame;
        }
    }

    private void OnFrameReady(FramePresentation frame)
    {
        StorePending(frame);
    }

    private void OnStatusChanged(EngineStatus status)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Status = status;
            if (status.State == EngineState.Disconnected)
            {
                lock (_bitmapLock)
                {
                    Frame = null;
                }
            }
            AppendLog($"status: {status.State} \u2014 {status.Detail}");
        });
    }

    public void TakeScreenshot()
    {
        WriteableBitmap? presented;
        lock (_bitmapLock)
        {
            presented = Frame;
        }
        if (presented is null)
        {
            return;
        }

        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "mirr");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"mirr_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Oriented(presented, Rotation)));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);

        ShowToast("Screenshot saved");
        _dispatcher.BeginInvoke(() => LogLines.Add(Cap($"screenshot → {path}")));
    }

    private static BitmapSource Oriented(BitmapSource source, double rotation)
    {
        double angle = rotation % 360;
        if (Math.Abs(angle) < 0.01)
        {
            return source;
        }
        return new TransformedBitmap(source, new RotateTransform(angle));
    }

    private async void ShowToast(string message)
    {
        ToastText = message;
        ToastVisible = true;
        await Task.Delay(TimeSpan.FromSeconds(2));
        ToastVisible = false;
    }

    private static string Cap(string line)
    {
        if (line.Length <= 220)
        {
            return line;
        }
        return line[..220] + "…";
    }

    private static SolidColorBrush SolidColorBrush(Color color) => new(color);

    public void Dispose()
    {
        _session.Pipeline.FrameReady -= OnFrameReady;
        _session.StatusChanged -= OnStatusChanged;
    }

    private void RaisePropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
