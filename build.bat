@echo off
rem Builds VisceraJapaneseInstaller.exe with the C# compiler that ships with Windows (.NET Framework 4).
setlocal EnableDelayedExpansion
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set R=/resource:payload\manifest.tsv,manifest.tsv
for %%f in (payload\patches\*) do set R=!R! /resource:%%f,patches/%%~nxf
for %%f in (payload\jpn\*) do set R=!R! /resource:%%f,jpn/%%~nxf
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /win32icon:app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /out:VisceraJapaneseInstaller.exe !R! Program.cs
if errorlevel 1 (echo Build failed.& exit /b 1)
echo Built VisceraJapaneseInstaller.exe
