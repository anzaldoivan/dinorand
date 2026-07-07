using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace DinoRand.App
{
    /// <summary>
    /// Interaction logic for RandoSlider.xaml
    /// </summary>
    public partial class RandoSlider : UserControl
    {
        public static readonly DependencyProperty HeadingProperty =
           DependencyProperty.Register(nameof(Heading), typeof(string), typeof(RandoSlider), new PropertyMetadata("Heading", OnHeadingChanged));

        public static readonly DependencyProperty LowTextProperty =
            DependencyProperty.Register(nameof(LowText), typeof(string), typeof(RandoSlider), new PropertyMetadata("Low"));

        public static readonly DependencyProperty HighTextProperty =
            DependencyProperty.Register(nameof(HighText), typeof(string), typeof(RandoSlider), new PropertyMetadata("High"));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(RandoSlider));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RandoSlider));

        public static readonly DependencyProperty CenterTextProperty =
            DependencyProperty.Register(nameof(CenterText), typeof(string), typeof(RandoSlider),
                new PropertyMetadata("", OnCenterTextChanged));

        public static readonly DependencyProperty ShowTicksProperty =
            DependencyProperty.Register(nameof(ShowTicks), typeof(bool), typeof(RandoSlider),
                new PropertyMetadata(false, OnShowTicksChanged));

        public event RoutedPropertyChangedEventHandler<double> ValueChanged;

        public string Heading
        {
            get => (string)GetValue(HeadingProperty);
            set => SetValue(HeadingProperty, value);
        }

        public string LowText
        {
            get => (string)GetValue(LowTextProperty);
            set => SetValue(LowTextProperty, value);
        }

        public string HighText
        {
            get => (string)GetValue(HighTextProperty);
            set => SetValue(HighTextProperty, value);
        }

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        /// <summary>Optional caption shown centred under the track (e.g. the "Vanilla" midpoint of a
        /// signed dial). Empty (default) ⇒ the label is collapsed and layout is unchanged.</summary>
        public string CenterText
        {
            get => (string)GetValue(CenterTextProperty);
            set => SetValue(CenterTextProperty, value);
        }

        /// <summary>When true, draws integer tick marks under the slider and snaps to them. Off by
        /// default so existing sliders are unaffected.</summary>
        public bool ShowTicks
        {
            get => (bool)GetValue(ShowTicksProperty);
            set => SetValue(ShowTicksProperty, value);
        }

        public RandoSlider()
        {
            InitializeComponent();
        }

        private static void OnHeadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RandoSlider slider)
            {
                slider.headingLabel.Visibility = string.IsNullOrEmpty((string)e.NewValue) ?
                    Visibility.Collapsed :
                    Visibility.Visible;
            }
        }

        private static void OnCenterTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RandoSlider slider)
            {
                slider.centerLabel.Visibility = string.IsNullOrEmpty((string)e.NewValue) ?
                    Visibility.Collapsed :
                    Visibility.Visible;
            }
        }

        private static void OnShowTicksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RandoSlider slider && e.NewValue is bool show)
            {
                slider.slider.TickPlacement = show
                    ? System.Windows.Controls.Primitives.TickPlacement.BottomRight
                    : System.Windows.Controls.Primitives.TickPlacement.None;
                slider.slider.IsSnapToTickEnabled = show;
            }
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ValueChanged?.Invoke(this, e);
        }
    }
}
