# 보도자료 코퍼스(press) 연계 — DocMine 통합 핸드오프

> **목적**: 외부 프로젝트 `stn-crawler`가 수집·적재한 **4대 기관 보도자료 코퍼스**를 DocMine 검색에
> 통합하기 위한 인계 문서. 이 문서만 보고 DocMine 세션에서 작업을 시작할 수 있도록 작성.
>
> **작성 시점 상태**: FSC·FSS·MOEF **적재 완료**, BOK 크롤 진행 중(미적재), 임베딩은 stn-crawler 쪽에서
> 별도 진행 중(이 통합과 **무관** — 아래 §7 참조).

---

## 1. 한눈에

- `stn-crawler`(별도 프로젝트, `c:/projects/stn-crawler`)가 금융위·금감원·기재부·한국은행 **보도자료 첨부문서**를
  **파일 단위로 텍스트 추출해 MariaDB `stn_press_db.press_document`에 적재**해 둠.
- DocMine은 이 테이블을 **읽기 전용으로 참조**해서, 기존 `documents`(로컬 스캔 문서) 검색에 **보도자료를 합치면** 됨.
- **결합점은 DB 스키마 하나뿐** (코드 의존 없음). stn-crawler는 쓰기만, DocMine은 읽기만. (Sentinel↔Forge가 md 사양 하나로 결합된 것과 같은 철학.)

### ⭐ 핵심 단순화 — DocMine 입장에서 거의 공짜
- **본문 텍스트가 이미 DB(`content` 컬럼)에 추출돼 있음.** → DocMine은 **파싱·한/글 COM·워커·DRM 회피 로직이 전혀 불필요**. 그냥 DB 행을 읽어 검색만 하면 됨.
- 즉 이 통합은 DocMine의 워커/DRM 불변식을 **건드리지 않음** (파일 내용 read가 아니라 DB 텍스트 read).

---

## 2. 현재 적재 스냅샷 (작성 시점)

| 기관(source) | 문서(파일) | 게시물 | 본문有 | 본문량 | 기간 |
|---|---|---|---|---|---|
| `fsc` 금융위 | 20,885 | 14,701 | 20,658 | 74백만자 | 1998-07 ~ 2026-06 |
| `fss` 금감원 | 22,561 | 20,302 | 21,173 | 74백만자 | 1998-06 ~ 2026-06 |
| `moef` 기재부 | 25,113 | 20,754 | 23,753 | 186백만자 | 1998-02 ~ 2026-06 |
| `bok` 한국은행 | (크롤 중) | — | — | — | (미적재) |

- BOK은 크롤 완료 후 같은 테이블에 추가됨 → **DocMine 코드 변경 없이 자동 포함**(같은 스키마·source 컬럼).

---

## 3. 접속 정보

- **서버**: 로컬 MariaDB (DocMine DB와 **같은 인스턴스**, `localhost:3306`).
- **DB**: `stn_press_db`
- **읽기 전용 계정**: `pdbuser` / `1226` (stn-crawler `sql/001_schema.sql`에서 GRANT SELECT. 존재 확인됨.)
- DocMine은 자체 DB 계정으로 자기 `documents`를 쓰되, **press 조회는 같은 서버의 다른 스키마**라 동일 커넥션에서 cross-schema 쿼리 가능(해당 계정에 `stn_press_db` SELECT 권한 부여 필요) 또는 `pdbuser`로 별도 read 커넥션.

---

## 4. 스키마 — `stn_press_db.press_document` (결합 계약)

```
id             INT PK
source         ENUM('fsc','fss','moef','bok')   -- 기관
source_seq     VARCHAR(128)   -- 게시물 식별자(같은 게시물의 여러 첨부 묶기)
folder         VARCHAR(512)   -- '날짜_제목' 다운로드 폴더명
published_date DATE           -- 보도일자
post_title     VARCHAR(512)   -- 게시물 제목
file_name      VARCHAR(512)   -- 문서(첨부) 파일명
file_ext       VARCHAR(16)    -- pdf / hwpx / hwp
file_url       VARCHAR(1024)  -- 원본 다운로드 URL
content        MEDIUMTEXT     -- ★ 추출된 본문 텍스트 (검색 대상)
char_count     INT
content_hash   CHAR(64)
UNIQUE(source, source_seq, file_name)
```
- **1 행 = 1 파일(문서)**. 한 게시물(`source_seq`)에 첨부가 N개면 N행.
- 같은 문서의 확장자 중복(pdf/hwpx/hwp)은 **PDF 우선 1개만** 적재(중복 없음).
- `content`가 비었거나 NULL = 추출 실패/이미지성(소수). 검색 시 `content <> ''` 필터 권장.

### DocMine `documents` ↔ `press_document` 컬럼 매핑

