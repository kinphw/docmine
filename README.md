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
⑤ 설정       SettingsTab      DB 접속 / 스캔 예외 폴더
```

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
