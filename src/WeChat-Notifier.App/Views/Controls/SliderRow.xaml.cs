using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Globalization;

namespace WeChat_Notifier.Views.Controls;

public sealed partial class SliderRow : UserControl
{
    private bool _ready, _updating;
    public static readonly DependencyProperty LabelProperty = Register(nameof(Label), typeof(string), "");
    public static readonly DependencyProperty HintProperty = Register(nameof(Hint), typeof(string), "");
    public static readonly DependencyProperty SuffixProperty = Register(nameof(Suffix), typeof(string), "");
    public static readonly DependencyProperty FormatProperty = Register(nameof(Format), typeof(string), "0.##");
    public static readonly DependencyProperty MinProperty = Register(nameof(Min), typeof(double), 0d);
    public static readonly DependencyProperty MaxProperty = Register(nameof(Max), typeof(double), 100d);
    public static readonly DependencyProperty StepProperty = Register(nameof(Step), typeof(double), 1d);
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), typeof(double), 0d);
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public double Min { get => (double)GetValue(MinProperty); set => SetValue(MinProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private static DependencyProperty Register(string name, Type type, object value) =>
        DependencyProperty.Register(name, type, typeof(SliderRow),
            new PropertyMetadata(value, (obj, _) => ((SliderRow)obj).Refresh()));

    public SliderRow()
    {
        InitializeComponent();
        _ready = true;
        Refresh();
    }

    private void Refresh()
    {
        if (!_ready || _updating) return;
        _updating = true;
        try
        {
            LabelText.Text = Label;
            HintText.Text = Hint;
            HintText.Visibility = string.IsNullOrEmpty(Hint) ? Visibility.Collapsed : Visibility.Visible;
            ValueText.Text = Value.ToString(Format, CultureInfo.CurrentCulture) + Suffix;
            // 调整范围可能钳制 Slider.Value，不能把初始化钳制写回配置。
            ValueSlider.Maximum = Math.Max(Max, Min);
            ValueSlider.Minimum = Min;
            ValueSlider.StepFrequency = Step;
            ValueSlider.Value = Value;
            AutomationProperties.SetName(ValueSlider, Label);
            AutomationProperties.SetHelpText(ValueSlider, Hint);
        }
        finally { _updating = false; }
    }

    private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_ready || _updating) return;
        Value = Math.Round(args.NewValue, 4);
    }
}
