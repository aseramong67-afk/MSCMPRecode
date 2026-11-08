@echo off
rem ==== Сборка мода MP Recode Remastered ====
rem Таргет: Mono-профиль My Summer Car (.NET 3.5-совместимый) через ссылку на mscorlib игры
setlocal
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set GAME=E:\My Summer Car - MP Copy
set MAN=%GAME%\mysummercar_Data\Managed
rem MSCLoader 1.4.2 грузит моды ТОЛЬКО из корня Mods\
set OUT=%GAME%\Mods

if not exist "%OUT%" mkdir "%OUT%"

"%CSC%" /nologo /noconfig /nostdlib+ /target:library /platform:anycpu /w:1 /optimize+ ^
 /define:NET35 ^
 /r:"%MAN%\mscorlib.dll" ^
 /r:"%MAN%\System.dll" ^
 /r:"%MAN%\System.Core.dll" ^
 /r:"%MAN%\UnityEngine.dll" ^
 /r:"%MAN%\UnityEngine.UI.dll" ^
 /r:"%MAN%\Assembly-CSharp.dll" ^
 /r:"%MAN%\Assembly-CSharp-firstpass.dll" ^
 /r:"%MAN%\PlayMaker.dll" ^
 /r:"%MAN%\ES2.dll" ^
 /r:"%MAN%\cInput.dll" ^
 /r:"%MAN%\MSCLoader.dll" ^
 /r:"%MAN%\0Harmony.dll" ^
 /out:"%OUT%\MSCMPRecode.dll" ^
 src\*.cs

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK -^> "%OUT%\MSCMPRecode.dll"

rem ==== Лаунчер (обновление мода + запуск игры) ====
set LDIR=%~dp0dist
if not exist "%LDIR%" mkdir "%LDIR%"

"%CSC%" /nologo /target:exe /platform:anycpu /w:1 /optimize+ /out:"%LDIR%\MPRecodeLauncher.exe" launcher\Launcher.cs
if errorlevel 1 (
  echo LAUNCHER BUILD FAILED
  exit /b 1
)
echo LAUNCHER OK -^> "%LDIR%\MPRecodeLauncher.exe"

rem ==== Дистрибутив: версия + мод + лаунчер (для git/raw авто-обновления) ====
if exist version.txt (
  copy /y version.txt "%LDIR%\version.txt" >nul
  copy /y version.txt "%OUT%\MPRecode.version" >nul
) else (
  echo 0.3.0>"%LDIR%\version.txt"
)
copy /y "%OUT%\MSCMPRecode.dll" "%LDIR%\MSCMPRecode.dll" >nul
echo DIST OK -^> "%LDIR%\"
endlocal
