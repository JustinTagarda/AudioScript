using System.Windows;
using AudioTranscript.ViewModels;

namespace AudioTranscript;

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
            ShowError($"Unable to validate API key: {ex.Message}");
        }
        finally {
            SetSavingState(false);
        }
    }

    private void SetSavingState(bool isSaving) {
        _isSaving = isSaving;
        SaveButton.IsEnabled = !isSaving;
        SaveButton.Content = isSaving ? "Validating..." : "Save";
    }

    private void ShowError(string message) {
        var dialog = new ErrorDialogWindow(message) {
            Owner = this,
        };
        dialog.ShowDialog();
    }
}
