@echo off
setlocal

set PROJ=%~dp0CustomSceneCreator\CustomSceneCreator.csproj
set DIST=%~dp0Dist\CustomSceneCreator
set GAME=F:\SteamLibrary\steamapps\common\Mount ^& Blade II Bannerlord\Modules\CustomSceneCreator

echo Building Custom Bannerlord Scene Creator...
"C:\Program Files\dotnet\dotnet.exe" build "%PROJ%" -c Release -p:Platform=x64
if %ERRORLEVEL% neq 0 (
    echo.
    echo Build FAILED. Aborting.
    exit /b %ERRORLEVEL%
)

echo.
set /p COPY_TO_GAME="Build succeeded. Deploy to game Modules folder? (y/n): "
if /i not "%COPY_TO_GAME%"=="y" goto :done

echo Deploying to "%GAME%" ...
if not exist "%GAME%" mkdir "%GAME%"
xcopy /e /y /i "%DIST%\*" "%GAME%\"
if %ERRORLEVEL% neq 0 (
    echo Deploy FAILED.
    exit /b %ERRORLEVEL%
)
echo Deployed.

:done
echo.
echo Done.
endlocal
