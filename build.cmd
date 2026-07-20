@echo off
REM DocMine 개발/빌드 스크립트 진입점.
REM 사용법: build <명령>   예) build dev
REM   dev      소스에서 바로 GUI 실행 (디버그 빌드 후 기동)
REM   start    루트 docmine.exe 실행 (빌드 없이, 마지막 publish 산출물)
REM   build    솔루션 전체 개발 빌드
REM   publish  FDD 단일 exe publish → 루트에 docmine.exe 복사
REM   release  publish + zip 패키징 → release/docmine_v<version>.zip
REM 주의: 이 파일은 cp949 + CRLF 로 저장해야 한다 (cmd 배치 파서 제약).
setlocal
cd /d "%~dp0"

if "%~1"=="" goto :usage
if /i "%~1"=="dev"     goto :dev
if /i "%~1"=="start"   goto :start
if /i "%~1"=="build"   goto :build
if /i "%~1"=="publish" goto :publish
if /i "%~1"=="release" goto :release
echo [build] 알 수 없는 명령: %~1
goto :usage

:dev
dotnet run --project src\DocMine.UI\DocMine.UI.csproj
goto :eof

:start
REM 루트 exe 는 publish 산출물. 없으면 publish 먼저 안내.
if not exist docmine.exe (
    echo [build] docmine.exe 가 없습니다. 먼저 "build publish" 를 실행하세요.
    exit /b 1
)
start "" docmine.exe
goto :eof

:build
dotnet build DocMine.sln
goto :eof

:publish
REM 루트 exe 실행 중이면 덮어쓰기만 실패하고 빌드는 계속된다.
dotnet publish src\DocMine.UI\DocMine.UI.csproj -c Release -p:SelfContained=false
goto :eof

:release
pwsh -NoProfile -ExecutionPolicy Bypass -File make_release_cs.ps1
goto :eof

:usage
echo.
echo   build dev       소스에서 GUI 실행 (개발용)
echo   build start     루트 docmine.exe 실행 (빌드 없이)
echo   build build     솔루션 전체 빌드
echo   build publish   FDD 단일 exe publish
echo   build release   publish + zip 패키징
echo.
exit /b 1
