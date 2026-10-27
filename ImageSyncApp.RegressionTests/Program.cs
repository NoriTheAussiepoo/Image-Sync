using ImageSyncApp;

var tests = new (string Name, Func<Task> Run)[]
{
    ("filesystem sync preserves structure and skips existing files", SyncPreservesStructureAndSkipsExistingAsync),
    ("filesystem sync rejects a nested destination", SyncRejectsNestedDestinationAsync),
    ("configuration migrates legacy settings and writes version two", ConfigMigratesAndWritesV2Async),
    ("filesystem sync throttles progress and reports completion", SyncThrottlesProgressAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL: {test.Name}\n{ex}");
    }
}

return failures.Count == 0 ? 0 : 1;

static Task SyncPreservesStructureAndSkipsExistingAsync()
{
    using var workspace = new TemporaryWorkspace();
    var source = workspace.CreateDirectory("source");
    var destination = workspace.CreateDirectory("destination");
    File.WriteAllText(Path.Combine(source, "new.jpg"), "new");
    Directory.CreateDirectory(Path.Combine(source, "album", "2026"));
    File.WriteAllText(Path.Combine(source, "album", "2026", "nested.jpg"), "nested");
    File.WriteAllText(Path.Combine(destination, "new.jpg"), "old");

    var result = new FileSyncService().Sync(SourceSelection.FileSystem(source), destination);

    AssertEqual(1, result.Copied);
    AssertEqual(1, result.Skipped);
    AssertEqual(0, result.Failed);
    AssertEqual("old", File.ReadAllText(Path.Combine(destination, "new.jpg")));
    AssertEqual("nested", File.ReadAllText(Path.Combine(destination, "album", "2026", "nested.jpg")));
    return Task.CompletedTask;
}

static Task SyncRejectsNestedDestinationAsync()
{
    using var workspace = new TemporaryWorkspace();
    var source = workspace.CreateDirectory("source");
    var nestedDestination = workspace.CreateDirectory("source", "destination");

    var exception = AssertThrows<InvalidOperationException>(
        () => new FileSyncService().Sync(SourceSelection.FileSystem(source), nestedDestination));
    AssertTrue(exception.Message.Contains("outside of the source", StringComparison.Ordinal));
    return Task.CompletedTask;
}

static async Task ConfigMigratesAndWritesV2Async()
{
    using var workspace = new TemporaryWorkspace();
    var configPath = Path.Combine(workspace.Root, "config.json");
    await File.WriteAllTextAsync(configPath, "{\"SourceDirectory\":\"C:\\\\photos\",\"DestinationDirectory\":\"C:\\\\backup\",\"SourceKind\":\"FileSystem\"}");
    var store = new AppConfigStore(configPath);

    var migrated = await store.ReadAsync();
    AssertEqual(AppConfig.CurrentVersion, migrated.Config.Version);
    AssertEqual(SourceKind.FileSystem, migrated.Config.Source.Kind);
    AssertEqual("C:\\photos", migrated.Config.Source.Location);

    await store.SaveAsync(migrated.Config);
    var persisted = await File.ReadAllTextAsync(configPath);
    AssertTrue(persisted.Contains("\"version\": 2", StringComparison.Ordinal));
    AssertTrue(persisted.Contains("\"source\"", StringComparison.Ordinal));
}

static Task SyncThrottlesProgressAsync()
{
    using var workspace = new TemporaryWorkspace();
    var source = workspace.CreateDirectory("source");
    var destination = workspace.CreateDirectory("destination");
    const int fileCount = 1000;
    for (var index = 0; index < fileCount; index++)
    {
        File.WriteAllText(Path.Combine(source, $"{index}.jpg"), "x");
    }

    var reports = new List<SyncProgress>();
    var result = new FileSyncService().Sync(SourceSelection.FileSystem(source), destination, new RecordingProgress(reports));

    AssertEqual(fileCount, result.Copied);
    AssertTrue(reports.Count is > 1 and <= 202);
    AssertEqual(fileCount, reports[^1].Current);
    return Task.CompletedTask;
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void AssertTrue(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Assertion failed.");
    }
}

static TException AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

// Records synchronously, unlike Progress<T>, which posts to the thread pool when there is no UI context.
sealed class RecordingProgress(List<SyncProgress> reports) : IProgress<SyncProgress>
{
    public void Report(SyncProgress value) => reports.Add(value);
}

sealed class TemporaryWorkspace : IDisposable
{
    public TemporaryWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "ImageSyncApp.RegressionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string CreateDirectory(params string[] segments)
    {
        var path = Path.Combine([Root, .. segments]);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
