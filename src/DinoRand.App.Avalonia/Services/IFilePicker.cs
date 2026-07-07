#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DinoRand.App.Services
{
    /// <summary>
    /// Framework-agnostic file/folder picker. The UI depends on this interface, never on a
    /// concrete dialog API (WPF's <c>Microsoft.Win32.OpenFileDialog</c> / Avalonia's
    /// <c>IStorageProvider</c>), so window logic stays portable and unit-testable with a fake.
    /// All methods return the chosen filesystem path, or <c>null</c> when the user cancels.
    /// </summary>
    public interface IFilePicker
    {
        Task<string?> PickFileAsync(FilePickerRequest request);
        Task<string?> PickFolderAsync(FolderPickerRequest request);
    }

    /// <summary>A named set of filename patterns (e.g. "Dino Crisis executable" → ["DINO.exe"]).</summary>
    public sealed record FilePickerFileFilter(string Name, IReadOnlyList<string> Patterns);

    /// <summary>
    /// Open-a-file request. <paramref name="FileTypes"/> are offered as filters (first = default);
    /// <paramref name="SuggestedStartPath"/> seeds the initial directory when it exists.
    /// </summary>
    public sealed record FilePickerRequest(
        string Title,
        IReadOnlyList<FilePickerFileFilter>? FileTypes = null,
        string? SuggestedStartPath = null);

    /// <summary>Open-a-folder request. <paramref name="SuggestedStartPath"/> seeds the initial directory.</summary>
    public sealed record FolderPickerRequest(
        string Title,
        string? SuggestedStartPath = null);
}
