using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace DinoRand.App
{
    /// <summary>
    /// Interaction logic for GameLocationBox.xaml
    /// </summary>
    /// <remarks>
    /// Adapted from BioRand's GameLocationBox: the executable-picker ComboBox is dropped
    /// because DC1 ships a single game folder. Picks a folder (or an exe inside it) and
    /// raises <see cref="Validate"/> so the host can confirm the path holds room files.
    /// </remarks>
    public partial class GameLocationBox : UserControl
    {
        public event EventHandler Changed;
        public event EventHandler<PathValidateEventArgs> Validate;

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(GameLocationBox));

        public static readonly DependencyProperty LocationProperty =
            DependencyProperty.Register(nameof(Location), typeof(string), typeof(GameLocationBox));

        /// <summary>
        /// Flag to know if the settings are completely loaded. Used to avoid triggering a save when we are loading the configs.
        /// </summary>
        public bool IsSettingsLoaded { get; set; }

        public string Header
        {
            get => (string)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public string Location
        {
            get => (string)GetValue(LocationProperty);
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

        private void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            dialog.Title = $"Select {Header} game location";
            if (Directory.Exists(txtGameDataLocation.Text))
                dialog.InitialDirectory = txtGameDataLocation.Text;
            dialog.Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*";
            dialog.CheckFileExists = false;
            dialog.CheckPathExists = false;
            var window = Window.GetWindow(this);
            if (dialog.ShowDialog(window) == true)
            {
                ValidatePath(dialog.FileName);
            }
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
