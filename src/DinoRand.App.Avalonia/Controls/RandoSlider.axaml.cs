#nullable enable
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace DinoRand.App
{
    /// <summary>
    /// Avalonia port of the WPF <c>RandoSlider</c>: a labelled <see cref="Slider"/> with optional
    /// heading, low/high captions, a centred caption, and tick snapping. Translation notes:
    ///   • all seven <c>DependencyProperty</c>s → <see cref="StyledProperty{T}"/>; <c>Value</c> is
    ///     registered <see cref="BindingMode.TwoWay"/> so a host two-way binding round-trips.
    ///   • the three metadata callbacks (heading/center visibility, tick placement) →
    ///     static <c>Changed.AddClassHandler</c> handlers, null-guarded for first-set ordering.
    ///   • <c>ValueChanged</c> stays a public event; its args are Avalonia's
    ///     <see cref="RangeBaseValueChangedEventArgs"/> (OldValue/NewValue) instead of WPF's
    ///     <c>RoutedPropertyChangedEventArgs&lt;double&gt;</c>.
    /// </summary>
    public partial class RandoSlider : UserControl
    {
        public static readonly StyledProperty<string> HeadingProperty =
            AvaloniaProperty.Register<RandoSlider, string>(nameof(Heading), "Heading");

        public static readonly StyledProperty<string> LowTextProperty =
            AvaloniaProperty.Register<RandoSlider, string>(nameof(LowText), "Low");

        public static readonly StyledProperty<string> HighTextProperty =
            AvaloniaProperty.Register<RandoSlider, string>(nameof(HighText), "High");

        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<RandoSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<double> MaximumProperty =
            AvaloniaProperty.Register<RandoSlider, double>(nameof(Maximum));

        public static readonly StyledProperty<string> CenterTextProperty =
            AvaloniaProperty.Register<RandoSlider, string>(nameof(CenterText), "");

        public static readonly StyledProperty<bool> ShowTicksProperty =
            AvaloniaProperty.Register<RandoSlider, bool>(nameof(ShowTicks));

        /// <summary>Raised when the underlying slider's value changes (drag or programmatic).</summary>
        public event EventHandler<RangeBaseValueChangedEventArgs>? ValueChanged;

        public string Heading
        {
            get => GetValue(HeadingProperty);
            set => SetValue(HeadingProperty, value);
        }

        public string LowText
        {
            get => GetValue(LowTextProperty);
            set => SetValue(LowTextProperty, value);
        }

        public string HighText
        {
            get => GetValue(HighTextProperty);
            set => SetValue(HighTextProperty, value);
        }

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public double Maximum
        {
            get => GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        /// <summary>Optional caption shown centred under the track (e.g. the "Vanilla" midpoint of a
        /// signed dial). Empty (default) ⇒ the label is collapsed and layout is unchanged.</summary>
        public string CenterText
        {
            get => GetValue(CenterTextProperty);
            set => SetValue(CenterTextProperty, value);
        }

        /// <summary>When true, draws integer tick marks under the slider and snaps to them. Off by
        /// default so existing sliders are unaffected.</summary>
        public bool ShowTicks
        {
            get => GetValue(ShowTicksProperty);
            set => SetValue(ShowTicksProperty, value);
        }

        static RandoSlider()
        {
            HeadingProperty.Changed.AddClassHandler<RandoSlider>((c, e) => c.OnHeadingChanged(e));
            CenterTextProperty.Changed.AddClassHandler<RandoSlider>((c, e) => c.OnCenterTextChanged(e));
            ShowTicksProperty.Changed.AddClassHandler<RandoSlider>((c, e) => c.OnShowTicksChanged(e));
        }

        public RandoSlider()
        {
            InitializeComponent();
        }

        private void OnHeadingChanged(AvaloniaPropertyChangedEventArgs e)
        {
            // headingLabel is generated; null-guard in case a property is set before the content
            // tree is built (defensive — the host sets these after construction).
            if (headingLabel is not null)
                headingLabel.IsVisible = !string.IsNullOrEmpty(e.NewValue as string);
        }

        private void OnCenterTextChanged(AvaloniaPropertyChangedEventArgs e)
        {
            if (centerLabel is not null)
                centerLabel.IsVisible = !string.IsNullOrEmpty(e.NewValue as string);
        }

        private void OnShowTicksChanged(AvaloniaPropertyChangedEventArgs e)
        {
            if (slider is null)
                return;
            var show = e.NewValue is true;
            slider.TickPlacement = show ? TickPlacement.BottomRight : TickPlacement.None;
            slider.IsSnapToTickEnabled = show;
        }

        private void Slider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
            => ValueChanged?.Invoke(this, e);
    }
}
