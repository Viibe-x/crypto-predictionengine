@echo off
rem Builds the self-contained Windows release of DexScannerBot into .\release
cd /d "%~dp0"
echo ==== %DATE% %TIME% ==== > build.log
where dotnet >> build.log 2>&1
dotnet --version >> build.log 2>&1
echo ==== PUBLISH ==== >> build.log
dotnet publish DexScannerBot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o release >> build.log 2>&1
echo PUBLISH_EXIT=%ERRORLEVEL% >> build.log
echo ==== TESTS ==== >> build.log
if exist tests\SignalStackEngine.Tests\SignalStackEngine.Tests.csproj (
  dotnet test tests\SignalStackEngine.Tests\SignalStackEngine.Tests.csproj -c Release >> build.log 2>&1
  call echo TEST_EXIT=%%ERRORLEVEL%% >> build.log
) else (
  echo Tests folder not found - skipped >> build.log
)
echo ==== DONE ==== >> build.log
