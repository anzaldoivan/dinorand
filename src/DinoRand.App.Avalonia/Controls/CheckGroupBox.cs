using System;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace DinoRand.App
{
    /// <summary>
    /// Avalonia port of the WPF <c>CheckGroupBox</c>: a <see cref="HeaderedContentControl"/> whose
    /// header carries a check box; ticking it enables the content, unticking disables it. Translation
    /// notes vs the WPF original:
    ///   • <c>DependencyProperty</c> → <see cref="StyledProperty{T}"/>.
    ///   • the <c>IsChecked</c> metadata callback + the <c>OnHeaderChanged</c> override → one
    ///     <see cref="OnPropertyChanged"/> override (Avalonia's <c>HeaderedContentControl</c> has no
    ///     <c>OnHeaderChanged</c> virtual).
    ///   • the template lives in <c>Templates/CheckGroupBox.axaml</c> (a <c>Styles</c> include), since
    ///     Avalonia has no <c>GroupBox</c> — it's a titled <c>Border</c> + overlaid <c>CheckBox</c>.
    /// </summary>
    public class CheckGroupBox : HeaderedContentControl
    {
        public event EventHandler<RoutedEventArgs> Unchecked;
        public event EventHandler<RoutedEventArgs> Checked;
        public event EventHandler<RoutedEventArgs> OnCheckedChanged;

        public static readonly StyledProperty<string> ActualHeaderProperty =
            AvaloniaProperty.Register<CheckGroupBox, string>(nameof(ActualHeader));

        public static readonly StyledProperty<bool?> IsCheckedProperty =
            AvaloniaProperty.Register<CheckGroupBox, bool?>(nameof(IsChecked), defaultValue: false);

        public static readonly StyledProperty<bool> IsChildrenEnabledProperty =
            AvaloniaProperty.Register<CheckGroupBox, bool>(nameof(IsChildrenEnabled));

        public string ActualHeader
        {
            get => GetValue(ActualHeaderProperty);
            set => SetValue(ActualHeaderProperty, value);
        }

        public bool IsChildrenEnabled
        {
            get => GetValue(IsChildrenEnabledProperty);
            set => SetValue(IsChildrenEnabledProperty, value);
        }

        public bool? IsChecked
        {
            get => GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsCheckedProperty)
            {
                var newValue = change.GetNewValue<bool?>();
                IsChildrenEnabled = newValue == true;
                if (newValue == true)
                    Checked?.Invoke(this, new RoutedEventArgs());
                if (newValue == false)
                    Unchecked?.Invoke(this, new RoutedEventArgs());
                OnCheckedChanged?.Invoke(this, new RoutedEventArgs());
            }
            else if (change.Property == HeaderProperty)
            {
                ActualHeader = "      " + change.GetNewValue<object>();
            }
        }
    }
}
