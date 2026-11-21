@echo off
echo ========================================================
echo  UniBackup Pro .NET - Unified Single-File Native Release
echo ========================================================

set OUTPUT_DIR=dist\UniBackup_Release
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"

echo Publishing Unified UniBackup.exe (GUI + Headless CLI + Windows Service)...
dotnet publish src\UniBackup.UI\UniBackup.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%OUTPUT_DIR%"

echo Copying default configuration assets...
if exist "..\dist\all configs\config.json" copy "..\dist\all configs\config.json" "%OUTPUT_DIR%\"
if exist "..\dist\all configs\jobs.json" copy "..\dist\all configs\jobs.json" "%OUTPUT_DIR%\"
if exist "..\dist\all configs\gdrive_credentials.json" copy "..\dist\all configs\gdrive_credentials.json" "%OUTPUT_DIR%\"
if exist "..\dist\all configs\gdrive_token.json" copy "..\dist\all configs\gdrive_token.json" "%OUTPUT_DIR%\"

echo Copying PowerShell UniBackup Module...
if exist "modules\UniBackup" (
    mkdir "%OUTPUT_DIR%\modules\UniBackup"
    copy "modules\UniBackup\*" "%OUTPUT_DIR%\modules\UniBackup\"
)

echo ========================================================
echo  Unified Single-File Release Built: %OUTPUT_DIR%\UniBackup.exe
echo ========================================================
pause
