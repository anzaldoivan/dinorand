using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DinoRand.App.Services;

namespace DinoRand.App
{
    /// <summary>
    /// View for the MVVM <see cref="MainWindowViewModel"/>. The code-behind is reduced to construction
    /// wiring plus the small amount of glue that genuinely can't be a binding:
    ///   • the view-model's dependencies (file picker / dialogs over <c>this</c>, the clipboard) are
    ///     supplied here and the VM is set as the <see cref="StyledElement.DataContext"/>;
    ///   • two <c>LostFocus</c> handlers — Avalonia 12 has no native event→command and we may add no
    ///     behaviours package, so a path field validates on blur via a one-line call into the VM;
    ///   • the <c>PieChart</c> bridge — that control exposes no data-binding API (and is out of scope to
    ///     modify), so the chart is rebuilt imperatively whenever the VM raises <c>PieDataChanged</c>.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();

            _vm = new MainWindowViewModel(
                new AvaloniaFilePicker(this),
                new AvaloniaDialogs(this),
                () => Clipboard);
            _vm.PieDataChanged += UpdateItemPie;
            DataContext = _vm;

            UpdateItemPie();   // initial render (the VM raised PieDataChanged before we subscribed)
        }

        // --- View glue ---------------------------------------------------------

        // Path fields validate on blur only (preserves the old LostFocus-only validation, not per-keystroke).
        private void GamePath_LostFocus(object sender, RoutedEventArgs e) => _vm.ValidateGamePath();
        private void VoicePacksRoot_LostFocus(object sender, RoutedEventArgs e) => _vm.OnVoicePacksRootChanged();

        // PieChart has no bindable data surface, so populate it from the VM's current config in code.
        private void UpdateItemPie()
        {
            var config = _vm.CurrentConfig;
            const double keyItems = 1 / 8.0;
            int totalRest = config.RatioAmmo + config.RatioHealth;
            if (totalRest == 0)
                totalRest = 1;
            double remaining = (1 - keyItems) / totalRest;

            pieItemRatios.Records.Clear();
            pieItemRatios.Records.Add(new PieChart.Record
            { Name = "Keys", Value = keyItems, Color = Colors.LightBlue });
            pieItemRatios.Records.Add(new PieChart.Record
            { Name = "Ammo", Value = config.RatioAmmo * remaining, Color = Colors.IndianRed });
            pieItemRatios.Records.Add(new PieChart.Record
            { Name = "Health", Value = config.RatioHealth * remaining, Color = Colors.MediumSeaGreen });
            pieItemRatios.Update();
        }
    }
}
