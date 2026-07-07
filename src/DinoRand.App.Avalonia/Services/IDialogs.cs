#nullable enable
using System.Threading.Tasks;

namespace DinoRand.App.Services
{
    /// <summary>
    /// Framework-agnostic modal dialogs. Replaces WPF's <c>MessageBox.Show</c> so the UI can ask
    /// for confirmation or surface info/errors without binding to a concrete toolkit.
    /// </summary>
    public interface IDialogs
    {
        /// <summary>OK / Cancel prompt. Returns <c>true</c> when the user accepts (OK), else <c>false</c>.
        /// Used for the irreversible-ish "Install to Game" confirmation.</summary>
        Task<bool> ConfirmAsync(string title, string message);

        /// <summary>Informational acknowledgement (single OK button).</summary>
        Task ShowInfoAsync(string title, string message);

        /// <summary>Error acknowledgement (single OK button).</summary>
        Task ShowErrorAsync(string title, string message);
    }
}
