# DocMine

HWP / HWPX / PDF 문서를 드라이브 전체에서 스캔해 본문을 파싱하고, MariaDB 에 적재한 뒤 GUI 로 키워드 검색하는 도구.

> **0.5.0 부터 C# (.NET 8) 포트로 전환되었습니다.**
> Python 0.4.3 까지의 히스토리는 `python-legacy` 브랜치 / `v0.4.3-python` 태그로 보존돼 있습니다.

## 파이프라인

```
① 스캔        DriveScanner    드라이브 재귀 스캔 → CSV (HWP/PDF 별도)
② HWP 적재   HwpInsertRunner  HWP CSV → 한/글 COM 파싱 → MariaDB
② PDF 적재   PdfInsertRunner  PDF CSV → PdfPig 워커 N개 병렬 → MariaDB
③ 검색       SearchTab        DB 전문 검색 (HWP + PDF 통합)
④ 추출       ExtractorTab     선택 파일들의 본문 → 단일 TXT
⑤ 비교       CompareTab       변경 전/후 HWP(X) diff — 화면 변경추적(취소선/밑줄)
⑥ 설정       SettingsTab      DB 접속 / 스캔 예외 폴더
```

### 문서 비교 (CompareTab)

변경내용추적을 쓰지 않는 환경에서 두 버전 사이에 무엇이 바뀌었는지 확인하기 위한 도구.
**전/후 파일을 드래그하고 [비교] 만 누르면 파일을 열 필요 없이 화면에서 바로 변경점을 본다.**
세 가지 보기를 제공한다.

- **변경추적(기본)** — 변경 전/후를 한 흐름으로 병합해 워드/한글의 "모든 변경 내용" 보기처럼
  보여준다. **삭제 = 빨강 + 취소선, 추가 = 초록 + 밑줄.** 전체 문서를 읽으며 변경점을 따라간다.
- **변경 목록** — 문단/표 단위로 변경만 위치와 함께 나열. "제2조 › 문단 5 수정", "표 2행2열 5년→7년".
- **좌우대조** — 본문 텍스트를 라인 단위로 좌우 대조 + 인라인 하이라이트(동기 스크롤).

요약줄에 **페이지 수(예: 12 → 13쪽)** 와 변경 통계를 표시(페이지 수는 COM 경로일 때).

입력: 「찾아보기」 또는 **박스에 파일을 드래그**(한 박스에 2개를 떨구면 전/후 한꺼번에).

결과 저장(「결과 저장…」):
- **HWP / HWPX** — 같은 변경추적 표기(삭제=취소선/빨강, 추가=밑줄/초록)를 입힌 한/글 문서를 COM 으로 생성.
- **TXT** — `[-삭제-]` `{+추가+}` 마커로.

동작 원리:

- 추출·리포트 생성은 기존 `--hwp-worker` 를 재사용 — 모든 파일 내용 읽기·COM 편집은
  워커(자식 프로세스)에서만 수행해 DRM/DLP 의 메인 프로세스 강제 종료를 피한다.
- 구조 추출 경로: 비DRM `.hwpx` 는 워커에서 ZIP 직접 파싱. 바이너리 `.hwp` / DRM 은
  COM `SaveAs(path,"HWPX","")` 로 HWPX 임시본을 만들어(정규화) 같은 ZIP 파서로 흘려보낸다.
  정규화가 실패(한/글 미지원·DLP 임시본 재암호화 등)하면 **평문 비교로 자동 폴백**하고 사유를 알린다.
- 인라인(라인 내부) 비교는 **문자 단위**(DiffPlex CharacterChunker) — 한국어 어절이 길어
  단어 단위 하이라이트가 거친 문제를 보완. ("전 직원"→"전 임직원" 에서 "임" 만 강조)
- 변경추적 통합본은 미변경 문단까지 전체를 담아 화면에 그린다. 변경이 과도하면(무관 문서 등)
  요약에 경고하고 색칠 예산을 넘기는 시점에서 색칠만 멈춰 UI 멈춤을 막는다. 비교는 [취소] 가능.
- 비교 엔진은 `DocMine.Core/Diff` (DiffPlex 래핑, COM 무의존) — GUI 와 분리, 단위 검증 가능.
- (후속 v3 후보) 서식/개체 변경 감지 — 현재 CharShape/ParaShape 를 안 읽으므로 별도 캡처 필요.

## 빌드

```powershell
# 개발 빌드
dotnet build DocMine.sln

# 배포 zip 생성
pwsh -File make_release_cs.ps1
# → release/docmine_v<version>.zip
```

## 요구 환경

- Windows 10 / 11 x64
- .NET 8 Desktop Runtime (FDD 배포)
- 한/글 (HWP 적재 / 추출 시)
- MariaDB / MySQL (적재 / 검색)

## 설정 위치

- `%APPDATA%\DocMine\settings.json` — DB 접속 정보 + 스캔 예외 폴더
- 기존 `.env` 가 있으면 첫 실행 시 자동 마이그레이션

## 프로젝트 구조

```
src/
  DocMine.Core/    도메인 라이브러리 (GUI 무의존)
    Config/        AppConfig + UserSettings 영속화
    Hwp/           HWPX ZIP 파싱
    Diff/          문서 비교 엔진 (DiffPlex 래핑, COM 무의존)
    Pdf/           PdfPig 텍스트 추출
    Db/            MySqlConnector Repository + 검색
    Pipeline/      스캔 / HWP 적재 / PDF 적재 runner
    Scanning/      드라이브 재귀 + CSV writer
  DocMine.Win32/   Win32 P/Invoke (JobObject, FileClipboard, Drives)
  DocMine.UI/      WinForms GUI + 워커 모드 (단일 binary)
```

빌드 결과는 `python.exe` 단일 파일이고, 같은 binary 가 args 에 따라 GUI / HWP 워커 / PDF 워커 모드로 분기됩니다 (Python `multiprocessing` 패턴 1:1 이식).

## 버전

현재 버전은 [Directory.Build.props](Directory.Build.props) 의 `<Version>` 단일 소스. About 다이얼로그·zip 파일명에 자동 반영.
