@echo off
echo Building Cinema Mode...
echo Cleaning NuGet cache and restoring packages...
dotnet nuget locals all --clear
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false

copy /y bin\Release\net8.0-windows\win-x64\publish\CinemaMode.exe .

echo.
echo Done! Your portable app is now here: %~dp0CinemaMode.exe
pause
