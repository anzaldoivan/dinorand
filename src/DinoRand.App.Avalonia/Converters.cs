using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace DinoRand.App
{
    /// <summary>
    /// Maps a <c>null</c> brush to <see cref="AvaloniaProperty.UnsetValue"/> so a binding reproduces the
    /// WPF code-behind's <c>ClearValue(...)</c> semantics: when the view-model exposes a concrete brush the
    /// target uses it, and when it exposes <c>null</c> the target falls back to its themed default instead
    /// of being forced to a null brush. Used for every dynamically-coloured label/border the old code-behind
    /// either set to a fixed colour or cleared (seed border, progress / install-status / validation labels).
    /// </summary>
    public sealed class NullToUnsetConverter : IValueConverter
    {
        public static readonly NullToUnsetConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value ?? AvaloniaProperty.UnsetValue;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => AvaloniaProperty.UnsetValue;
    }
}
