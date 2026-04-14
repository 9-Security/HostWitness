@echo off
REM HostWitness release entrypoint (official). Runs dotnet via cmd.exe, not PowerShell script host.
REM Usage:
REM   publish.cmd                      restore, build, test, publish to Release\, optional sign
REM   publish.cmd -SkipPublish         restore, build, test only
REM   publish.cmd -StableGate          run InvokeStableReleaseGate.ps1 first; then publish unless -SkipPublish
REM   publish.cmd -Runtime win-arm64  target RID (default win-x64)
REM   publish.cmd -FrameworkDependent  single-file but requires .NET 8 Desktop on target
REM   publish.cmd -SkipTests           skip test step (for gate -SkipTests; default runs tests)
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
if errorlevel 1 exit /b 1
set "ROOT=%CD%"
set "NUGET_APPDATA=%ROOT%\.nuget-appdata"

set "RUNTIME=win-x64"
set "FW_DEP=0"
set "SKIP_PUBLISH=0"
set "SKIP_TESTS=0"
set "STABLE_GATE=0"

:argloop
if "%~1"=="" goto :argsdone
if /i "%~1"=="-SkipPublish" set "SKIP_PUBLISH=1" & shift & goto :argloop
if /i "%~1"=="-SkipTests" set "SKIP_TESTS=1" & shift & goto :argloop
if /i "%~1"=="-StableGate" set "STABLE_GATE=1" & shift & goto :argloop
if /i "%~1"=="-FrameworkDependent" set "FW_DEP=1" & shift & goto :argloop
if /i "%~1"=="-SingleFile" shift & goto :argloop
if /i "%~1"=="-Runtime" (
  if "%~2"=="" (
    echo publish.cmd: ERROR -Runtime requires a value (e.g. win-x64^).
    exit /b 1
  )
  set "RUNTIME=%~2"
  shift
  shift
  goto :argloop
)
echo publish.cmd: ERROR unknown argument: %~1
exit /b 1

:argsdone
REM Use repo-relative paths for dotnet (cwd is %ROOT% after cd /d %%~dp0). Matches working: dotnet restore .\WinDFIR.sln
if not exist "WinDFIR.sln" (
  echo publish.cmd: ERROR WinDFIR.sln not found in "%CD%"
  exit /b 1
)
if not exist "WinDFIR.UI\WinDFIR.UI.csproj" (
  echo publish.cmd: ERROR WinDFIR.UI\WinDFIR.UI.csproj not found in "%CD%"
  exit /b 1
)
if not exist "%NUGET_APPDATA%\NuGet" mkdir "%NUGET_APPDATA%\NuGet" >nul 2>nul
if not exist "%NUGET_APPDATA%\NuGet" (
  echo publish.cmd: ERROR could not prepare local NuGet appdata under "%NUGET_APPDATA%\NuGet".
  exit /b 1
)
set "APPDATA=%NUGET_APPDATA%"

if "%STABLE_GATE%"=="1" goto :run_gate

:call_restore_build_test
echo [publish.cmd] build/test chain via repo-local APPDATA ...
set "BUILD_CHAIN=set APPDATA=%NUGET_APPDATA% & mkdir %NUGET_APPDATA%\NuGet 2>nul & cd /d %ROOT% & del /q WinDFIR.UI\obj\*.tmp 2>nul & del /q WinDFIR.Tests\obj\*.tmp 2>nul & dotnet restore WinDFIR.UI\WinDFIR.UI.csproj -v minimal --disable-parallel && dotnet restore WinDFIR.Tests\WinDFIR.Tests.csproj -v minimal --disable-parallel && dotnet build WinDFIR.sln -c Release --no-restore -v minimal"
if not "%SKIP_TESTS%"=="1" set "BUILD_CHAIN=%BUILD_CHAIN% && dotnet test WinDFIR.sln -c Release --no-restore"
cmd.exe /d /c "%BUILD_CHAIN%"
if errorlevel 1 (
  echo publish.cmd: ERROR build/test chain failed (dotnet exit %ERRORLEVEL%^).
  echo Tips: close processes locking obj/bin files; retry after a clean restore.
  exit /b 1
)
if "%SKIP_PUBLISH%"=="1" (
  echo [publish.cmd] -SkipPublish: done.
  exit /b 0
)
goto :do_publish_sign_exit

:run_gate
echo [publish.cmd] Stable gate: scripts\InvokeStableReleaseGate.ps1 ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\InvokeStableReleaseGate.ps1"
set "GE=!ERRORLEVEL!"
if not "!GE!"=="0" (
  echo publish.cmd: ERROR stable gate failed (exit !GE!^).
  exit /b 1
)
if "%SKIP_PUBLISH%"=="1" (
  echo [publish.cmd] Gate OK; -SkipPublish: done.
  exit /b 0
)
echo [publish.cmd] Gate OK; publishing...

:do_publish_sign_exit
set "PUBLISH_ARGS=-c Release -o Release -r %RUNTIME% -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=none --no-restore"
if not "%FW_DEP%"=="1" set "PUBLISH_ARGS=%PUBLISH_ARGS% --self-contained true"
echo [publish.cmd] publish chain via repo-local APPDATA ...
set "PUBLISH_CHAIN=set APPDATA=%NUGET_APPDATA% & mkdir %NUGET_APPDATA%\NuGet 2>nul & cd /d %ROOT% & rd /s /q Release 2>nul & mkdir Release & del /q WinDFIR.UI\obj\*.tmp 2>nul & dotnet restore WinDFIR.UI\WinDFIR.UI.csproj -r %RUNTIME% -v minimal --disable-parallel && dotnet publish WinDFIR.UI\WinDFIR.UI.csproj %PUBLISH_ARGS% && if exist Release\HostWitness.exe (exit /b 0) else (exit /b 3)"
cmd.exe /d /c "%PUBLISH_CHAIN%"
set "PE=!ERRORLEVEL!"
if not "!PE!"=="0" (
  if "!PE!"=="3" (
    echo publish.cmd: ERROR HostWitness.exe missing under Release after publish.
  ) else (
    echo publish.cmd: ERROR publish chain failed (dotnet exit !PE!^).
  )
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\Sign-HostWitness.ps1" -RepoRoot "%ROOT%"
echo [publish.cmd] Done. Output: "%CD%\Release"
exit /b 0
