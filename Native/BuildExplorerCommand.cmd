@echo off
setlocal

if /I not "%~1"=="x64" (
  echo FluenityHubExplorerCommand currently supports x64 packages only.
  exit /b 1
)

set "root=%~dp0.."
set "source=%~dp0FluenityHub.ExplorerCommand"
set "output=%source%\x64\%~2"
set "llvm=%root%\.tools\llvm-23.1.0\LLVM"
set "msvc=%root%\.tools\msvc-14.44\Contents\VC\Tools\MSVC\14.44.35207"
set "sdk=%root%\.tools\native-sdk\microsoft.windows.sdk.cpp\10.0.28000.2526\c"
set "sdkx64=%root%\.tools\native-sdk\microsoft.windows.sdk.cpp.x64\10.0.28000.2526\c"
set "sdkversion=10.0.28000.0"

dotnet restore "%~dp0ExplorerCommandSdk.csproj" --nologo -v:minimal
if errorlevel 1 exit /b %errorlevel%

if exist "%llvm%\bin\clang-cl.exe" goto local_toolchain

set "vswhere=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%vswhere%" (
  echo Install the Visual Studio Desktop C++ tools or provide the project-local LLVM toolchain under .tools.
  exit /b 1
)
for /f "usebackq tokens=*" %%i in (`"%vswhere%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "vsdir=%%i"
if not defined vsdir (
  echo Install the Visual Studio Desktop C++ tools or provide the project-local LLVM toolchain under .tools.
  exit /b 1
)
call "%vsdir%\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b %errorlevel%
msbuild "%source%\FluenityHub.ExplorerCommand.vcxproj" /t:Build /p:Configuration=%~2 /p:Platform=x64 /v:minimal
exit /b %errorlevel%

:local_toolchain
if not exist "%output%" mkdir "%output%"
if errorlevel 1 exit /b %errorlevel%

"%llvm%\bin\clang-cl.exe" /nologo /c /std:c++20 /W4 /WX /O2 /GR- /GS- /Zl /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /DNOMINMAX /imsvc"%msvc%\include" /imsvc"%sdk%\Include\%sdkversion%\shared" /imsvc"%sdk%\Include\%sdkversion%\um" /imsvc"%sdk%\Include\%sdkversion%\ucrt" /Fo"%output%\ExplorerCommand.obj" "%source%\ExplorerCommand.cpp"
if errorlevel 1 exit /b %errorlevel%

"%sdk%\bin\%sdkversion%\x64\rc.exe" /nologo /I"%sdk%\Include\%sdkversion%\shared" /I"%sdk%\Include\%sdkversion%\um" /fo "%output%\ExplorerCommand.res" "%source%\ExplorerCommand.rc"
if errorlevel 1 exit /b %errorlevel%

"%llvm%\bin\lld-link.exe" /dll /entry:DllMain /nodefaultlib /machine:x64 /subsystem:windows /export:DllCanUnloadNow /export:DllGetClassObject /out:"%output%\FluenityHubExplorerCommand.dll" "%output%\ExplorerCommand.obj" "%output%\ExplorerCommand.res" /libpath:"%sdkx64%\um\x64" kernel32.lib ole32.lib shell32.lib shlwapi.lib advapi32.lib uuid.lib
exit /b %errorlevel%