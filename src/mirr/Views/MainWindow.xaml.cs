using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using mirr.Interop;
using mirr.ViewModels;

namespace mirr.Views;

public partial class MainWindow : Window
{
    private bool _closing;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel();
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SourceInitialized += OnSourceInitialized;
        CompositionTarget.Rendering += OnCompositionRendering;
        StateChanged += OnStateChanged;
        Closing += OnClosingRequested;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.StartAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        ApplyImmersiveDarkMode(hwnd);
        ApplyBackdrop(hwnd);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsTopmost))
        {
            ApplyTopmost();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsFullscreen))
        {
            ApplyFullscreen();
        }
    }

    private void ApplyFullscreen()
    {
        _fullscreen = ViewModel.IsFullscreen;
        if (_fullscreen)
        {
            _stateBeforeFullscreen = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = _stateBeforeFullscreen;
        }
        TitleBarRow.Height = _fullscreen ? new GridLength(0) : new GridLength(44);
        UpdateRootMargin();
    }

    private void UpdateRootMargin()
    {
        RootBorder.Margin = !_fullscreen && WindowState == WindowState.Maximized
            ? new Thickness(7)
            : new Thickness(0);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel.IsFullscreen)
        {
            ViewModel.IsFullscreen = false;
            e.Handled = true;
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        ViewModel.OnRenderTick(DateTime.UtcNow);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateRootMargin();
    }

    private void OnClosingRequested(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        CompositionTarget.Rendering -= OnCompositionRendering;
        _ = ViewModel.ShutdownAsync();
    }

    private void ApplyImmersiveDarkMode(IntPtr hwnd)
    {
        int enabled = 1;
        DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private void ApplyBackdrop(IntPtr hwnd)
    {
        if (Environment.OSVersion.Version.Build < 22000)
        {
            RootBorder.Background = (Brush)FindResource("FallbackBackground");
            return;
        }

        int backdrop = DwmApi.DwmsbtMainwindow;
        int hr = DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        if (hr != 0)
        {
            RootBorder.Background = (Brush)FindResource("FallbackBackground");
            return;
        }

        DwmApi.Margins margins = DwmApi.Margins.Extended;
        DwmApi.DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private void ApplyTopmost()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        IntPtr insertAfter = ViewModel.IsTopmost ? User32.HwndTopmost : User32.HwndNotTopmost;
        User32.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, User32.SwpNomove | User32.SwpNosize | User32.SwpShowwindow);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnScreenshot(object sender, RoutedEventArgs e) => ViewModel.TakeScreenshot();

    private void OnCycleOrientation(object sender, RoutedEventArgs e)
    {
        ViewModel.Rotation = ViewModel.Rotation switch
        {
            0 => 90,
            90 => 180,
            180 => 270,
            _ => 0,
        };
    }

    private void OnFpsMenu(object sender, RoutedEventArgs e) => FpsPopup.IsOpen = true;

    private void OnFps15(object sender, RoutedEventArgs e) => ViewModel.SelectedFps = 15;

    private void OnFps30(object sender, RoutedEventArgs e) => ViewModel.SelectedFps = 30;

    private void OnFps60(object sender, RoutedEventArgs e) => ViewModel.SelectedFps = 60;

    private void OnStatusPillClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ShowEngineLog();
    }

    private void OnShowLog(object sender, RoutedEventArgs e)
    {
        ShowEngineLog();
    }

    private EngineLogWindow? _logWindow;

    private void ShowEngineLog()
    {
        if (_logWindow is { IsLoaded: true })
        {
            _logWindow.Activate();
            return;
        }
        _logWindow = new EngineLogWindow { Owner = this };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }
}
