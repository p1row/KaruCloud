@echo off
REM Bigger exe (~70 MB) but runs without installing any .NET runtime.
dotnet publish KaruCloud.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist-selfcontained
if errorlevel 1 (echo. & echo BUILD FAILED & pause & exit /b 1)
echo.
echo Done: dist-selfcontained\KaruCloud.exe
pause
