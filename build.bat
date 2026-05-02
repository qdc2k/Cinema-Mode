@echo off
echo Building Cinema Mode...
echo Cleaning NuGet cache and restoring packages...
dotnet nuget locals all --clear
dotnet publish CinemaMode.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:ApplicationIcon=CinemaMode.ico

copy /y bin\Release\net8.0-windows\win-x64\publish\CinemaMode.exe .

echo.
echo Cleaning build artifacts...
rmdir /s /q bin 2>nul
rmdir /s /q obj 2>nul

echo Done! Your portable app is now here: %~dp0CinemaMode.exe
pause
