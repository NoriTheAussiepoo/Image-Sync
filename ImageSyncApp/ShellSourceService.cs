using System.Buffers;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace ImageSyncApp;

public static class ShellSourceService
{
    // CSIDL_DRIVES is the Windows Shell namespace for "This PC".
    private const int ThisPcShellFolder = 17;
    private const string ThisPcParsingPath = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const int MaximumFolderDepth = 64;
    private static readonly TimeSpan CopyCompletionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CopyStabilityInterval = TimeSpan.FromMilliseconds(500);
    // When the expected size is known, a matching size plus an exclusive open is strong evidence the copy is done,
    // so a shorter stability window is enough and saves ~400 ms per copied file.
    private static readonly TimeSpan KnownSizeStabilityInterval = TimeSpan.FromMilliseconds(100);
    private static readonly SearchValues<char> InvalidFileNameCharacters = SearchValues.Create(Path.GetInvalidFileNameChars());

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

    public static void CopyItemToDirectory(object shellApplication, object shellItem, string targetDirectory)
    {
        dynamic shell = shellApplication;
        dynamic? destinationFolder = shell.NameSpace(targetDirectory);
        if (destinationFolder is null)
        {
            throw new IOException($"Windows could not open the destination folder: {targetDirectory}");
        }

        destinationFolder.CopyHere(shellItem, (int)(ShellCopyOptions.Silent | ShellCopyOptions.NoConfirmation | ShellCopyOptions.NoErrorUi));
    }

    public static bool WaitForCopiedFile(string targetPath, long? expectedSize)
    {
        var pollInterval = expectedSize is null ? CopyStabilityInterval : KnownSizeStabilityInterval;
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

            Thread.Sleep(pollInterval);
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
            bool isFolder = IsFolder(item);
            // CopyHere writes the real file name, while Name can omit the extension when Explorer hides known
            // extensions, so prefer the actual file name to keep the target path in sync with the copied file.
            string name = isFolder ? GetItemName(item) : GetItemFileName(item);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string[] childSegments = [.. relativeSegments, SanitizeSegment(name)];
            if (isFolder)
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

    public static object CreateShellApplication()
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

        // Device folders have no file system path. Record every folder title up to This PC (for example
        // "This PC\Nintendo Switch\Album") so ResolveByDisplayPath can find the folder again after a restart.
        var titles = new List<string>();
        dynamic? current = folder;
        while (current is not null && titles.Count < MaximumFolderDepth)
        {
            string currentPath = TryGetFolderPath(current);
            if (currentPath.Equals(ThisPcParsingPath, StringComparison.OrdinalIgnoreCase))
            {
                if (titles.Count > 0)
                {
                    return $"This PC\\{string.Join('\\', titles)}";
                }

                break;
            }

            string currentTitle = TryGetFolderTitle(current);
            if (string.IsNullOrWhiteSpace(currentTitle))
            {
                break;
            }

            titles.Insert(0, currentTitle);
            current = TryGetParentFolder(current);
        }

        // Not under This PC: fall back to the folder's own title.
        var title = TryGetFolderTitle(folder);
        return string.IsNullOrWhiteSpace(title) ? path : $"This PC\\{title}";
    }

    private static object? TryGetParentFolder(dynamic folder)
    {
        try
        {
            return folder.ParentFolder;
        }
        catch (Exception)
        {
            return null;
        }
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

    private static string GetItemFileName(dynamic item)
    {
        try
        {
            string? fileName = item.ExtendedProperty("System.FileName") as string;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }
        catch (Exception)
        {
            // Some namespaces do not expose System.FileName; fall back to the display name.
        }

        return GetItemName(item);
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
        var cleaned = segment.AsSpan().ContainsAny(InvalidFileNameCharacters)
            ? string.Create(segment.Length, segment, static (buffer, source) =>
              {
                  for (var index = 0; index < source.Length; index++)
                  {
                      buffer[index] = InvalidFileNameCharacters.Contains(source[index]) ? '_' : source[index];
                  }
              })
            : segment;
        cleaned = cleaned.Trim();

        // "." and ".." contain no invalid characters but would escape the destination folder in Path.Combine.
        return string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or ".." ? "_" : cleaned;
    }
}
