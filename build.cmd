@echo off
rem Builds TaskbarMonitor.exe on Windows. Requires the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
rem   dist\small\TaskbarMonitor.exe       ~2 MB, needs the .NET 8 Desktop Runtime installed
rem   dist\standalone\TaskbarMonitor.exe  ~70 MB, runs anywhere, no install needed
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist\small || exit /b 1
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist\standalone || exit /b 1
echo.
echo Done. See the dist folder.
