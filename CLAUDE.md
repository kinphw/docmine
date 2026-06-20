# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 프로젝트 개요

DocMine — HWP / HWPX / PDF 문서를 드라이브 전체에서 재귀 스캔해 본문을 파싱하고, MariaDB 에 적재한 뒤 GUI 로 전문 검색하는 Windows 데스크톱 도구. 0.5.0 부터 Python(0.4.3) → **C# (.NET 8 / WinForms)** 포트로 전환. 소스 곳곳의 클래스 주석이 대응하는 Python 모듈(`docmine/*.py`)을 명시한다 (1:1 포팅). Python 히스토리는 `python-legacy` 브랜치 / `v0.4.3-python` 태그.

## 빌드 · 실행

```powershell
dotnet build DocMine.sln                                       # 개발 빌드 (전체 솔루션)
dotnet publish src/DocMine.UI/DocMine.UI.csproj -c Release -p:SelfContained=false   # FDD 단일 exe (기본 배포 형태)
pwsh -File make_release_cs.ps1                                 # publish + zip → release/docmine_v<version>.zip
```

- **VSCode**: `Ctrl+Shift+B` = 기본 빌드 태스크("fdd 빌드", 위 publish 와 동일). `F5` = `DocMine.UI` 구성으로 GUI 디버그.
- publish 후 `Target=CopyExeToRoot` 가 `docmine.exe` 를 솔루션 루트로 복사 → 루트에서 바로 실행 가능. (루트 exe 실행 중이면 덮어쓰기 실패하지만 빌드는 계속.)
- **테스트 프로젝트는 아직 없다** (`tests/` 부재). Core 의 `Diff/`·`Db/SearchService` 는 COM/GUI 무의존으로 설계돼 단위 테스트 대상이지만 미작성 상태.

## 가장 중요한 아키텍처 두 가지

future Claude 가 가장 먼저 알아야 할, 여러 파일을 읽어야 드러나는 핵심 두 가지:

### 1. 단일 바이너리 다중 모드 (Python `multiprocessing` 패턴 이식)

산출물은 **하나의 exe** 이고, `args` 에 따라 역할이 갈린다 ([Program.cs](src/DocMine.UI/Program.cs)):

| args | 모드 | 진입점 |
|------|------|--------|
| (없음) / GUI | 메인 GUI | `Application.Run(new MainForm())` |
| `--hwp-worker` | HWP/HWPX 워커 (STDIO 루프) | [HwpWorkerEntry.Run](src/DocMine.UI/Worker/HwpWorkerEntry.cs) |
| `--pdf-worker` | PDF 워커 (STDIO 루프) | [PdfWorkerEntry.Run](src/DocMine.UI/Worker/PdfWorkerEntry.cs) |

워커 spawn 은 항상 `Environment.ProcessPath`(자기 자신 재실행)라 **exe 파일명과 무관하게 동작**한다. 보조 바이너리는 없다. Python 의 `mp.Process(target=worker_main)` 가 같은 인터프리터를 spawn 하던 패턴과 1:1.

**개발 산출물은 `docmine.exe`(`AssemblyName=docmine`), 배포 zip 은 `python.exe` 로 rename** ([make_release_cs.ps1](make_release_cs.ps1)). 이유: 운영 환경 DRM 솔루션(Fasoo·MarkAny 등)이 **프로세스 basename 화이트리스트**로 파일 I/O 를 판정하기 때문. 보호 PDF 접근이 `python.exe` 여야 허용됨. 이 제약은 임의로 바꾸지 말 것.

### 2. DRM/DLP 안전 불변식 — 본문 읽기는 워커에서만

**메인 GUI 프로세스는 보호 파일의 *내용* 을 절대 읽지 않는다.** 메타데이터(존재·크기·mtime)만 메인에서 다루고, 실제 본문 read(PdfPig, HWPX ZIP 직접 읽기, 한/글 COM)는 **격리된 자식 워커 프로세스**에서만 수행한다. raw read 횟수가 DLP 임계에 걸리면 메인이 **silent 종료**되는 운영 회귀가 실제로 있었기 때문 (worker 가 죽어도 메인 GUI 는 생존). 새 코드에서 메인 측에 파일 내용을 읽는 경로를 추가하지 말 것 — 워커 op 로 위임한다.

