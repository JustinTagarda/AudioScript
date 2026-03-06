using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioTranscript.ViewModels;

namespace AudioTranscript;

public partial class MainWindow : Window {
    private bool _isOpenAiDialogOpen;
    private MainViewModel? _boundViewModel;

    public MainWindow() {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnMainWindowClosed;
    }

    private void EngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (!IsLoaded || DataContext is not MainViewModel vm) {
            return;
        }

        if (!vm.IsOpenAiEngineSelected) {
            return;
        }

        if (!string.IsNullOrWhiteSpace(vm.OpenAiApiKey)) {
            return;
        }

        ShowOpenAiSettingsDialog();
    }

    private void OpenOpenAiSettings_Click(object sender, RoutedEventArgs e) {
        ShowOpenAiSettingsDialog();
    }

    private void CopyFinalizedToClipboard_Click(object sender, RoutedEventArgs e) {
        if (DataContext is not MainViewModel vm) {
            return;
        }

        try {
            System.Windows.Clipboard.SetText(vm.FinalizedText ?? string.Empty);
        }
        catch (Exception ex) {
            var dialog = new ErrorDialogWindow($"Unable to copy transcript to clipboard: {ex.Message}") {
                Owner = this,
            };
            dialog.ShowDialog();
        }
    }

    private void ShowOpenAiSettingsDialog() {
        if (_isOpenAiDialogOpen || DataContext is not MainViewModel vm || !vm.IsOpenAiEngineSelected) {
            return;
        }

        var dialog = new OpenAiSettingsWindow {
            Owner = this,
            DataContext = DataContext,
        };

        try {
            _isOpenAiDialogOpen = true;
            dialog.ShowDialog();
        }
        finally {
            _isOpenAiDialogOpen = false;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) {
        if (_boundViewModel is not null) {
            _boundViewModel.ErrorOccurred -= OnErrorOccurred;
            _boundViewModel.ProcessLogs.CollectionChanged -= OnProcessLogsCollectionChanged;
            _boundViewModel = null;
        }

        if (e.NewValue is MainViewModel vm) {
            _boundViewModel = vm;
            _boundViewModel.ErrorOccurred += OnErrorOccurred;
            _boundViewModel.ProcessLogs.CollectionChanged += OnProcessLogsCollectionChanged;
            ScrollLogsToLatest();
        }
    }

    private void OnErrorOccurred(object? sender, string message) {
        var dialog = new ErrorDialogWindow(message) {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e) {
        if (_boundViewModel is null) {
            return;
        }

        _boundViewModel.ErrorOccurred -= OnErrorOccurred;
        _boundViewModel.ProcessLogs.CollectionChanged -= OnProcessLogsCollectionChanged;
        _boundViewModel = null;
    }

    private void OnProcessLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        ScrollLogsToLatest();
    }

    private void ScrollLogsToLatest() {
        if (_boundViewModel is null || _boundViewModel.ProcessLogs.Count == 0) {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => {
            if (_boundViewModel is null || _boundViewModel.ProcessLogs.Count == 0) {
                return;
            }

            ProcessLogsListView.ScrollIntoView(_boundViewModel.ProcessLogs[^1]);
        }), DispatcherPriority.Background);
    }
}
