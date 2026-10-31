# Image Sync

A small Windows UI for copying new files from one folder to another.

It is meant for cases like copying Nintendo Switch screenshots, camera photos, SD card files, or flash drive files into a normal Windows directory such as a local Google Drive folder.

## How to run

Double-click `Start-ImageSync.cmd`.

The launcher opens the compiled WPF app when it has been published. During development, it falls back to `dotnet run`.

The app remembers the last source and destination in a versioned `config.json`, which is created beside the executable. Older configuration files are migrated automatically.

## Sync behavior

- Copies from the left folder to the right folder.
- Recursively scans subfolders.
- Preserves the source folder structure in the destination.
- Skips a file when the same relative file path already exists in the destination.
- Never deletes files from either folder.
- Creates the destination folder if it does not exist.
- Can use normal folders or Windows Shell device folders, such as `This PC\Nintendo Switch\Album`.
- When copying from a Shell device folder, invalid Windows filename characters in folder names are replaced with `_` in the destination.

Example:

```text
Source:
Album\2026\07\screenshot.jpg

Destination after sync:
Album\2026\07\screenshot.jpg
```

## Development

The app is a C# WPF desktop app targeting .NET 10 for Windows. Build it with:

```powershell
dotnet build ImageSyncApp\ImageSyncApp.csproj -c Release
```

Publish the executable used by the launcher with:

```powershell
.\Publish-ImageSync.ps1
```

Run the focused regression suite with:

```powershell
dotnet run --project ImageSyncApp.RegressionTests\ImageSyncApp.RegressionTests.csproj -c Release
```

## License

Copyright 2026 Hazel Saliceta.

This project is licensed under the PolyForm Noncommercial License 1.0.0. See [LICENSE](LICENSE.md) and [NOTICE](NOTICE).
