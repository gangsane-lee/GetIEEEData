# SemiconductorLoader

반도체 분야 학술 논문 크롤러가 생성한 CSV 파일을 SQL Server로 자동 적재하는 WPF 데스크톱 애플리케이션.

---

## 목차

1. [요구 사항](#요구-사항)
2. [프로젝트 구조](#프로젝트-구조)
3. [설치 및 빌드](#설치-및-빌드)
4. [설정 파일 (`db_config.json`)](#설정-파일-db_configjson)
5. [데이터베이스 스키마](#데이터베이스-스키마)
6. [ETL 파이프라인](#etl-파이프라인)
7. [CSV 형식](#csv-형식)
8. [주요 컴포넌트](#주요-컴포넌트)
9. [아키텍처 개요](#아키텍처-개요)
10. [트러블슈팅](#트러블슈팅)

---

## 요구 사항

| 항목 | 버전 |
|------|------|
| .NET | 8.0 이상 |
| Windows | 10 / 11 (WPF) |
| SQL Server | 2017 이상 (Express 가능) |

NuGet 패키지:

| 패키지 | 버전 | 용도 |
|--------|------|------|
| `Microsoft.Data.SqlClient` | 5.2.0 | MSSQL 연결, `SqlBulkCopy` |
| `CsvHelper` | 33.0.1 | 따옴표·줄바꿈 포함 CSV 파싱 |
| `Newtonsoft.Json` | 13.0.3 | `db_config.json` 역직렬화 |

---

## 프로젝트 구조

```
SemiconductorLoader/
├── Config/
│   └── AppConfig.cs          # db_config.json 로드·저장, 경로 자동 탐색
├── Models/
│   ├── PaperRecord.cs        # CSV 1행 ↔ C# 객체 매핑
│   └── LoadLogEntry.cs       # load_log 테이블 조회 결과 모델
├── Services/
│   ├── CsvParserService.cs   # CSV → List<PaperRecord> 변환
│   ├── DatabaseService.cs    # DB 생성, 테이블 DDL, BulkInsert, 이력 조회
│   ├── LoaderService.cs      # ETL 오케스트레이터 (파일 스캔 → 파싱 → 적재 → archive)
│   └── CrawlerService.cs     # 외부 크롤러 BAT 실행 및 대기
├── ViewModels/
│   ├── MainViewModel.cs      # 메인 화면 로직 (스케줄, 수동 실행, 실시간 로그)
│   ├── SettingsViewModel.cs  # 설정 화면 (DB 연결 정보, 경로, 주기)
│   └── HistoryViewModel.cs   # 이력 조회 화면 (DataGrid, 통계)
├── Behaviors/
│   └── AutoScrollBehavior.cs # ListBox 자동 스크롤 Attached Behavior
├── MainWindow.xaml / .xaml.cs
├── App.xaml / .xaml.cs
└── SemiconductorLoader.csproj
```

---

## 설치 및 빌드

```bash
# 1. 저장소 클론 또는 zip 압축 해제
cd SemiconductorLoader

# 2. 빌드
dotnet build -c Release

# 3. 실행
dotnet run --project SemiconductorLoader
# 또는 bin/Release/net8.0-windows/SemiconductorLoader.exe 직접 실행
```

`db_config.json` 파일이 없으면 기본값으로 시작되며, 설정 화면에서 저장하면 exe 위치에 생성됩니다.

---

## 설정 파일 (`db_config.json`)

애플리케이션은 실행 파일 위치에서 상위 6단계까지 `db_config.json`을 자동으로 탐색합니다.
파일이 없으면 아래 기본값이 사용됩니다.

```json
{
  "database": {
    "server": "127.0.0.1",
    "port": 1433,
    "database": "AutoReport",
    "auth_mode": "sql",
    "username": "",
    "password": "",
    "connection_timeout": 30,
    "command_timeout": 300
  },
  "loader": {
    "scan_interval_minutes": 5,
    "batch_size": 2000,
    "data_folder": "",
    "archive_folder": "",
    "crawler_bat_path": "",
    "crawler_timeout_minutes": 30,
    "crawler_enabled": true,
    "etl_enabled": true,
    "crawler_interval_minutes": 60
  }
}
```

### 주요 항목 설명

| 키 | 기본값 | 설명 |
|----|--------|------|
| `auth_mode` | `"sql"` | `"sql"` = SQL Server 인증, `"windows"` = Windows 통합 인증 |
| `batch_size` | `2000` | SqlBulkCopy 배치 단위 (건수가 많을수록 처리 속도 향상, 메모리 증가) |
| `data_folder` | 자동 탐색 | CSV 입력 폴더. 비워두면 exe 위치에서 상위로 `data/` 폴더를 자동 탐색 |
| `archive_folder` | `data/archive` | 처리 완료 CSV 이동 경로 |
| `crawler_bat_path` | 자동 탐색 | 크롤러 실행 BAT 경로. 비워두면 상위에서 `start.bat` 자동 탐색 |
| `crawler_timeout_minutes` | `30` | 크롤러 프로세스 강제 종료 대기 시간 (분) |
| `scan_interval_minutes` | `5` | ETL 스케줄 주기 (분) |
| `crawler_interval_minutes` | `60` | 크롤러 독립 스케줄 주기 (분) |

---

## 데이터베이스 스키마

애플리케이션 최초 실행 또는 "DB 초기화" 버튼 클릭 시 테이블이 없으면 자동 생성됩니다.
기존 테이블에 신규 컬럼(`doi`, `abstract`, `citation_count`)이 없으면 `ALTER TABLE`로 자동 추가됩니다.

### `pre_table` — 논문 데이터

```sql
CREATE TABLE [dbo].[pre_table] (
    seq            INT            IDENTITY(1,1) NOT NULL,
    url            NVARCHAR(1000)               NULL,
    site_name      NVARCHAR(100)                NULL,
    keyword        NVARCHAR(500)                NULL,
    paper_number   NVARCHAR(200)                NULL,
    title          NVARCHAR(2000)               NULL,
    authors        NVARCHAR(MAX)                NULL,
    published_date DATE                         NULL,
    doi            NVARCHAR(500)                NULL,
    abstract       NVARCHAR(MAX)                NULL,
    citation_count INT                          NULL,
    extracted_at   DATETIME                     NULL,
    loaded_at      DATETIME       NOT NULL      DEFAULT GETDATE(),
    CONSTRAINT PK_pre_table PRIMARY KEY (seq)
);
```

### `load_log` — ETL 이력

```sql
CREATE TABLE [dbo].[load_log] (
    log_id       INT            IDENTITY(1,1) NOT NULL,
    file_name    NVARCHAR(500)                NOT NULL,
    total_rows   INT                          NOT NULL,
    loaded_rows  INT                          NOT NULL,
    failed_rows  INT                          NOT NULL,
    status       NVARCHAR(20)                 NOT NULL,  -- SUCCESS | PARTIAL | FAILED
    error_msg    NVARCHAR(MAX)                NULL,
    started_at   DATETIME                     NOT NULL,
    finished_at  DATETIME                     NOT NULL,
    CONSTRAINT PK_load_log PRIMARY KEY (log_id)
);
```

---

## ETL 파이프라인

```
data/*.csv
    │
    ▼  [CsvParserService]
    │  - 헤더 검사 (구버전 CSV 자동 호환)
    │  - 날짜 포맷 자동 인식: YYYY-MM-DD / YYYY-MM / YYYY
    │  - doi, abstract, citation_count 컬럼 없는 경우 null 처리
    │
    ▼  [DatabaseService.BulkInsertAsync]
    │  - SqlBulkCopy (batch_size 단위 커밋)
    │  - 진행률 이벤트 (UI 프로그레스 바 연동)
    │
    ▼  [DatabaseService.WriteLogAsync]
    │  - load_log 테이블에 처리 결과 기록
    │
    ▼  archive/YYYY-MM-DD/filename.csv
       (SUCCESS / PARTIAL 시 이동, FAILED 시 data/ 폴더에 유지)
```

### 처리 상태

| 상태 | 의미 |
|------|------|
| `SUCCESS` | 모든 행 파싱 성공 + DB 적재 완료 |
| `PARTIAL` | 일부 행 파싱 실패, 나머지 적재 완료 |
| `FAILED` | DB 연결 오류 등 전체 실패 (파일 data/ 유지, 재시도 가능) |

---

## CSV 형식

크롤러(`crawler.py`)가 생성하는 CSV 형식:

```
url,site_name,keyword,paper_number,title,authors,published_date,doi,abstract,citation_count,extracted_at
```

| 컬럼 | 타입 | 설명 |
|------|------|------|
| `url` | string | 논문 원문 URL |
| `site_name` | string | 출처 (arxiv, semantic_scholar 등) |
| `keyword` | string | 검색 키워드 |
| `paper_number` | string | 고유 식별자 (arXiv ID, DOI 등) |
| `title` | string | 논문 제목 |
| `authors` | string | 저자 목록 (쉼표 구분) |
| `published_date` | string | 발행일 (`YYYY-MM-DD` / `YYYY-MM` / `YYYY`) |
| `doi` | string | DOI (없으면 빈 값) |
| `abstract` | string | 초록 (없으면 빈 값) |
| `citation_count` | int | 인용 수 (미지원 소스는 빈 값) |
| `extracted_at` | datetime | 크롤링 시각 |

> **구버전 CSV 호환**: `keyword`, `doi`, `abstract`, `citation_count` 컬럼이 없어도 파싱 오류 없이 null로 처리됩니다.

---

## 주요 컴포넌트

### `AppConfig` (`Config/AppConfig.cs`)

- 실행 파일 위치에서 최대 7단계 상위까지 `db_config.json` 자동 탐색
- `FindPath(name, isDirectory)` 헬퍼로 파일/디렉터리 탐색 로직 통합
- `Save()` 호출 시 현재 `ConfigFilePath`에 JSON으로 저장

### `CsvParserService` (`Services/CsvParserService.cs`)

- `static readonly CsvConfiguration` 재사용으로 반복 호출 시 오버헤드 최소화
- 헤더 검사 방식으로 컬럼 유무를 먼저 확인 후 조건부 읽기 → 구버전 CSV 호환
- `published_date` 파싱 우선순위: `YYYY-MM-DD` → `YYYY-MM` → `YYYY` → `DateTime.TryParse`

### `DatabaseService` (`Services/DatabaseService.cs`)

- `OpenConnectionAsync()` / `OpenMasterConnectionAsync()` 헬퍼로 커넥션 생성 로직 중앙화
- `BulkInsertAsync`: `SqlBulkCopy` + `EnableStreaming = true` + `NotifyAfter` 진행률 콜백
- `EnsureTablesAsync()`: `IF NOT EXISTS` 멱등 DDL — 테이블·컬럼 없을 때만 생성/추가
- 모든 파라미터에 명시적 `SqlDbType` 지정 (형 불일치 및 쿼리 플랜 재사용 보장)

### `LoaderService` (`Services/LoaderService.cs`)

- `RunCrawlerAsync()`, `RunEtlAsync()`, `RunAsync()` 분리 → UI에서 독립 실행 가능
- 파일 잠금 검사(`IsFileLocked`) — 크롤러가 아직 기록 중인 파일 방지
- `OnLog`, `OnFileProcessed`, `OnBulkProgress` 이벤트로 UI 갱신 분리

### `MainViewModel` (`ViewModels/MainViewModel.cs`)

- `IDisposable` 구현 — `_crawlerTimer`, `_etlTimer`, `_healthTimer` 소멸 시 해제
- `Interlocked.CompareExchange`로 ETL 동시 실행 방지
- `Dispatch()` 헬퍼로 모든 UI 조작을 `Application.Current.Dispatcher`로 전달
- `CommandManager.InvalidateRequerySuggested()` 는 항상 UI 스레드에서만 호출

---

## 아키텍처 개요

```
┌─────────────────────────────────────────┐
│              WPF (View)                 │
│  MainWindow.xaml  ─────────────────┐   │
│  (탭: 메인 / 설정 / 이력)           │   │
└──────────────────────────────────┬─┘   │
                                   │ DataBinding
                   ┌───────────────▼─────────────────┐
                   │         ViewModel Layer          │
                   │  MainViewModel (스케줄, 로그)    │
                   │  SettingsViewModel (설정 저장)   │
                   │  HistoryViewModel (이력 조회)    │
                   └───────────────┬─────────────────┘
                                   │
                   ┌───────────────▼─────────────────┐
                   │          Service Layer           │
                   │  LoaderService (ETL 오케스트레이터)│
                   │  ├─ CsvParserService             │
                   │  ├─ DatabaseService              │
                   │  └─ CrawlerService               │
                   └───────────────┬─────────────────┘
                                   │
                   ┌───────────────▼─────────────────┐
                   │       Infrastructure             │
                   │  SQL Server (pre_table, load_log)│
                   │  CSV files (data/, archive/)     │
                   └─────────────────────────────────┘
```

---

## 트러블슈팅

### DB 연결 실패

1. SQL Server 서비스 실행 여부 확인: `services.msc` → `SQL Server (MSSQLSERVER)`
2. 방화벽에서 TCP 1433 포트 허용 여부 확인
3. `auth_mode: "sql"` 사용 시 SQL Server 인증 모드 활성화 필요:
   ```
   SSMS → 서버 속성 → 보안 → SQL Server 및 Windows 인증 모드
   ```
4. `TrustServerCertificate=True`가 연결 문자열에 포함되어 있어 자체 서명 인증서 환경에서도 연결 가능

### CSV 파싱 오류가 많을 때

- `failedRows` 수는 로그와 `load_log.failed_rows`에서 확인 가능
- 가장 흔한 원인: 따옴표 처리가 안 된 CSV, BOM 없는 UTF-8 파일
- CsvHelper의 `BadDataFound = null` 설정으로 불량 행은 건너뜀

### 파일이 "잠김"으로 건너뛰어지는 경우

크롤러 프로세스가 아직 해당 CSV를 기록 중일 때 발생합니다.  
크롤러 종료 후 다음 ETL 스케줄 주기에 자동으로 재처리됩니다.

### `archive/` 이동 실패

archive 폴더가 다른 드라이브에 있으면 `File.Move`가 실패할 수 있습니다.
`archive_folder`를 `data_folder`와 동일한 드라이브 경로로 설정하세요.

### 적재 완료 확인 쿼리

```sql
-- 최근 적재된 논문 확인
SELECT TOP 10
    seq, site_name, title, doi, citation_count, loaded_at
FROM [dbo].[pre_table]
ORDER BY seq DESC;

-- ETL 이력 확인
SELECT TOP 20
    log_id, file_name, total_rows, loaded_rows, failed_rows, status, started_at
FROM [dbo].[load_log]
ORDER BY log_id DESC;

-- 소스별 인용 수 분포
SELECT site_name,
       COUNT(*) AS papers,
       AVG(CAST(citation_count AS FLOAT)) AS avg_citations
FROM [dbo].[pre_table]
WHERE citation_count IS NOT NULL
GROUP BY site_name
ORDER BY avg_citations DESC;
```
