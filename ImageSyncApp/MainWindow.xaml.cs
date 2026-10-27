using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ImageSyncApp;

public partial class MainWindow : Window
{
    private readonly AppConfigStore _configStore = new();
    private readonly FileSyncService _syncService = new();
    private SourceSelection _sourceSelection = SourceSelection.FileSystem("");
    private AppConfig? _lastSavedConfig;
    private int _countRefreshVersion;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var loadedConfig = await _configStore.ReadAsync();
        _sourceSelection = SourceSelection.FromConfig(loadedConfig.Config);
        SourceTextBox.Text = _sourceSelection.DisplayPath;
        DestinationTextBox.Text = loadedConfig.Config.DestinationDirectory;

        if (loadedConfig.Warning is not null)
        {
            StatusTextBlock.Text = loadedConfig.Warning;
        }

        StartRefreshCounts();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        UpdateSourceSelectionFromText();
        PersistCurrentConfigAsync(reportErrors: false).GetAwaiter().GetResult();
    }

    private async void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = ShellSourceService.BrowseForSource(this);
        if (selected is null)
        {
            return;
        }

        _sourceSelection = selected;
        SourceTextBox.Text = selected.DisplayPath;
        await PersistCurrentConfigAsync();
        StartRefreshCounts();
    }

    private async void BrowseDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectDestinationFolder("Choose the directory to copy to.", DestinationTextBox.Text);
        if (selected is null)
        {
            return;
        }

        DestinationTextBox.Text = selected;
        await PersistCurrentConfigAsync();
        StartRefreshCounts();
    }

    private async void DirectoryTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, SourceTextBox))
        {
            UpdateSourceSelectionFromText();
        }

        await PersistCurrentConfigAsync();
        StartRefreshCounts();
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        UpdateSourceSelectionFromText();
        SetBusy(true);
        StatusTextBlock.Text = "Preparing to sync...";
        SyncProgressBar.Value = 0;

        try
        {
            await PersistCurrentConfigAsync();

            var sourceSelection = _sourceSelection;
            var destinationDirectory = DestinationTextBox.Text;
            var progress = new Progress<SyncProgress>(UpdateProgress);
            var result = await Task.Run(() => _syncService.Sync(sourceSelection, destinationDirectory, progress));

            StartRefreshCounts(sourceSelection.Kind == SourceKind.Shell ? result.Total : null);
            SyncProgressBar.Maximum = Math.Max(result.Total, 1);
            SyncProgressBar.Value = SyncProgressBar.Maximum;
            StatusTextBlock.Text = BuildCompletionMessage(result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Image Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusTextBlock.Text = "Sync did not finish.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string BuildCompletionMessage(SyncResult result)
    {
        var summary = $"Done. Copied {result.Copied}, skipped {result.Skipped}, failed {result.Failed}.";
        return result.Failures.Count == 0
            ? summary
            : $"{summary} First issue: {result.Failures[0].SourcePath}: {result.Failures[0].Message}";
    }

    private void UpdateProgress(SyncProgress progress)
    {
        SyncProgressBar.Maximum = Math.Max(progress.Total, 1);
        SyncProgressBar.Value = Math.Min(progress.Current, SyncProgressBar.Maximum);
        StatusTextBlock.Text = progress.Message;
    }

    private void StartRefreshCounts(int? sourceCountOverride = null)
    {
        var version = Interlocked.Increment(ref _countRefreshVersion);
        var source = _sourceSelection;
        var destinationPath = DestinationTextBox.Text;

        SourceCountTextBlock.Text = sourceCountOverride is not null
            ? $"Files found: {sourceCountOverride}"
            : source.Kind == SourceKind.Shell ? "Files found: on sync" : "Files found: counting...";
        DestinationCountTextBlock.Text = "Files found: counting...";
        _ = RefreshCountsAsync(version, source, destinationPath, sourceCountOverride);
    }

    private async Task RefreshCountsAsync(int version, SourceSelection source, string destinationPath, int? sourceCountOverride)
    {
        var sourceTask = source.Kind == SourceKind.Shell
            ? Task.FromResult(sourceCountOverride)
            : Task.Run<int?>(() => _syncService.CountSourceFiles(source));
        var destinationTask = Task.Run(() => FileSyncService.CountFiles(destinationPath));

        await Task.WhenAll(sourceTask, destinationTask);
        if (version != _countRefreshVersion)
        {
            return;
        }

        if (sourceTask.Result is int sourceCount)
        {
            SourceCountTextBlock.Text = $"Files found: {sourceCount}";
        }

        DestinationCountTextBlock.Text = $"Files found: {destinationTask.Result}";
    }

    private async Task PersistCurrentConfigAsync(bool reportErrors = true)
    {
        var config = new AppConfig(
            AppConfig.CurrentVersion,
            _sourceSelection.ToSavedSource(),
            FileSyncService.NormalizeDirectory(DestinationTextBox.Text));

        // Every focus change and the window closing call this; skip the disk write when nothing changed.
        if (config == _lastSavedConfig)
        {
            return;
        }

        _lastSavedConfig = config;
        try
        {
            await _configStore.SaveAsync(config).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastSavedConfig = null;
            if (reportErrors)
            {
                _ = Dispatcher.BeginInvoke(() => StatusTextBlock.Text = $"Could not save settings: {ex.Message}");
            }
        }
    }

    private void UpdateSourceSelectionFromText()
    {
        if (!string.Equals(SourceTextBox.Text, _sourceSelection.DisplayPath, StringComparison.OrdinalIgnoreCase))
        {
            _sourceSelection = SourceSelection.FileSystem(SourceTextBox.Text);
        }
    }

    private static string? SelectDestinationFolder(string description, string initialDirectory)
    {
        var normalized = FileSyncService.NormalizeDirectory(initialDirectory);
        var dialog = new OpenFolderDialog
        {
            Title = description,
            InitialDirectory = Directory.Exists(normalized) ? normalized : null
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        Cursor = isBusy ? System.Windows.Input.Cursors.Wait : null;
        SyncButton.IsEnabled = !isBusy;
        BrowseSourceButton.IsEnabled = !isBusy;
        BrowseDestinationButton.IsEnabled = !isBusy;
        SourceTextBox.IsEnabled = !isBusy;
        DestinationTextBox.IsEnabled = !isBusy;
    }
}
