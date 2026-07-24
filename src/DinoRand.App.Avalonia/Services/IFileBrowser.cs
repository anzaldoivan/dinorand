#nullable enable
using System.Diagnostics;
using System.IO;

namespace DinoRand.App.Services
{
    /// <summary>Opens a generated file or directory in its operating-system file manager.</summary>
    public interface IFileBrowser
    {
        bool TryOpen(string filePath);
    }

    /// <summary>Uses the native file manager, selecting files where the platform supports it and
    /// opening directories directly. It never opens a generated file in an editor.</summary>
    public sealed class OsFileBrowser : IFileBrowser
    {
        public bool TryOpen(string filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (Directory.Exists(fullPath))
                return TryOpenDirectory(fullPath);

            var folder = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(folder))
                return false;

            if (OperatingSystem.IsWindows())
            {
                var select = new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"/select,\"{fullPath}\"",
                    UseShellExecute = true,
                };
                if (TryStart(select))
                    return true;
            }
            else if (OperatingSystem.IsMacOS())
            {
                var select = new ProcessStartInfo("open") { UseShellExecute = false };
                select.ArgumentList.Add("-R");
                select.ArgumentList.Add(fullPath);
                if (TryStart(select))
                    return true;
            }
            else
            {
                var openFolder = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                openFolder.ArgumentList.Add(folder);
                if (TryStart(openFolder))
                    return true;
            }

            return TryStart(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }

        private static bool TryOpenDirectory(string directory)
        {
            if (OperatingSystem.IsWindows())
            {
                if (TryStart(new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"\"{directory}\"",
                    UseShellExecute = true,
                }))
                    return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                var open = new ProcessStartInfo("open") { UseShellExecute = false };
                open.ArgumentList.Add(directory);
                if (TryStart(open))
                    return true;
            }
            else if (!OperatingSystem.IsWindows())
            {
                var openFolder = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                openFolder.ArgumentList.Add(directory);
                if (TryStart(openFolder))
                    return true;
            }

            return TryStart(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }

        private static bool TryStart(ProcessStartInfo startInfo)
        {
            try
            {
                using var process = Process.Start(startInfo);
                return process is not null;
            }
            catch { return false; }
        }
    }
}
