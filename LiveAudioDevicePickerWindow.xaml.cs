using System.Windows;
using AudioScript.Audio;

namespace AudioScript;

public partial class LiveAudioDevicePickerWindow : Window
{
    public LiveAudioDevicePickerWindow(IReadOnlyList<AudioInputDeviceOption> devices)
    {
        InitializeComponent();
        DeviceComboBox.ItemsSource = devices;
        DeviceComboBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
    }

    public AudioInputDeviceOption? SelectedDevice =>
        DeviceComboBox.SelectedItem as AudioInputDeviceOption;

    public void SelectPreferredDevice(LiveAudioSourceKind preferredKind, int preferredDeviceNumber)
    {
        AudioInputDeviceOption? preferred = DeviceComboBox.Items
            .OfType<AudioInputDeviceOption>()
            .FirstOrDefault(item =>
                item.Kind == preferredKind
                && item.DeviceNumber == preferredDeviceNumber);

        if (preferred is not null)
        {
            DeviceComboBox.SelectedItem = preferred;
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is null)
        {
            return;
        }

        DialogResult = true;
    }
}
