using System.Collections.ObjectModel;
using System.IO;

namespace ImageSyncApp;

public sealed class FileSyncService
{
    private const int MaximumReportedFailures = 10;

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

        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        var failures = new List<SyncFailure>();
        var copied = 0;
        var skipped = 0;
        var failed = 0;

        for (var index = 0; index < sourceFiles.Count; index++)
        {
            var sourceFile = sourceFiles[index];
            var relativePath = Path.GetRelativePath(source, sourceFile);
            var targetPath = Path.Combine(destination, relativePath);
            progress?.Report(new SyncProgress($"Checking {index + 1} of {sourceFiles.Count}", index + 1, sourceFiles.Count));

            if (File.Exists(targetPath))
            {
                skipped++;
                continue;
            }

            try
            {
                var targetDirectory = Path.GetDirectoryName(targetPath)
                    ?? throw new IOException("Could not determine the destination folder.");
                Directory.CreateDirectory(targetDirectory);
                File.Copy(sourceFile, targetPath, overwrite: false);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failed++;
                AddFailure(failures, sourceFile, ex.Message);
            }
        }

        return new SyncResult(sourceFiles.Count, copied, skipped, failed, new ReadOnlyCollection<SyncFailure>(failures));
    }

    private static SyncResult SyncShellSource(SourceSelection sourceSelection, string destinationDirectory, IProgress<SyncProgress>? progress)
    {
        var destination = NormalizeDirectory(destinationDirectory);
        EnsureDestinationIsUsable(destination);
        Directory.CreateDirectory(destination);

        var sourceFolder = ShellSourceService.ResolveFolder(sourceSelection)
            ?? throw new InvalidOperationException("Reselect the source folder. Windows could not reopen that device folder.");
        var sourceFiles = ShellSourceService.EnumerateFiles(sourceFolder).ToList();
        var failures = new List<SyncFailure>();
        var copied = 0;
        var skipped = 0;
        var failed = 0;

        for (var index = 0; index < sourceFiles.Count; index++)
        {
            var sourceFile = sourceFiles[index];
            var targetPath = Path.Combine([destination, .. sourceFile.SafeRelativeSegments]);
            progress?.Report(new SyncProgress($"Checking {index + 1} of {sourceFiles.Count}", index + 1, sourceFiles.Count));

            if (File.Exists(targetPath))
            {
                skipped++;
                continue;
            }

            try
            {
                var targetDirectory = Path.GetDirectoryName(targetPath)
                    ?? throw new IOException("Could not determine the destination folder.");
                Directory.CreateDirectory(targetDirectory);
                ShellSourceService.CopyItemToDirectory(sourceFile.Item, targetDirectory);

                if (ShellSourceService.WaitForCopiedFile(targetPath, sourceFile.ExpectedSize))
                {
                    copied++;
                }
                else
                {
                    failed++;
                    AddFailure(failures, string.Join(Path.DirectorySeparatorChar, sourceFile.SafeRelativeSegments), "Windows did not finish copying the file before the timeout.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                failed++;
                AddFailure(failures, string.Join(Path.DirectorySeparatorChar, sourceFile.SafeRelativeSegments), ex.Message);
            }
        }

        return new SyncResult(sourceFiles.Count, copied, skipped, failed, new ReadOnlyCollection<SyncFailure>(failures));
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
            return Directory.EnumerateFiles(normalized, "*", SearchOption.AllDirectories).Count();
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

    private static void AddFailure(List<SyncFailure> failures, string sourcePath, string message)
    {
        if (failures.Count < MaximumReportedFailures)
        {
            failures.Add(new SyncFailure(sourcePath, message));
        }
    }
}
