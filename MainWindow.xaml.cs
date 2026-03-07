using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AudioTranscript.ViewModels;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DataGridCellsPresenter = System.Windows.Controls.Primitives.DataGridCellsPresenter;

namespace AudioTranscript;

public partial class MainWindow : Window {
    private const int TranscriptTextColumnIndex = 1;

    private bool _isOpenAiDialogOpen;
    private bool _isAdjustingTranscriptCell;
    private MainViewModel? _boundViewModel;
    private CancellationTokenSource? _copyToastCts;

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
            string plainText = string.Join(
                Environment.NewLine,
                vm.FinalizedTranscriptLines
                    .Select(line => line.Text?.Trim() ?? string.Empty)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));

            System.Windows.Clipboard.SetText(plainText);
            ShowCopyToast(
                "Copied to clipboard",
                "Finalized transcript is ready to paste.");
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
        CancelCopyToast();

        if (_boundViewModel is null) {
            return;
        }

        _boundViewModel.ErrorOccurred -= OnErrorOccurred;
        _boundViewModel.ProcessLogs.CollectionChanged -= OnProcessLogsCollectionChanged;
        _boundViewModel = null;
    }

    private void ShowCopyToast(string title, string message) {
        double startOpacity = CopyToastHost.Visibility == Visibility.Visible
            ? CopyToastHost.Opacity
            : 0;
        double startOffset = CopyToastHost.Visibility == Visibility.Visible
            ? CopyToastTransform.Y
            : 14;

        CancelCopyToast();

        CopyToastTitleText.Text = title;
        CopyToastMessageText.Text = message;
        CopyToastHost.Visibility = Visibility.Visible;
        CopyToastHost.Opacity = startOpacity;
        CopyToastTransform.Y = startOffset;

        CopyToastHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(startOpacity, 1, TimeSpan.FromMilliseconds(180)) {
                EasingFunction = new CubicEase {
                    EasingMode = EasingMode.EaseOut,
                },
            });
        CopyToastTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(startOffset, 0, TimeSpan.FromMilliseconds(180)) {
                EasingFunction = new CubicEase {
                    EasingMode = EasingMode.EaseOut,
                },
            });

        _copyToastCts = new CancellationTokenSource();
        _ = HideCopyToastAfterDelayAsync(_copyToastCts);
    }

    private async Task HideCopyToastAfterDelayAsync(CancellationTokenSource toastCts) {
        try {
            await Task.Delay(TimeSpan.FromSeconds(3), toastCts.Token);
        }
        catch (OperationCanceledException) {
            return;
        }

        if (toastCts.Token.IsCancellationRequested) {
            return;
        }

        var opacityAnimation = new DoubleAnimation(CopyToastHost.Opacity, 0, TimeSpan.FromMilliseconds(180)) {
            EasingFunction = new CubicEase {
                EasingMode = EasingMode.EaseIn,
            },
        };
        opacityAnimation.Completed += (_, _) => {
            if (toastCts.Token.IsCancellationRequested) {
                return;
            }

            CopyToastHost.Visibility = Visibility.Collapsed;
            CopyToastHost.Opacity = 0;
            CopyToastTransform.Y = 14;
        };

        CopyToastHost.BeginAnimation(OpacityProperty, opacityAnimation);
        CopyToastTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(CopyToastTransform.Y, 10, TimeSpan.FromMilliseconds(180)) {
                EasingFunction = new CubicEase {
                    EasingMode = EasingMode.EaseIn,
                },
            });
    }

    private void CancelCopyToast() {
        if (_copyToastCts is not null) {
            try {
                _copyToastCts.Cancel();
            }
            catch (ObjectDisposedException) {
            }

            _copyToastCts.Dispose();
            _copyToastCts = null;
        }

        CopyToastHost.BeginAnimation(OpacityProperty, null);
        CopyToastTransform.BeginAnimation(TranslateTransform.YProperty, null);
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

    private void FinalizedTranscriptGrid_CurrentCellChanged(object sender, EventArgs e) {
        if (_isAdjustingTranscriptCell) {
            return;
        }

        if (FinalizedTranscriptGrid.Columns.Count <= TranscriptTextColumnIndex) {
            return;
        }

        DataGridCellInfo current = FinalizedTranscriptGrid.CurrentCell;
        DataGridColumn transcriptColumn = FinalizedTranscriptGrid.Columns[TranscriptTextColumnIndex];

        if (current.Column == transcriptColumn) {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(() => EnsureTranscriptColumnFocused(beginEdit: false)),
            DispatcherPriority.Background);
    }

    private void FinalizedTranscriptGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
        if (FinalizedTranscriptGrid.Items.Count == 0 || FinalizedTranscriptGrid.Columns.Count <= TranscriptTextColumnIndex) {
            return;
        }

        if (e.Key == Key.Enter) {
            if (!IsCurrentTranscriptCellEditing()) {
                e.Handled = true;
                EnsureTranscriptColumnFocused(beginEdit: true);
            }

            return;
        }

        if (e.Key is Key.Up or Key.Down) {
            e.Handled = true;
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            FinalizedTranscriptGrid.CommitEdit(DataGridEditingUnit.Row, true);
            MoveTranscriptFocusByRow(e.Key == Key.Up ? -1 : 1);
        }
    }

    private void MoveTranscriptFocusByRow(int delta) {
        IList<object> rowItems = GetTranscriptRowItems();
        if (rowItems.Count == 0) {
            return;
        }

        object? currentItem = FinalizedTranscriptGrid.CurrentCell.Item;
        if (!IsDataItem(currentItem)) {
            currentItem = FinalizedTranscriptGrid.CurrentItem ?? FinalizedTranscriptGrid.SelectedItem;
        }

        int currentIndex = currentItem is null ? 0 : rowItems.IndexOf(currentItem);

        if (currentIndex < 0) {
            currentIndex = 0;
        }

        int targetIndex = Math.Min(
            Math.Max(currentIndex + delta, 0),
            rowItems.Count - 1);

        object targetItem = rowItems[targetIndex];
        FocusTranscriptCell(targetItem, beginEdit: false);
    }

    private void EnsureTranscriptColumnFocused(bool beginEdit) {
        if (FinalizedTranscriptGrid.Columns.Count <= TranscriptTextColumnIndex) {
            return;
        }

        IList<object> rowItems = GetTranscriptRowItems();
        if (rowItems.Count == 0) {
            return;
        }

        object? targetItem = FinalizedTranscriptGrid.CurrentCell.Item;

        if (!IsDataItem(targetItem)) {
            targetItem = FinalizedTranscriptGrid.CurrentItem ?? FinalizedTranscriptGrid.SelectedItem;
        }

        if (!IsDataItem(targetItem) || !rowItems.Contains(targetItem)) {
            targetItem = rowItems[0];
        }

        FocusTranscriptCell(targetItem, beginEdit);
    }

    private void FocusTranscriptCell(object targetItem, bool beginEdit) {
        if (FinalizedTranscriptGrid.Columns.Count <= TranscriptTextColumnIndex) {
            return;
        }

        try {
            _isAdjustingTranscriptCell = true;

            DataGridColumn targetColumn = FinalizedTranscriptGrid.Columns[TranscriptTextColumnIndex];
            var cellInfo = new DataGridCellInfo(targetItem, targetColumn);

            if (!cellInfo.IsValid) {
                return;
            }

            FinalizedTranscriptGrid.SelectedCells.Clear();
            FinalizedTranscriptGrid.CurrentCell = cellInfo;
            FinalizedTranscriptGrid.SelectedCells.Add(cellInfo);
            FinalizedTranscriptGrid.ScrollIntoView(targetItem, targetColumn);
            FinalizedTranscriptGrid.UpdateLayout();

            DataGridCell? targetCell = TryGetTranscriptCell(targetItem);
            if (targetCell is not null) {
                targetCell.Focus();
            }
            else {
                FinalizedTranscriptGrid.Focus();
            }

            if (beginEdit) {
                FinalizedTranscriptGrid.BeginEdit();
            }
        }
        catch {
            // Ignore focus correction failures to keep UI responsive.
        }
        finally {
            _isAdjustingTranscriptCell = false;
        }
    }

    private bool IsCurrentTranscriptCellEditing() {
        DataGridCellInfo current = FinalizedTranscriptGrid.CurrentCell;

        if (current.Column is null || current.Item is null) {
            return false;
        }

        FrameworkElement? element = current.Column.GetCellContent(current.Item);
        System.Windows.Controls.DataGridCell? cell = element?.Parent as System.Windows.Controls.DataGridCell;
        return cell?.IsEditing == true;
    }

    private System.Windows.Controls.DataGridCell? TryGetTranscriptCell(object item) {
        if (FinalizedTranscriptGrid.Columns.Count <= TranscriptTextColumnIndex) {
            return null;
        }

        DataGridColumn targetColumn = FinalizedTranscriptGrid.Columns[TranscriptTextColumnIndex];
        DataGridRow? row = FinalizedTranscriptGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
        if (row is null) {
            FinalizedTranscriptGrid.ScrollIntoView(item, targetColumn);
            FinalizedTranscriptGrid.UpdateLayout();
            row = FinalizedTranscriptGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
        }

        if (row is null) {
            return null;
        }

        DataGridCellsPresenter? presenter = FindVisualChild<DataGridCellsPresenter>(row);
        if (presenter is null) {
            row.ApplyTemplate();
            presenter = FindVisualChild<DataGridCellsPresenter>(row);
        }

        if (presenter is null) {
            return null;
        }

        System.Windows.Controls.DataGridCell? cell =
            presenter.ItemContainerGenerator.ContainerFromIndex(TranscriptTextColumnIndex) as System.Windows.Controls.DataGridCell;
        if (cell is null) {
            FinalizedTranscriptGrid.ScrollIntoView(item, targetColumn);
            FinalizedTranscriptGrid.UpdateLayout();
            cell =
                presenter.ItemContainerGenerator.ContainerFromIndex(TranscriptTextColumnIndex) as System.Windows.Controls.DataGridCell;
        }

        return cell;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++) {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);

            if (child is T match) {
                return match;
            }

            T? descendant = FindVisualChild<T>(child);
            if (descendant is not null) {
                return descendant;
            }
        }

        return null;
    }

    private IList<object> GetTranscriptRowItems() {
        return FinalizedTranscriptGrid.Items
            .Cast<object>()
            .Where(IsDataItem)
            .ToList();
    }

    private static bool IsDataItem(object? item) {
        return item is not null
            && !ReferenceEquals(item, CollectionView.NewItemPlaceholder)
            && !ReferenceEquals(item, DependencyProperty.UnsetValue);
    }
}
