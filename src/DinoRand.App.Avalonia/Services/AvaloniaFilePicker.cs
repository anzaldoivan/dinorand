#nullable enable
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DinoRand.App.Services
{
    /// <summary>
    /// <see cref="IFilePicker"/> over Avalonia's <see cref="IStorageProvider"/>. Constructed with any
    /// control in the visual tree (normally the owning <see cref="Window"/>); the top level — and thus
    /// the storage provider — is resolved per call so it works regardless of when the picker is created.
    /// </summary>
    public sealed class AvaloniaFilePicker : IFilePicker
    {
        private readonly Visual _anchor;

        public AvaloniaFilePicker(Visual anchor) => _anchor = anchor;

        public async Task<string?> PickFileAsync(FilePickerRequest request)
        {
            var storage = TopLevel.GetTopLevel(_anchor)?.StorageProvider;
            if (storage is null)
                return null;

            var options = new FilePickerOpenOptions
            {
                Title = request.Title,
                AllowMultiple = false,
            };
            if (request.FileTypes is { Count: > 0 })
                options.FileTypeFilter = request.FileTypes
                    .Select(t => new FilePickerFileType(t.Name) { Patterns = t.Patterns.ToArray() })
                    .ToList();
            options.SuggestedStartLocation = await ResolveStartFolder(storage, request.SuggestedStartPath);

            var result = await storage.OpenFilePickerAsync(options);
            return result.Count > 0 ? result[0].TryGetLocalPath() : null;
        }

        public async Task<string?> PickFolderAsync(FolderPickerRequest request)
        {
            var storage = TopLevel.GetTopLevel(_anchor)?.StorageProvider;
            if (storage is null)
                return null;

            var options = new FolderPickerOpenOptions
            {
                Title = request.Title,
                AllowMultiple = false,
                SuggestedStartLocation = await ResolveStartFolder(storage, request.SuggestedStartPath),
            };

            var result = await storage.OpenFolderPickerAsync(options);
            return result.Count > 0 ? result[0].TryGetLocalPath() : null;
        }

        // Map a filesystem path to a start folder for the picker, ignoring a missing/blank path.
        private static async Task<IStorageFolder?> ResolveStartFolder(IStorageProvider storage, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try { return await storage.TryGetFolderFromPathAsync(path); }
            catch { return null; }
        }
    }
}
