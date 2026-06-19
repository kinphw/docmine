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
⑤ 비교       CompareTab       변경 전/후 HWP(X) 본문 diff — 좌우 대조 + 단어 하이라이트
⑥ 설정       SettingsTab      DB 접속 / 스캔 예외 폴더
```

### 문서 비교 (CompareTab)

변경내용추적을 쓰지 않는 환경에서 두 버전 사이에 무엇이 바뀌었는지 확인하기 위한 도구.
변경 전/후 파일을 고르면 두 가지 방식으로 비교한다.

- **구조 비교(기본)** — 문단/표 단위로 정렬해 변경 "목록"을 위치와 함께 보여준다.
  "제2조 적용범위 › 문단 5 수정", "표 2행2열 5년→10년" 처럼. 문단 정렬은 DiffPlex
  로 LCS 정렬 후 수정/추가/삭제 판정, 수정 문단은 단어 단위 인라인 하이라이트,
  표는 셀 격자 비교.
- **평문 좌우대조** — 본문 텍스트를 라인 단위로 좌우 대조 + 인라인 하이라이트(동기 스크롤).

입력: 「찾아보기」 또는 **박스에 파일을 드래그**(한 박스에 2개를 떨구면 전/후 한꺼번에).

결과 저장(「결과 저장…」):
- **HWP / HWPX** — 변경점을 글자색(빨강=삭제, 초록=추가)으로 입힌 리포트 문서를 한/글 COM 으로 생성.
- **TXT** — 같은 내용을 평문으로.

동작 원리:

- 추출·리포트 생성은 기존 `--hwp-worker` 를 재사용 — 모든 파일 내용 읽기·COM 편집은
  워커(자식 프로세스)에서만 수행해 DRM/DLP 의 메인 프로세스 강제 종료를 피한다.
- 구조 추출 경로: 비DRM `.hwpx` 는 워커에서 ZIP 직접 파싱. 바이너리 `.hwp` / DRM 은
  COM `SaveAs(path,"HWPX","")` 로 HWPX 임시본을 만들어(정규화) 같은 ZIP 파서로 흘려보낸다.
  정규화가 실패(한/글 미지원·DLP 임시본 재암호화 등)하면 **평문 비교로 자동 폴백**하고 사유를 알린다.
- 인라인(라인 내부) 비교는 **문자 단위**(DiffPlex CharacterChunker) — 한국어 어절이 길어
  단어 단위 하이라이트가 거친 문제를 보완. ("전 직원"→"전 임직원" 에서 "임" 만 강조)
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
