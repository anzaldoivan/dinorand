using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DinoRand.App.Services;

namespace DinoRand.App
{
    /// <summary>
    /// Avalonia port of the WPF <c>GameLocationBox</c>: a <see cref="CheckGroupBox"/> wrapping a path
    /// box + browse button that raises <see cref="Validate"/> so the host can confirm the path holds
    /// the game's room files. The browse button goes through the framework-agnostic
    /// <see cref="IFilePicker"/> (Phase 1) instead of WPF's <c>Microsoft.Win32.OpenFileDialog</c>.
    /// </summary>
    public partial class GameLocationBox : UserControl
    {
        public event EventHandler Changed;
        public event EventHandler<PathValidateEventArgs> Validate;

        public static readonly StyledProperty<string> HeaderProperty =
            AvaloniaProperty.Register<GameLocationBox, string>(nameof(Header));

        public static readonly StyledProperty<string> LocationProperty =
            AvaloniaProperty.Register<GameLocationBox, string>(nameof(Location));

        private IFilePicker _filePicker;

        /// <summary>The file picker used by Browse; defaults to the Avalonia adapter anchored on this
        /// control, but is settable so a host/test can substitute a fake.</summary>
        public IFilePicker FilePicker
        {
            get => _filePicker ??= new AvaloniaFilePicker(this);
            set => _filePicker = value;
        }

        /// <summary>True once settings are fully loaded; suppresses a save while loading configs.</summary>
        public bool IsSettingsLoaded { get; set; }

        public string Header
        {
            get => GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public string Location
        {
            get => GetValue(LocationProperty);
            set => SetValue(LocationProperty, value);
        }

        public bool? IsChecked
        {
            get => groupBox.IsChecked;
            set => groupBox.IsChecked = value;
        }

        public GameLocationBox()
        {
            InitializeComponent();
            // Wire the group's check toggle in code (no XAML CLR-event dependency).
            groupBox.OnCheckedChanged += groupBox_OnCheckedChanged;
        }

        public void ValidateNow() => ValidatePath(txtGameDataLocation.Text);

        private void ValidatePath(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    path = Path.GetDirectoryName(path);
                }

                var eventArgs = new PathValidateEventArgs();
                eventArgs.Path = path;
                Validate?.Invoke(this, eventArgs);

                txtValidationMessage.Text = eventArgs.Message;
                txtValidationMessage.Foreground = eventArgs.IsValid ?
                    Brushes.Green :
                    Brushes.Red;
            }
            catch (Exception ex)
            {
                txtValidationMessage.Text = ex.Message;
                txtValidationMessage.Foreground = Brushes.Red;
            }

            Location = path;
            InvokeChanges();
        }

        private void InvokeChanges()
        {
            if (IsSettingsLoaded)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        private async void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = await FilePicker.PickFileAsync(new FilePickerRequest(
                $"Select {Header} game location",
                FileTypes: new[]
                {
                    new FilePickerFileFilter("Executable Files", new[] { "*.exe" }),
                    new FilePickerFileFilter("All Files", new[] { "*" }),
                },
                SuggestedStartPath: Directory.Exists(txtGameDataLocation.Text) ? txtGameDataLocation.Text : null));
            if (path is not null)
                ValidatePath(path);
        }

        private void txtGameDataLocation_LostFocus(object sender, RoutedEventArgs e)
        {
            ValidatePath(txtGameDataLocation.Text);
        }

        private void groupBox_OnCheckedChanged(object sender, RoutedEventArgs e)
        {
            InvokeChanges();
        }
    }

    public class PathValidateEventArgs : EventArgs
    {
        public string Path { get; set; }
        public string Message { get; set; }
        public bool IsValid { get; set; }
    }
}
