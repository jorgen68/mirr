using System.Windows;
using mirr.ViewModels;

namespace mirr.Views;

public partial class EngineLogWindow : Window
{
    public MainViewModel ViewModel { get; }

    public EngineLogWindow()
    {
        InitializeComponent();
        ViewModel = ((MainWindow)Application.Current.MainWindow).ViewModel;
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LogText))
        {
            LogBox.ScrollToEnd();
        }
    }
}