| DocMine `documents` | ← `press_document` | 비고 |
|---|---|---|
| `directory` | `folder` (또는 `CONCAT('[보도자료:',source,'] ',folder)`) | source 식별 위해 prefix 권장 |
| `filename` | `file_name` | |
| `extension` | `file_ext` | |
| `file_size` | `char_count` 또는 0 | press엔 바이트크기 없음 |
| `file_mtime` | `published_date` | 보도일자를 mtime처럼 |
| `body_text` | `content` | ★ 본문 |
| `parse_status` | `'success'` (content 있으면) / `'empty'` | |
| (없음) | `source`, `post_title`, `file_url` | DocMine에 추가 컬럼/표시 고려 |

---

## 5. 통합 방식 (합의된 **1안**)

**press_document를 DocMine `documents`에 import(복사)하지 말고, 원본 테이블 유지 + 쿼리 시 UNION.**

- **권장 A: SearchService에서 cross-DB UNION** — 검색 쿼리를 `documents`와 `stn_press_db.press_document`(매핑) `UNION ALL`로 확장하고, 결과에 **출처(로컬/보도자료-기관) 라벨** 추가.
  ```sql
  SELECT id, directory, filename, body_text, parsed_at, '로컬' AS origin FROM `{documents}` WHERE ...
  UNION ALL
  SELECT id, CONCAT('[',source,'] ',folder), file_name, content, published_date,
         CONCAT('보도:',source) AS origin
    FROM stn_press_db.press_document
   WHERE content<>'' AND (<같은 검색조건>)
  ```
  - 장점: 항상 최신, 중복 없음. 단점: cross-DB SELECT 권한 필요.
- **대안 B: 별도 read 커넥션으로 두 쿼리 후 코드 병합** — SQL UNION 대신 .NET에서 두 결과 merge. 스키마 차이가 커서 매핑이 번거로우면 이쪽이 깔끔할 수 있음.

> SearchService(`src/DocMine.Core/Db/SearchService.cs`)의 WHERE 빌더·스니펫 로직을 press 컬럼에도 적용. press는 본문이 길어(특히 moef) 스니펫·페이징 그대로 유효.

---

## 6. 원본 파일 열람 (선택)

- 본문 검색은 DB만으로 충분(위). **원본 pdf/hwp 파일을 열어줘야 할 때만** 디스크 접근 필요.
- 경로: `c:/projects/stn-crawler/data/<source>/<folder>/<file_name>` (예: `.../data/fsc/2026-06-25_…/….pdf`).
- ⚠ press 원본은 **이미 텍스트화돼 DB에 있으므로**, 원본 열람은 "원문 확인용 다운로드" 정도의 부가기능. **DRM 무관**(공개 보도자료). DocMine 워커/COM 경로 재사용 불필요.
- 환경2(폐쇄망) 배포 시 raw 파일 동반 여부는 별도 결정(텍스트만으로 충분하면 raw 미동반 가능).

---

## 7. 임베딩(`press_document_chunks`)은 지금 무시해도 됨

- stn-crawler가 `press_document_chunks`(text-embedding-3-large 3072d)도 적재 중이나, **의미검색은 MariaDB 11.7+ VECTOR 기능이 필요**(현재 서버 11.6은 미지원). 이는 **stn-crawler/Sentinel 쪽에서 업그레이드 후 별도 구현** 예정.
- **DocMine은 1차로 키워드/전문검색(`content` LIKE/FULLTEXT)만** 붙이면 됨. 의미검색 재사용은 VECTOR 도입 이후 옵션으로 검토(같은 DB라 그때 `press_document_chunks` 읽어 활용 가능).

---

## 8. 불변식 / 주의

- **읽기 전용**: `stn_press_db`에 **절대 쓰지 말 것**(crawler 소유). DocMine은 SELECT만.
- **결합은 스키마뿐**: §4 컬럼이 계약. stn-crawler가 스키마 변경 시 이 문서 갱신.
- DocMine의 DRM/워커 불변식과 **무관**(press는 DB 텍스트 read).

---

## 9. DocMine 세션에서 정할 것

1. **통합 방식 A(cross-DB UNION) vs B(코드 병합)** — 권장 A.
2. **출처(origin) 표현** — 검색 결과·GUI에서 로컬문서 vs 보도자료(+기관)를 어떻게 구분/필터링할지 (예: source 필터 드롭다운).
3. **권한** — DocMine DB 계정에 `stn_press_db` SELECT 부여 vs `pdbuser` 별도 커넥션.
4. **원본 파일 열람** 지원 여부(§6).
5. (후속) BOK 추가분·press 갱신 동기화는 자동(같은 테이블) — 별도 작업 없음.

---

## 10. 첫 단계 제안

1. Settings 또는 코드에 `stn_press_db` read 접속 추가(`pdbuser`).
2. 소량 확인: `SELECT source, post_title, LEFT(content,100) FROM stn_press_db.press_document WHERE content<>'' LIMIT 5;`
3. SearchService에 press UNION(또는 병합) 1차 붙이고, 검색어로 로컬+보도자료가 함께 나오는지 확인.
4. 결과에 출처 라벨/필터 추가 → GUI 노출.

---

*연계 대상: `stn-crawler`(c:/projects/stn-crawler) — `sql/001_schema.sql`(press_document 정의), `mcp/press-mcp/src/db.ts`(동일 테이블 읽기 예시 쿼리 참고).*
