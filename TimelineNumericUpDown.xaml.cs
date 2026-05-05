using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace AudioScript;

public partial class TimelineNumericUpDown : WpfUserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(TimeSpan),
        typeof(TimelineNumericUpDown),
        new FrameworkPropertyMetadata(
            TimeSpan.Zero,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(TimeSpan),
        typeof(TimelineNumericUpDown),
        new PropertyMetadata(TimeSpan.Zero, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(TimeSpan),
        typeof(TimelineNumericUpDown),
        new PropertyMetadata(TimeSpan.FromHours(1), OnRangeChanged));

    public static readonly DependencyProperty IncrementProperty = DependencyProperty.Register(
        nameof(Increment),
        typeof(TimeSpan),
        typeof(TimelineNumericUpDown),
        new PropertyMetadata(TimeSpan.FromSeconds(1), OnIncrementChanged));

    public TimelineNumericUpDown()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateText();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public TimeSpan Value
    {
        get => (TimeSpan)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public TimeSpan Minimum
    {
        get => (TimeSpan)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public TimeSpan Maximum
    {
        get => (TimeSpan)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public TimeSpan Increment
    {
        get => (TimeSpan)GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    private static void OnValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        var control = (TimelineNumericUpDown)dependencyObject;
        TimeSpan coerced = control.CoerceValueWithinBounds(control.Value);
        if (control.Value != coerced)
        {
            control.SetCurrentValue(ValueProperty, coerced);
            return;
        }

        control.UpdateText();
    }

    private static void OnRangeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        var control = (TimelineNumericUpDown)dependencyObject;
        if (control.Maximum < control.Minimum)
        {
            control.SetCurrentValue(MaximumProperty, control.Minimum);
        }

        control.SetCurrentValue(ValueProperty, control.CoerceValueWithinBounds(control.Value));
        control.UpdateText();
    }

    private static void OnIncrementChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        var control = (TimelineNumericUpDown)dependencyObject;
        if (control.Increment <= TimeSpan.Zero)
        {
            control.SetCurrentValue(IncrementProperty, TimeSpan.FromSeconds(1));
        }
    }

    private void IncreaseValue_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Step(+1);
    }

    private void DecreaseValue_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Step(-1);
    }

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        _ = sender;

        if (e.Key == Key.Up)
        {
            e.Handled = true;
            Step(+1);
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            Step(-1);
        }
    }

    private void Step(int direction)
    {
        TimeSpan nextValue = Value + (Increment * direction);
        Value = CoerceValueWithinBounds(nextValue);
    }

    private TimeSpan CoerceValueWithinBounds(TimeSpan value)
    {
        if (value < Minimum)
        {
            return Minimum;
        }

        if (value > Maximum)
        {
            return Maximum;
        }

        return value;
    }

    private void UpdateText()
    {
        if (ValueTextBlock is null)
        {
            return;
        }

        TimeSpan displayValue = Value < TimeSpan.Zero ? TimeSpan.Zero : Value;
        int totalMinutes = (int)displayValue.TotalMinutes;
        ValueTextBlock.Text = $"{totalMinutes:00}:{displayValue.Seconds:00}";
    }
}
