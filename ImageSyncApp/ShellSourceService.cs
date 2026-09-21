using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace ImageSyncApp;

public static class ShellSourceService
{
    // CSIDL_DRIVES is the Windows Shell namespace for "This PC".
    private const int ThisPcShellFolder = 17;
    private static readonly TimeSpan CopyCompletionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CopyStabilityInterval = TimeSpan.FromMilliseconds(500);

    [Flags]
    private enum ShellCopyOptions
    {
        Silent = 4,
        NoConfirmation = 512,
        NoErrorUi = 1024
    }

    public static SourceSelection? BrowseForSource(Window owner)
    {
        dynamic shell = CreateShellApplication();
        var ownerHandle = new WindowInteropHelper(owner).Handle;
        dynamic? folder = shell.BrowseForFolder(unchecked((int)ownerHandle), "Choose the directory to copy from.", 0, ThisPcShellFolder);

        if (folder is null)
        {
            return null;
        }

        var fileSystemPath = TryGetFolderPath(folder);
        return !string.IsNullOrWhiteSpace(fileSystemPath) && Directory.Exists(fileSystemPath)
            ? SourceSelection.FileSystem(fileSystemPath)
            : SourceSelection.Shell(GetDisplayPath(folder), folder);
    }

    public static object? ResolveFolder(SourceSelection selection)
    {
        if (selection.ShellFolder is not null)
        {
            return selection.ShellFolder;
        }

        return selection.Kind == SourceKind.Shell && !string.IsNullOrWhiteSpace(selection.DisplayPath)
            ? ResolveByDisplayPath(selection.DisplayPath)
            : null;
    }

    public static int CountFiles(SourceSelection selection)
    {
        try
        {
            var folder = ResolveFolder(selection);
            return folder is null ? 0 : EnumerateFiles(folder).Count();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static IEnumerable<ShellSourceFile> EnumerateFiles(object shellFolder) =>
        EnumerateFiles(shellFolder, Array.Empty<string>());

    public static void CopyItemToDirectory(object shellItem, string targetDirectory)
    {
        dynamic shell = CreateShellApplication();
        dynamic? destinationFolder = shell.NameSpace(targetDirectory);
        if (destinationFolder is null)
        {
            throw new IOException($"Windows could not open the destination folder: {targetDirectory}");
        }

        destinationFolder.CopyHere(shellItem, (int)(ShellCopyOptions.Silent | ShellCopyOptions.NoConfirmation | ShellCopyOptions.NoErrorUi));
    }

    public static bool WaitForCopiedFile(string targetPath, long? expectedSize)
    {
        var deadline = DateTime.UtcNow + CopyCompletionTimeout;
        long? previousLength = null;
        DateTime previousWriteTime = default;

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(targetPath))
            {
                var info = new FileInfo(targetPath);
                var expectedSizeReached = expectedSize is null || info.Length == expectedSize.Value;
                var isStable = previousLength == info.Length && previousWriteTime == info.LastWriteTimeUtc;

                if (expectedSizeReached && isStable && CanOpenForRead(targetPath))
                {
                    return true;
                }

                previousLength = info.Length;
                previousWriteTime = info.LastWriteTimeUtc;
            }

            Thread.Sleep(CopyStabilityInterval);
        }

        return false;
    }

    private static IEnumerable<ShellSourceFile> EnumerateFiles(object shellFolder, IReadOnlyList<string> relativeSegments)
    {
        dynamic folder = shellFolder;
        dynamic items = folder.Items();
        var count = (int)items.Count;

        for (var index = 0; index < count; index++)
        {
            dynamic item = items.Item(index);
            string name = GetItemName(item);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string safeName = SanitizeSegment(name);
            string[] childSegments = relativeSegments.Concat(new[] { safeName }).ToArray();
            if (IsFolder(item))
            {
                object childFolder = item.GetFolder;
                foreach (var child in EnumerateFiles(childFolder, childSegments))
                {
                    yield return child;
                }
            }
            else
            {
                yield return new ShellSourceFile(item, childSegments, TryGetItemSize(item));
            }
        }
    }

    private static object? ResolveByDisplayPath(string displayPath)
    {
        var segments = displayPath
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !segment.Equals("This PC", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (segments.Length == 0)
        {
            return null;
        }

        dynamic shell = CreateShellApplication();
        dynamic? currentFolder = shell.NameSpace(ThisPcShellFolder);
        if (currentFolder is null)
        {
            return null;
        }

        foreach (var segment in segments)
        {
            dynamic items = currentFolder.Items();
            object? nextFolder = null;
            var count = (int)items.Count;

            for (var index = 0; index < count; index++)
            {
                dynamic item = items.Item(index);
                if (IsFolder(item) && GetItemName(item).Equals(segment, StringComparison.OrdinalIgnoreCase))
                {
                    nextFolder = item.GetFolder;
                    break;
                }
            }

            if (nextFolder is null)
            {
                return null;
            }

            currentFolder = nextFolder;
        }

        return currentFolder;
    }

    private static object CreateShellApplication()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Windows Shell integration is not available.");
        return Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows Shell integration could not be started.");
    }

    private static string TryGetFolderPath(dynamic folder)
    {
        try
        {
            return (string?)folder.Self.Path ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string GetDisplayPath(dynamic folder)
    {
        var path = TryGetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(path) && !path.StartsWith("::", StringComparison.Ordinal))
        {
            return path;
        }

        var title = TryGetFolderTitle(folder);
        return string.IsNullOrWhiteSpace(title) ? path : $"This PC\\{title}";
    }

    private static string TryGetFolderTitle(dynamic folder)
    {
        try
        {
            return (string?)folder.Title ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string GetItemName(dynamic item)
    {
        try
        {
            return (string?)item.Name ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static long? TryGetItemSize(dynamic item)
    {
        try
        {
            return Convert.ToInt64(item.Size);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsFolder(dynamic item)
    {
        try
        {
            return (bool)item.IsFolder;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CanOpenForRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string SanitizeSegment(string segment)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "_" : cleaned;
    }
}