**워커 STDIO 프로토콜** (line-delimited JSON, [HwpWorkerClient](src/DocMine.UI/Worker/HwpWorkerClient.cs) ↔ [HwpWorkerEntry](src/DocMine.UI/Worker/HwpWorkerEntry.cs)):
- 부모가 `RedirectStandardInput/Output=true` 로 Process.Start → 자식 WinExe 의 `Console.In/Out` 이 부모 pipe 에 자동 연결 (AllocConsole 불필요).
- ops: `parse`(평문 추출) · `parse2`(구조화 추출, 문서 비교용) · `report`(비교결과 색상 HWP/HWPX 생성) · `quit`.
- 동기 `ReadLine` 블로킹 → 호출자는 반드시 백그라운드 스레드에서 호출.
- 워커 stdout EOF = 프로세스 사망(DRM/DLP 강제 종료 가능성)으로 간주해 예외.
- 생존 보장: GUI 모드 시작 시 [JobObject.SetupKillOnClose](src/DocMine.Win32/JobObject.cs) 로 부모 사망 시 모든 워커 동반 종료.

## 프로젝트 구조 (3 프로젝트)

```
src/
  DocMine.Core/    도메인 라이브러리 — GUI·COM 무의존, 단위 테스트 대상
    Config/        UserSettings(%APPDATA%\DocMine\settings.json) + AppConfig + LegacyEnvImporter(.env 1회 마이그레이션)
    Scanning/      DriveScanner — 드라이브 재귀 + CSV writer (HWP/PDF 분리)
    Hwp/           HwpxZipReader / SectionParser — HWPX(ZIP) 직접 파싱 + DRM 감지
    Pdf/           PdfTextExtractor — PdfPig 텍스트 추출
    Db/            DocumentRepository(연결·DDL·진단) + SearchService(WHERE 빌더·스니펫·쿼리)
    Diff/          문서 비교 엔진 — DiffPlex 래핑, COM 무의존 (DocumentComparer / DocStructure / ReportBuilder)
    Pipeline/      HwpInsertRunner / PdfInsertRunner — CSV → 파싱 → DB 적재
  DocMine.Win32/   Win32 P/Invoke — JobObject, FileClipboard, Drives
  DocMine.UI/      WinForms GUI + 워커 진입점 (단일 binary). Tabs/ 에 기능별 탭, Worker/ 에 COM·워커 로직
```

`DocMine.Core` 는 UI 를 참조하지 않는다. 비교 엔진(`Diff/`)도 COM 무의존 — COM 정규화(`SaveAs` → HWPX)는 워커([HwpComExtractor](src/DocMine.UI/Worker/HwpComExtractor.cs))가 담당하고 결과 구조체만 Core 로 흘려보낸다. **새 도메인 로직은 Core 에, GUI/COM 의존은 UI 에** 두는 경계를 지킬 것.

## 파이프라인 (GUI 탭 = 처리 단계)

[MainForm](src/DocMine.UI/MainForm.cs) 의 좌측 그룹 네비가 각 [Tabs/](src/DocMine.UI/Tabs/) 인스턴스를 전환(상태 유지). 단계:

