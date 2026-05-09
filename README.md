# Semiconductor FA Paper Crawler

반도체 Failure Analysis(FA) 분야 학술 논문을 11개 학술 데이터베이스에서 자동 수집하여 MSSQL에 적재하는 파이프라인입니다.

---

## 전체 파이프라인

```
[1] Python 크롤러 (crawler.py)
    20개 키워드 × 11개 소스 병렬 수집 (ThreadPoolExecutor)
         ↓
[2] CSV 파일 저장 (data/advanced_fa_papers_YYYYMMDD_HHMMSS.csv)
         ↓
[3] C# ETL 로더 (SemiconductorLoader)
    CSV 파싱 → SqlBulkCopy → MSSQL pre_table 적재
         ↓
[4] Node.js REST API + Vue 3 웹 UI
    논문 조회 · 검색 · 페이징
```

---

## 수집 필드

| 컬럼 | 설명 | 비고 |
|------|------|------|
| `url` | 논문 원문 링크 | |
| `site_name` | 수집 소스명 | |
| `keyword` | 검색 키워드 | config.json에서 정의 |
| `paper_number` | 소스 내 고유 ID | 중복 제거 기준 |
| `title` | 논문 제목 | |
| `authors` | 저자 목록 | 쉼표 구분 |
| `published_date` | 출판일 | YYYY-MM-DD |
| `doi` | DOI | 일부 소스 미지원 시 빈 문자열 |
| `abstract` | 초록 | |
| `citation_count` | 인용 횟수 | 지원 소스만 (그 외 NULL) |
| `extracted_at` | 수집 시각 | YYYY-MM-DD HH:MM:SS |

---

## 지원 데이터 소스

| 소스 | 방식 | DOI | Abstract | Citation Count | API Key 필요 |
|------|------|:---:|:--------:|:--------------:|:------------:|
| arXiv | REST API | △ | ✅ | — | — |
| Semantic Scholar | REST API | ✅ | ✅ | ✅ | — |
| OpenAlex | REST API | ✅ | ✅ | ✅ | — |
| CrossRef | REST API | ✅ | △ | ✅ | — |
| Europe PMC | REST API | ✅ | ✅ | ✅ | — |
| CORE | REST API | ✅ | ✅ | — | ✅ |
| BASE | REST API | △ | △ | — | — |
| IEEE Xplore | REST API | ✅ | ✅ | ✅ | ✅ |
| Google Scholar | scholarly 라이브러리 | △ | △ | ✅ | — |
| PLOS | REST API | ✅ | ✅ | — | — |
| DOAJ | REST API | ✅ | ✅ | — | — |

> △ = 논문에 따라 값이 없을 수 있음

---

## 설치

```bash
pip install requests urllib3 tqdm scholarly
```

또는 `start.bat`를 실행하면 의존성 자동 설치 후 크롤러가 실행됩니다.

---

## 빠른 시작

### 1. API 키 설정 (선택)

`config.json`에서 API 키를 입력합니다. 없으면 해당 소스는 자동으로 건너뜁니다.

```json
{
  "core_api_key": "YOUR_CORE_API_KEY",
  "ieee_api_key": "YOUR_IEEE_API_KEY"
}
```

### 2. 크롤러 실행

```bash
# 직접 실행
python crawler.py

# 또는 배치 파일
start.bat
```

### 3. ETL 로더 실행

`SemiconductorLoader` WPF 앱을 열고:
1. **설정** 탭에서 MSSQL 서버/DB 정보 입력
2. **모니터** 탭에서 **크롤러 + 로드** 또는 **로드만** 실행

---

## config.json 상세

