using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

public partial class WireToGateMainWindow : Window
{
    private MainViewModel? _viewModel;

    public WireToGateMainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => FocusScanInput();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CanSubmit) && _viewModel?.CanSubmit == true)
        {
            _ = Dispatcher.BeginInvoke(FocusScanInput, DispatcherPriority.Input);
        }
    }

    private void FocusScanInput()
    {
        if (ScanTextBox.IsEnabled)
        {
            ScanTextBox.Focus();
            ScanTextBox.SelectAll();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        DataContextChanged -= OnDataContextChanged;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
