@echo off
REM Small build. Needs the .NET 8 Desktop Runtime on the PC that runs it.
dotnet publish KaruCloud.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:PublishReadyToRun=true -o dist
if errorlevel 1 (echo. & echo BUILD FAILED & pause & exit /b 1)
echo.
echo Done: dist\KaruCloud.exe
pause
