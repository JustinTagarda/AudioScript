using System.Windows;
using AudioScript.ViewModels;

namespace AudioScript;

public partial class OpenAiSettingsWindow : Window {
    private bool _isSaving;

    public OpenAiSettingsWindow() {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) {
        if (DataContext is not MainViewModel viewModel) {
            return;
        }

        ApiKeyTextBox.Text = viewModel.OpenAiApiKey;
        ApiKeyTextBox.Focus();
        ApiKeyTextBox.CaretIndex = ApiKeyTextBox.Text.Length;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e) {
        if (_isSaving || DataContext is not MainViewModel viewModel) {
            return;
        }

        string apiKey = ApiKeyTextBox.Text.Trim();

        SetSavingState(true);

        try {
            var validationResult = await viewModel.ValidateOpenAiApiKeyAsync(apiKey, CancellationToken.None);
            if (!validationResult.IsValid) {
                ShowError(validationResult.Message);
                return;
            }

            viewModel.ApplyOpenAiSettings(apiKey);
            DialogResult = true;
        }
        catch (Exception ex) {
            viewModel.LogHandledException("OpenAI settings save", ex);
            ShowError($"Unable to validate API key: {ex.Message}");
        }
        finally {
            SetSavingState(false);
        }
    }

    private void SetSavingState(bool isSaving) {
        _isSaving = isSaving;
        RemoveButton.IsEnabled = !isSaving;
        SaveButton.IsEnabled = !isSaving;
        SaveButton.Content = isSaving ? "Validating..." : "Save";
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e) {
        if (_isSaving || DataContext is not MainViewModel viewModel) {
            return;
        }

        var confirmation = new ConfirmationDialogWindow(
            title: "Remove API key?",
            message: "This removes the stored OpenAI API key from this device and clears it from memory.",
            confirmButtonText: "Remove",
            cancelButtonText: "Cancel") {
            Owner = this,
        };

        if (confirmation.ShowDialog() != true) {
            return;
        }

        ApiKeyTextBox.Text = string.Empty;
        viewModel.RemoveOpenAiSettings();
        DialogResult = true;
    }

    private void ShowError(string message) {
        var dialog = new ErrorDialogWindow(message) {
            Owner = this,
        };
        dialog.ShowDialog();
    }
}



