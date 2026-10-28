@echo off
REM ---------------------------------------------------------------------------
REM Build DollarCoPos (the counter PC app: fingerprint attendance + Sales).
REM
REM Uses the C# compiler that ships with Windows (.NET Framework 4.x), so
REM nothing has to be installed to build it: no Visual Studio, no .NET SDK.
REM
REM   build.cmd                   -> bin\DollarCoPos.exe      (desktopApp.config.json: live server)
REM   build.cmd <variant>         -> bin-<variant>\                   (desktopApp.<variant>.config.json)
REM   build.cmd /installer [...]  -> ...then installer\Output\DollarCoPos-Setup.exe
REM                                  (needs Inno Setup 6: winget install JRSoftware.InnoSetup)
REM
REM build-local.cmd = build.cmd local (http://localhost:5000/, a backend on this PC).
REM A config already in the output folder is kept: it is that PC's own
REM settings (saved from the app's Settings window). Delete it to take the
REM build's config again.
REM ---------------------------------------------------------------------------

setlocal
set "HERE=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF"

set MAKE_INSTALLER=
if /i "%~1"=="/installer" (
    set MAKE_INSTALLER=1
    shift
)

set "OUTDIR=%HERE%bin"
set "CONFIG=%HERE%desktopApp.config.json"
set "EXENAME=DollarCoPos.exe"
if not "%~1"=="" (
    set "OUTDIR=%HERE%bin-%~1"
    set "CONFIG=%HERE%desktopApp.%~1.config.json"
)

if not exist "%CONFIG%" (
    echo ERROR: config file not found: "%CONFIG%"
    exit /b 1
)
if not exist "%CSC%" (
    echo ERROR: C# compiler not found at %CSC%
    echo        This PC needs the 64-bit .NET Framework 4.x ^(part of Windows 10 and 11^).
    exit /b 1
)
if not exist "%HERE%lib\DPFPDevNET.dll" (
    echo ERROR: DigitalPersona SDK DLLs are missing from "%HERE%lib".
    echo        Needed: DPFPDevNET.dll DPFPShrNET.dll DPFPEngNET.dll DPFPVerNET.dll dpHFtrEx.dll dpHMatch.dll
    exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:4 ^
    /out:"%OUTDIR%\%EXENAME%" ^
    /win32icon:"%HERE%app.ico" ^
    /resource:"%HERE%assets\logo.png",DollarCo.Logo.png ^
    /win32manifest:"%HERE%app.manifest" ^
    /reference:"%HERE%lib\DPFPDevNET.dll" ^
    /reference:"%HERE%lib\DPFPShrNET.dll" ^
    /reference:"%HERE%lib\DPFPEngNET.dll" ^
    /reference:"%HERE%lib\DPFPVerNET.dll" ^
    /reference:System.Core.dll ^
    /reference:System.Security.dll ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.Web.Extensions.dll ^
    /reference:"%WPF%\PresentationCore.dll" ^
    /reference:"%WPF%\WindowsBase.dll" ^
    /reference:"%WPF%\System.Speech.dll" ^
    "%HERE%src\*.cs"

if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

REM The app loads these at run time, so they sit beside the exe.
for %%F in (DPFPDevNET.dll DPFPShrNET.dll DPFPEngNET.dll DPFPVerNET.dll dpHFtrEx.dll dpHMatch.dll) do (
    copy /y "%HERE%lib\%%F" "%OUTDIR%\" >nul
)
if not exist "%OUTDIR%\desktopApp.config.json" copy /y "%CONFIG%" "%OUTDIR%\desktopApp.config.json" >nul
if not exist "%OUTDIR%\sounds" mkdir "%OUTDIR%\sounds"
copy /y "%HERE%sounds\*.mp3" "%OUTDIR%\sounds\" >nul

echo BUILD OK: "%OUTDIR%\%EXENAME%"
echo Settings in "%OUTDIR%\desktopApp.config.json":
type "%OUTDIR%\desktopApp.config.json"

if not "%MAKE_INSTALLER%"=="1" goto :done

REM ---------------------------------------------------------------------------
REM The installer (installer\DollarCoPos.iss): this build's exe, DLLs and
REM sounds with the build's config (never the output folder's, which may be a
REM developer's own settings).
REM ---------------------------------------------------------------------------
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC (
    echo INSTALLER NOT BUILT: Inno Setup 6 is not installed. Install it with
    echo     winget install JRSoftware.InnoSetup
    echo then run build.cmd /installer again.
    exit /b 1
)

"%ISCC%" /Q "/DBinDir=%OUTDIR%" "/DExeName=%EXENAME%" "/DConfigFile=%CONFIG%" "%HERE%installer\DollarCoPos.iss"
if errorlevel 1 (
    echo INSTALLER FAILED
    exit /b 1
)
echo INSTALLER OK: "%HERE%installer\Output\DollarCoPos-Setup.exe"

:done
endlocal
