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
        private readonly IDialogs _dialogs;

        /// <summary>Guards the re-entrant close: without it, cancelling the close and calling
        /// <see cref="Window.Close"/> again livelocks (AvaloniaUI#15533).</summary>
        private bool _forceClose;

        public MainWindow()
        {
            InitializeComponent();

            _dialogs = new AvaloniaDialogs(this);
            _vm = new MainWindowViewModel(
                new AvaloniaFilePicker(this),
                _dialogs,
                () => Clipboard);
            _vm.PieDataChanged += UpdateItemPie;
            DataContext = _vm;

            UpdateItemPie();   // initial render (the VM raised PieDataChanged before we subscribed)
        }

        // --- Shutdown (GUI-SHUTDOWN-AND-CANCEL-PLAN.md S1) ---------------------

        /// <summary>
        /// Warn before closing while an install or an Archipelago session is live — the window
        /// closing does NOT close the game, so a silent exit can leave someone playing with no
        /// checks reaching the multiworld, or leave the game's Data\ folder half-overlaid.
        ///
        /// <para>Avalonia's <c>Closing</c> is <b>synchronous</b>: the window closes at the first
        /// <c>await</c> even with <c>e.Cancel = true</c> (AvaloniaUI#4070/#14748). So this uses the
        /// sanctioned re-entrant pattern — cancel the close, await the dialog, then set
        /// <see cref="_forceClose"/> and close again (#15307). The cancellation of the running work
        /// is fire-and-forget: blocking the UI thread on it deadlocks (#8782).</para>
        /// </summary>
        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);

            if (_forceClose || !_vm.ShouldConfirmClose)
            {
                _vm.CancelRunningWork();   // clean disconnect / state save on the way out
                return;
            }

            e.Cancel = true;
            if (!await _dialogs.ConfirmAsync("Close DinoRand?", _vm.CloseConfirmMessage))
                return;                    // user chose to stay — session and install keep running

            _forceClose = true;
            _vm.CancelRunningWork();
            Close();
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
