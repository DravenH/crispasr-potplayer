@echo off
rem Compile the shim and the setup installer with .NET Framework csc (no VS needed).
setlocal
pushd "%~dp0.."
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" ( echo [x] .NET Framework 4.x csc.exe not found & popd & exit /b 1 )

"%CSC%" -nologo -optimize+ "-win32icon:assets\app.ico" -out:"bin\whisper-faster.exe" "src\XxlShim.cs" || ( popd & exit /b 1 )
"%CSC%" -nologo -optimize+ -target:winexe "-win32icon:assets\app.ico" -out:"bin\CrispASR-PotPlayer-Setup.exe" "-resource:bin\whisper-faster.exe,wf.exe" "-resource:config\shim.ini.example,ini.txt" -reference:System.Windows.Forms.dll -reference:System.Drawing.dll -reference:System.IO.Compression.dll -reference:System.IO.Compression.FileSystem.dll "src\Installer.cs" "src\Download.cs" || ( popd & exit /b 1 )
echo OK: bin\whisper-faster.exe + bin\CrispASR-PotPlayer-Setup.exe built.
popd
