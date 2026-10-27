using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ImageSyncApp;

public sealed class FileSyncService
{
    private const int MaximumReportedFailures = 10;

    // Same files as SearchOption.AllDirectories (hidden/system included), but an unreadable subfolder such as
    // "System Volume Information" on an SD card is skipped instead of aborting the whole scan.
    private static readonly EnumerationOptions RecursiveEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0
    };

    public SyncResult Sync(SourceSelection sourceSelection, string destinationDirectory, IProgress<SyncProgress>? progress = null)
    {
        return sourceSelection.Kind == SourceKind.Shell
            ? SyncShellSource(sourceSelection, destinationDirectory, progress)
            : SyncFileSystemSource(sourceSelection.DisplayPath, destinationDirectory, progress);
    }

    public int CountSourceFiles(SourceSelection sourceSelection) =>
        sourceSelection.Kind == SourceKind.Shell
            ? ShellSourceService.CountFiles(sourceSelection)
            : CountFiles(sourceSelection.DisplayPath);

    private static SyncResult SyncFileSystemSource(string sourceDirectory, string destinationDirectory, IProgress<SyncProgress>? progress)
    {
        var source = NormalizeDirectory(sourceDirectory);
        var destination = NormalizeDirectory(destinationDirectory);

        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            throw new InvalidOperationException("Choose an existing source directory.");
        }

        EnsureFileSystemDestinationIsUsable(source, destination);
        Directory.CreateDirectory(destination);

        var sourceFiles = Directory.EnumerateFiles(source, "*", RecursiveEnumeration).ToList();
        return CopyMissingFiles(
            sourceFiles,
            sourceFile => Path.Combine(destination, Path.GetRelativePath(source, sourceFile)),
            sourceFile => sourceFile,
            (sourceFile, targetPath) => File.Copy(sourceFile, targetPath, overwrite: false),
            progress);
    }

    private static SyncResult SyncShellSource(SourceSelection sourceSelection, string destinationDirectory, IProgress<SyncProgress>? progress)
    {
        var destination = NormalizeDirectory(destinationDirectory);
        EnsureDestinationIsUsable(destination);
        Directory.CreateDirectory(destination);

        var sourceFolder = ShellSourceService.ResolveFolder(sourceSelection)
            ?? throw new InvalidOperationException("Reselect the source folder. Windows could not reopen that device folder.");
        var sourceFiles = ShellSourceService.EnumerateFiles(sourceFolder).ToList();
        // One Shell instance for the whole run instead of creating a COM object per file.
        var shell = ShellSourceService.CreateShellApplication();

        return CopyMissingFiles(
            sourceFiles,
            sourceFile => Path.Combine([destination, .. sourceFile.SafeRelativeSegments]),
            sourceFile => string.Join(Path.DirectorySeparatorChar, sourceFile.SafeRelativeSegments),
            (sourceFile, targetPath) =>
            {
                ShellSourceService.CopyItemToDirectory(shell, sourceFile.Item, Path.GetDirectoryName(targetPath)!);
                if (!ShellSourceService.WaitForCopiedFile(targetPath, sourceFile.ExpectedSize))
                {
                    throw new IOException("Windows did not finish copying the file before the timeout.");
                }
            },
            progress);
    }

    /// <summary>
    /// Copies every source file whose target path does not exist yet; existing targets are skipped, never overwritten.
    /// </summary>
    private static SyncResult CopyMissingFiles<TSource>(
        IReadOnlyList<TSource> sourceFiles,
        Func<TSource, string> getTargetPath,
        Func<TSource, string> describe,
        Action<TSource, string> copy,
        IProgress<SyncProgress>? progress)
    {
        var failures = new List<SyncFailure>();
        var total = sourceFiles.Count;
        var copied = 0;
        var skipped = 0;
        var failed = 0;
        // Report roughly 200 times per run so large syncs don't flood the UI thread with one update per file.
        var reportInterval = Math.Max(1, total / 200);

        for (var index = 0; index < total; index++)
        {
            var sourceFile = sourceFiles[index];
            var current = index + 1;
            if (current == 1 || current == total || current % reportInterval == 0)
            {
                progress?.Report(new SyncProgress($"Checking {current} of {total}", current, total));
            }

            try
            {
                var targetPath = getTargetPath(sourceFile);
                if (File.Exists(targetPath))
                {
                    skipped++;
                    continue;
                }

                var targetDirectory = Path.GetDirectoryName(targetPath)
                    ?? throw new IOException("Could not determine the destination folder.");
                Directory.CreateDirectory(targetDirectory);
                copy(sourceFile, targetPath);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or ArgumentException or InvalidOperationException or COMException)
            {
                failed++;
                if (failures.Count < MaximumReportedFailures)
                {
                    failures.Add(new SyncFailure(describe(sourceFile), ex.Message));
                }
            }
        }

        return new SyncResult(total, copied, skipped, failed, new ReadOnlyCollection<SyncFailure>(failures));
    }

    public static int CountFiles(string directory)
    {
        var normalized = NormalizeDirectory(directory);
        if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(normalized, "*", RecursiveEnumeration).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return path.Trim();
        }
    }

    public static bool IsSameOrNestedPath(string parentPath, string childPath)
    {
        var parent = NormalizeDirectory(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var child = NormalizeDirectory(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child))
        {
            return false;
        }

        return parent.Equals(child, StringComparison.OrdinalIgnoreCase) ||
               child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureFileSystemDestinationIsUsable(string sourcePath, string destinationPath)
    {
        EnsureDestinationIsUsable(destinationPath);
        if (IsSameOrNestedPath(sourcePath, destinationPath))
        {
            throw new InvalidOperationException("Choose a destination outside of the source directory.");
        }
    }

    private static void EnsureDestinationIsUsable(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new InvalidOperationException("Choose a destination directory.");
        }
    }
}
