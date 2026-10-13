@echo off
rem Builds every tool with the MSBuild that ships with the .NET Framework 4.
rem No Visual Studio needed. Output goes to bin\Release.
setlocal
set MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe
if not exist "%MSBUILD%" (
  echo .NET Framework 4 MSBuild not found at %MSBUILD%
  exit /b 1
)
"%MSBUILD%" "%~dp0BattlespireTools.sln" /p:Configuration=Release /v:minimal
