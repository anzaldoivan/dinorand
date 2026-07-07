#nullable enable
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DinoRand.App.Services
{
    /// <summary>
    /// <see cref="IDialogs"/> implemented with a small hand-rolled modal <see cref="Window"/> (see
    /// <see cref="MessageDialog"/>). Deliberately dependency-free: the MsBox.Avalonia package would pull
    /// in a nightly DialogHost.Avalonia, which we don't want in a release artifact for three simple dialogs.
    /// </summary>
    public sealed class AvaloniaDialogs : IDialogs
    {
        private readonly Window _owner;

        public AvaloniaDialogs(Window owner) => _owner = owner;

        public Task<bool> ConfirmAsync(string title, string message)
            => MessageDialog.ShowAsync(_owner, title, message, showCancel: true);

        public Task ShowInfoAsync(string title, string message)
            => MessageDialog.ShowAsync(_owner, title, message, showCancel: false);

        public Task ShowErrorAsync(string title, string message)
            => MessageDialog.ShowAsync(_owner, title, message, showCancel: false);
    }

    /// <summary>
    /// A minimal modal message window built in code (no XAML/generator). Returns <c>true</c> on OK and
    /// <c>false</c> on Cancel (or Esc); an info/error dialog has only an OK button so it always resolves true.
    /// </summary>
    internal static class MessageDialog
    {
        public static Task<bool> ShowAsync(Window owner, string title, string message, bool showCancel)
        {
            var window = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                ShowInTaskbar = false,
                MinWidth = 320,
                MaxWidth = 560,
            };

            var text = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16),
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(16, 0, 16, 16),
            };

            if (showCancel)
            {
                var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84 };
                cancel.Click += (_, _) => window.Close(false);
                buttons.Children.Add(cancel);
            }

            // OK is the default action (Enter). When there's no Cancel button, OK also handles Esc.
            var ok = new Button { Content = "OK", IsDefault = true, IsCancel = !showCancel, MinWidth = 84 };
            ok.Click += (_, _) => window.Close(true);
            buttons.Children.Add(ok);

            window.Content = new StackPanel { Children = { text, buttons } };
            return window.ShowDialog<bool>(owner);
        }
    }
}
