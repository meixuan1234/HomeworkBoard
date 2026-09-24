@echo off
REM ============================================================================
REM  作业可视化悬浮窗 · 一键发布脚本
REM
REM  功能：编译并打包出 x64 与 x86 两个单文件 exe 到 dist\ 目录。
REM  前提：已安装 .NET SDK 8.0 或更高版本。
REM  用法：把本文件放在项目根目录（与 src 同级），双击运行。
REM ============================================================================

setlocal
cd /d "%~dp0"

echo.
echo ============================================================
echo   作业可视化悬浮窗 - 发布单文件 exe
echo ============================================================
echo.

REM ---- 检查 dotnet 是否可用 ----
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 找不到 dotnet 命令。
    echo 请先安装 .NET SDK 8.0 或更高版本：https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

echo [1/3] 正在发布 x64 版本...
dotnet publish "src\HomeworkBoard.csproj" -c Release -r win-x64 -o "dist\win-x64" --nologo
if errorlevel 1 (
    echo.
    echo [错误] x64 发布失败，请查看上方错误信息。
    pause
    exit /b 1
)

echo.
echo [2/3] 正在发布 x86 版本...
dotnet publish "src\HomeworkBoard.csproj" -c Release -r win-x86 -o "dist\win-x86" --nologo
if errorlevel 1 (
    echo.
    echo [错误] x86 发布失败，请查看上方错误信息。
    pause
    exit /b 1
)

echo.
echo [3/3] 完成。产物位置：
echo   dist\win-x64\HomeworkBoard.exe   （64 位系统，推荐）
echo   dist\win-x86\HomeworkBoard.exe   （32 位系统）
echo.
echo 提示：把 exe 单独复制到桌面或 D 盘文件夹即可运行，
echo       首次启动会自动生成 settings.json 与 homework.json。
echo.
pause
endlocal
