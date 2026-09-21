namespace ImageSyncApp;

public enum SourceKind
{
    FileSystem,
    Shell
}

public sealed record SavedSource(SourceKind Kind, string Location);

public sealed record SourceSelection(SourceKind Kind, string DisplayPath, object? ShellFolder = null)
{
    public static SourceSelection FileSystem(string path) =>
        new(SourceKind.FileSystem, FileSyncService.NormalizeDirectory(path));

    public static SourceSelection Shell(string displayPath, object? shellFolder = null) =>
        new(SourceKind.Shell, displayPath, shellFolder);

    public static SourceSelection FromConfig(AppConfig config) =>
        config.Source.Kind == SourceKind.Shell
            ? Shell(config.Source.Location)
            : FileSystem(config.Source.Location);

    public SavedSource ToSavedSource() => new(Kind, DisplayPath);
}

public sealed record AppConfig(int Version, SavedSource Source, string DestinationDirectory)
{
    public const int CurrentVersion = 2;

    public static AppConfig Default { get; } = new(CurrentVersion, new SavedSource(SourceKind.FileSystem, ""), "");
}

public sealed record ConfigLoadResult(AppConfig Config, string? Warning = null);

public sealed record ShellSourceFile(object Item, string[] SafeRelativeSegments, long? ExpectedSize);

public sealed record SyncProgress(string Message, int Current, int Total);

public sealed record SyncFailure(string SourcePath, string Message);

public sealed record SyncResult(int Total, int Copied, int Skipped, int Failed, IReadOnlyList<SyncFailure> Failures);