1. **스캔** ([ScanTab](src/DocMine.UI/Tabs/ScanTab.cs) / DriveScanner) — 드라이브 재귀 → CSV (HWP/PDF 별도)
2. **적재** ([InsertTab](src/DocMine.UI/Tabs/InsertTab.cs)) — HWP: [HwpInsertRunner](src/DocMine.Core/Pipeline/HwpInsertRunner.cs)(한/글 COM) · PDF: [PdfInsertRunner](src/DocMine.Core/Pipeline/PdfInsertRunner.cs)(**워커 프로세스 N개=논리 CPU, 라운드로빈 분배, 메인은 직렬 DB INSERT**)
3. **검색** ([SearchTab](src/DocMine.UI/Tabs/SearchTab.cs) / SearchService) — DB 전문 검색 (제목/본문, AND/OR/구문, 적재일·ID 범위)
4. **반출** ([DbExportTab](src/DocMine.UI/Tabs/DbExportTab.cs)) — 검색결과 메타데이터 CSV / manifest 대조
5. **문서 추출** ([ExtractorTab](src/DocMine.UI/Tabs/ExtractorTab.cs)) — 선택 파일 본문 → 단일 TXT (HWP 워커 재사용)
6. **문서 비교** ([CompareTab](src/DocMine.UI/Tabs/CompareTab.cs)) — 변경 전/후 HWP(X) diff, 화면 변경추적(삭제=빨강·취소선 / 추가=초록·밑줄)
7. **DB 설정** ([SettingsTab](src/DocMine.UI/Tabs/SettingsTab.cs)) — DB 접속 / 스캔 예외 폴더

장기 작업 탭은 [IBusyTab](src/DocMine.UI/Tabs/IBusyTab.cs) 구현 → 창 닫을 때 `RequestStop()` + polling 으로 안전 종료.

DB 적재 SQL 은 `ON DUPLICATE KEY UPDATE` (재적재 시 갱신). 본문 없는 PDF 는 error 가 아니라 `parse_status='empty'` 로 기록(스캔본/이미지 PDF 구분). 스키마는 기존 Python 판과 호환(변경 없음).

## 문서 비교 (CompareTab) 동작 원리

비DRM `.hwpx` 는 워커가 ZIP 직접 파싱. 바이너리 `.hwp` / DRM 은 COM `SaveAs(path,"HWPX","")` 로 HWPX 임시본을 만들어(정규화) 같은 ZIP 파서로 흘려보낸다. **정규화 실패 시 평문 비교로 자동 폴백**하고 사유를 알림. 인라인 비교는 **문자 단위**(DiffPlex CharacterChunker — 한국어 어절이 길어 단어 단위가 거친 문제 보완). 변경 과다 시 색칠 예산 초과 시점에 색칠만 멈춰 UI 멈춤 방지(비교는 [취소] 가능). 결과는 색상 입힌 HWP/HWPX(COM) 또는 마커 TXT(`[-삭제-]` `{+추가+}`)로 저장.

## 컨벤션 · 환경

- **TFM** `net8.0-windows`, **Nullable enable**, **C# latest**, `ImplicitUsings enable`, `DebugType=embedded` — 공통값은 [Directory.Build.props](Directory.Build.props) 에서 상속. `UseWindowsForms` 는 UI 프로젝트만 켠다(Core/Win32 는 끔).
- **버전 SSOT**: [Directory.Build.props](Directory.Build.props) 의 `<Version>` 단일 소스 (About 다이얼로그·zip 파일명 자동 반영). 릴리스 시 `Version` + `FileVersion`/`AssemblyVersion` 같이 bump.
- **주석은 한국어**, `NeutralLanguage=ko-KR`. 기존 코드의 주석 밀도·어조에 맞출 것.
- `PublishTrimmed` 사용 불가 (WinForms + dynamic COM 비호환).
- 설정 파일: `%APPDATA%\DocMine\settings.json` (DB 접속 + 스캔 예외 폴더). 기존 `.env` 있으면 첫 실행 시 자동 마이그레이션.
- 요구 환경: Windows 10/11 x64 · .NET 8 Desktop Runtime · 한/글(HWP 적재·추출 시) · MariaDB/MySQL.
- 주요 의존: `MySqlConnector`(pymysql 대체) · `PdfPig`(PDF 추출, iText 대비 채택) · `DiffPlex`(비교 엔진) — 모두 순수 매니지드(네이티브 dll 없음)라 FDD 단일 exe 방침에 부합.
