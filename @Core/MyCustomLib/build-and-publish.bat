@echo off
chcp 65001 >nul
echo ========================================
echo 自定义 DLL 编译和发布脚本
echo ========================================
echo.

set PROJECT_NAME=MyCustomLib
set ASSEMBLY_NAME=ZC.MyCustomLib
set TARGET_DIR=..\..\-\libs\Core

echo [1/4] 清理旧的编译文件...
if exist bin\Release rmdir /s /q bin\Release
if exist obj rmdir /s /q obj

echo [2/4] 编译项目 (Release 模式)...
dotnet build -c Release
if %errorlevel% neq 0 (
    echo 编译失败！
    pause
    exit /b 1
)

echo [3/4] 复制 DLL 到 libs 目录...
if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%"
copy /Y "bin\Release\net10.0\%ASSEMBLY_NAME%.dll" "%TARGET_DIR%\"
copy /Y "bin\Release\net10.0\%ASSEMBLY_NAME%.pdb" "%TARGET_DIR%\"

echo [4/4] 验证文件...
if exist "%TARGET_DIR%\%ASSEMBLY_NAME%.dll" (
    echo ✓ DLL 文件已成功复制
) else (
    echo ✗ DLL 文件复制失败
)

if exist "%TARGET_DIR%\%ASSEMBLY_NAME%.pdb" (
    echo ✓ PDB 文件已成功复制
) else (
    echo ✗ PDB 文件复制失败
)

echo.
echo ========================================
echo 编译和发布完成！
echo ========================================
echo.
echo DLL 位置: %TARGET_DIR%\%ASSEMBLY_NAME%.dll
echo PDB 位置: %TARGET_DIR%\%ASSEMBLY_NAME%.pdb
echo.

pause