```json
{
  "global_keywords": ["키워드1", "키워드2"],  // 검색 키워드 배열
  "max_results_per_source": 20,               // 소스당 최대 수집 건수
  "min_new_papers": 3,                        // 이 미만이면 CSV 저장 생략
  "timeout_sec": 60,                          // HTTP 요청 타임아웃(초)
  "max_workers": 15,                          // 병렬 스레드 수 (기본 15)
  "core_api_key": "",                         // CORE API 키
  "ieee_api_key": "",                         // IEEE Xplore API 키
  "sources": {
    "arxiv":            { "enabled": true },
    "semantic_scholar": { "enabled": true },
    "openalex":         { "enabled": true },
    "crossref":         { "enabled": true },
    "europepmc":        { "enabled": true },
    "core":             { "enabled": true },
    "base":             { "enabled": true },
    "google_scholar":   { "enabled": true, "max_results": 5 },
    "ieee_xplore":      { "enabled": true },
    "plos":             { "enabled": true },
    "doaj":             { "enabled": true }
  }
}
```

> `max_workers`는 config.json에 항목이 없으면 기본값 15가 사용됩니다.

---

## 출력 파일

```
semiconductor_crawler/
├── data/
│   └── advanced_fa_papers_20260417_143022.csv   ← 수집 결과
├── master_log.csv                                ← 중복 제거 인덱스
└── master_log.db                                 ← SQLite (참고용)
```

### 중복 제거 원리

`master_log.csv`에 `(site_name, paper_number)` 튜플을 누적 저장합니다.  
크롤러 시작 시 전체를 메모리에 로드하여, 이미 수집된 논문은 결과에서 제외합니다.

---

## MSSQL 테이블 구조

### pre_table (논문 데이터)

```sql
CREATE TABLE [dbo].[pre_table] (
    seq            INT            IDENTITY(1,1) PRIMARY KEY,
    url            NVARCHAR(1000) NULL,
    site_name      NVARCHAR(100)  NULL,
    keyword        NVARCHAR(500)  NULL,
    paper_number   NVARCHAR(200)  NULL,
    title          NVARCHAR(2000) NULL,
    authors        NVARCHAR(MAX)  NULL,
    published_date DATE           NULL,
    doi            NVARCHAR(500)  NULL,
    abstract       NVARCHAR(MAX)  NULL,
    citation_count INT            NULL,
    extracted_at   DATETIME       NULL,
    loaded_at      DATETIME       NOT NULL DEFAULT GETDATE()
);
```

### load_log (ETL 이력)

```sql
CREATE TABLE [dbo].[load_log] (
    log_id      INT            IDENTITY(1,1) PRIMARY KEY,
    file_name   NVARCHAR(500)  NOT NULL,
    total_rows  INT            NOT NULL,
    loaded_rows INT            NOT NULL,
    failed_rows INT            NOT NULL,
    status      NVARCHAR(20)   NOT NULL,   -- SUCCESS | PARTIAL | FAILED
    error_msg   NVARCHAR(MAX)  NULL,
    started_at  DATETIME       NOT NULL,
    finished_at DATETIME       NOT NULL
);
```

> 테이블이 없으면 SemiconductorLoader가 자동으로 생성합니다 (멱등).  
> 기존 테이블에 `doi`, `abstract`, `citation_count` 컬럼이 없으면 `ALTER TABLE`로 자동 추가됩니다.

---

## 웹 UI

```bash
# 백엔드
cd web/backend
npm install
node src/app.js

# 프론트엔드
cd web/frontend
npm install
npm run dev
```

| 기능 | 설명 |
|------|------|
| 논문 목록 | 페이징 / 정렬 / 필터 |
| 키워드 검색 | 제목·초록 전문 검색 |
| 소스 필터 | 수집 출처별 드롭다운 |
| 상세 팝업 | 제목·저자·초록·DOI·인용 수 |

---

## 자주 발생하는 문제

| 증상 | 원인 | 해결 |
|------|------|------|
| CORE 소스 수집 0건 | API 키 미설정 | config.json `core_api_key` 입력 |
| IEEE Xplore 수집 0건 | API 키 미설정 | config.json `ieee_api_key` 입력 |
| Google Scholar 오류 | 봇 차단 | scholarly 라이브러리 한계; `max_results: 3`으로 낮추거나 비활성화 |
| CSV 저장 생략 | `min_new_papers` 미달 | config.json `min_new_papers` 값 낮추기 |
| DB 연결 실패 | ODBC 드라이버 미설치 | "ODBC Driver 17 for SQL Server" 설치 |
