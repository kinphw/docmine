# DocMine C# 배포 zip 생성 스크립트.
#
# Python make_release.py 의 C# 등가. PowerShell.
#
# 빌드:
#     pwsh -File make_release_cs.ps1
#
# 수행:
#   1. Directory.Build.props 에서 <Version> 읽기
#   2. dotnet publish DocMine.UI (FDD, single file) — 단일 python.exe
#      (HwpWorker 는 별도 binary 없음. python.exe 가 --hwp-worker 모드로 자가 spawn)
#   3. release/docmine_v<버전>/ 에 stage: python.exe + README.md
#   4. release/docmine_v<버전>.zip 으로 압축
#
# 배포 zip 사용법:
#   - zip 풀고 python.exe 더블클릭 = 끝
#   - 첫 실행 시 ⑤ 설정 탭에서 DB 정보 입력 → 자동 저장
#   - 기존 .env 가 있으면 자동 마이그레이션됨

[CmdletBinding()]
param(
    [switch]$SkipBuild  # 이미 publish 한 결과를 재패키징만
)

$ErrorActionPreference = 'Stop'

$Root        = Split-Path -Parent $MyInvocation.MyCommand.Path
$PropsFile   = Join-Path $Root 'Directory.Build.props'
$UICsproj    = Join-Path $Root 'src\DocMine.UI\DocMine.UI.csproj'
$PublishDir  = Join-Path $Root 'src\DocMine.UI\bin\Release\net8.0-windows\win-x64\publish'
$ReleaseRoot = Join-Path $Root 'release'

# 1) 버전 ─────────────────────────────────────────────────────────
[xml]$props = Get-Content $PropsFile
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw "Directory.Build.props 에서 <Version> 을 찾을 수 없습니다." }
Write-Host "  버전: $version" -ForegroundColor Cyan

# 2) publish ─────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "  publish 중..." -ForegroundColor Cyan
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
    & dotnet publish $UICsproj -c Release -p:SelfContained=false --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패 (exit $LASTEXITCODE)" }
}

# 3) 검증 ─────────────────────────────────────────────────────────
$pythonExe   = Join-Path $PublishDir 'python.exe'
if (-not (Test-Path $pythonExe)) { throw "산출물 없음: $pythonExe" }
$pythonSize = [math]::Round((Get-Item $pythonExe).Length / 1MB, 2)
Write-Host "  python.exe : $pythonSize MB"

# 4) stage 디렉토리 구성 ────────────────────────────────────────
$stageName = "docmine_v$version"
$stageDir  = Join-Path $ReleaseRoot $stageName
if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Copy-Item $pythonExe $stageDir

# 동봉 문서 — 운영 환경 사용 가이드.
$readme = @"
# DocMine $version (C# 포트)

## 사용법
1. ``python.exe`` 더블클릭 → GUI 실행
2. ⑤ 설정 탭에서 DB 접속 정보 입력 → 다른 필드 클릭 시 자동 저장
3. 첫 실행 시 ``%APPDATA%\DocMine\settings.json`` 생성
4. 기존 ``.env`` 가 있으면 자동 마이그레이션됨

## 폴더 안 파일
- ``python.exe``    유일한 binary (GUI + HWP 워커 모드 통합)
                    HWP 적재/추출 시 자기 자신을 --hwp-worker 모드로 자동 spawn
                    (Python mp.Process 패턴 1:1)

## 요구 환경
- Windows 10/11 x64
- .NET 8 Desktop Runtime (FDD 배포)
- 한/글 (HWP 적재/추출 시 필요)
- MariaDB / MySQL (적재/검색)

## 설정 위치
- ``%APPDATA%\DocMine\settings.json`` — DB 접속 + 스캔 예외 폴더

## 진단 (PDF 적재 silent crash 발생 시)
1. 작업 폴더의 ``pdf_crash_trace.log`` 마지막 ``>>>`` 줄 확인 → 죽인 파일의
   path / size / feat (JBIG2/JPX/ENC/XFA/FORM) / producer 메타 확인.
2. Windows Error Reporting 자동 덤프 활성화 (관리자 cmd, 운영 PC 1회):
``````
reg add "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\python.exe" /v DumpFolder /t REG_EXPAND_SZ /d "%LOCALAPPDATA%\CrashDumps" /f
reg add "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\python.exe" /v DumpType /t REG_DWORD /d 2 /f
``````
   crash 후 ``%LOCALAPPDATA%\CrashDumps\python.exe.*.dmp`` 가 생성됨.
   덤프의 faulting module 이 ``f_sps.dll`` → Fasoo DRM 충돌,
   ``coreclr.dll`` → .NET OOM/StackOverflow.

## 빌드
``pwsh -File make_release_cs.ps1``
"@
Set-Content -Path (Join-Path $stageDir 'README.md') -Value $readme -Encoding UTF8

# 5) zip 압축 ────────────────────────────────────────────────────
$zipPath = Join-Path $ReleaseRoot "$stageName.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)

Write-Host ""
Write-Host "  완료" -ForegroundColor Green
Write-Host "    stage : $stageDir"
Write-Host "    zip   : $zipPath  ($zipSize MB)"
