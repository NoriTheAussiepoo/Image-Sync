@echo off
set "APP_EXE=%~dp0ImageSyncApp\bin\Release\net10.0-windows\win-x64\publish\ImageSyncApp.exe"

if exist "%APP_EXE%" (
    start "" "%APP_EXE%"
) else (
    dotnet run --project "%~dp0ImageSyncApp\ImageSyncApp.csproj"
)
