# Semiconductor FA Paper Crawler

반도체 Failure Analysis(FA) 분야 학술 논문을 11개 데이터베이스에서 자동 수집하여
SQL Server에 저장하고 웹으로 조회하는 엔드-투-엔드 파이프라인.

---

## 전체 흐름

```
crawler.py (Python)
  └─ 20개 키워드 × 11개 소스 병렬 수집
  └─ data/advanced_fa_papers_YYYYMMDD_HHMMSS.csv 저장
       │
       ▼
SemiconductorLoader (C# WPF)
  └─ CSV 파싱 → SqlBulkCopy → SQL Server pre_table 적재
  └─ 처리 완료 파일 → data/archive/YYYY-MM-DD/ 이동
       │
       ▼
Web (Node.js + Vue 3)
  └─ REST API (localhost:3000) → 논문 검색·조회 UI
```

---

## 수집 소스 (11개)

| 소스 | API 키 필요 | DOI | Abstract | 인용 수 | 학술지명 |
|------|:-----------:|:---:|:--------:|:-------:|:-------:|
| arXiv | — | △ | ✅ | — | △ (게재본만) |
| Semantic Scholar | — | ✅ | ✅ | ✅ | ✅ |
| OpenAlex | — | ✅ | ✅ | ✅ | ✅ |
| CrossRef | — | ✅ | △ | ✅ | ✅ |
| Europe PMC | — | ✅ | ✅ | ✅ | ✅ |
| CORE | **필수** | ✅ | ✅ | — | ✅ |
| BASE | — | △ | △ | — | △ |
| IEEE Xplore | **필수** | ✅ | ✅ | ✅ | ✅ |
| Google Scholar | — | △ | △ | ✅ | △ |
| PLOS | — | ✅ | ✅ | — | ✅ |
| DOAJ | — | ✅ | ✅ | — | ✅ |

> △ = 논문에 따라 값 없을 수 있음  
> CORE · IEEE API 키 미입력 시 해당 소스는 자동 건너뜀

---

## 설치 및 실행

### 1. Python 크롤러

```powershell
pip install -r requirements.txt
python crawler.py
# 또는
start.bat
```

### 2. C# ETL 로더

1. `SemiconductorLoader/db_config.example.json` → `SemiconductorLoader/db_config.json` 복사
2. server·data_folder·archive_folder 경로 수정
3. Visual Studio에서 `SemiconductorLoader.sln` 열어 빌드 후 실행

### 3. 웹 백엔드

```powershell
cd web/backend
copy .env.example .env      # 실제 DB 정보 입력
npm install
npm start                   # http://localhost:3000
```

### 4. 웹 프론트엔드

```powershell
cd web/frontend
npm install
npm run dev                 # 개발 서버 (Vite)
npm run build               # dist/ 프로덕션 빌드
```

---

## 설정 (config.json)

| 항목 | 기본값 | 설명 |
|------|--------|------|
| `max_results_per_source` | 20 | 소스당 최대 수집 건수 |
| `min_new_papers` | 3 | 이 수 미만이면 CSV 저장 생략 |
| `timeout_sec` | 60 | HTTP 요청 타임아웃 (초) |
| `max_workers` | 15 | 병렬 스레드 수 |
| `core_api_key` | `""` | CORE API 키 — [무료 발급](https://core.ac.uk/) |
| `ieee_api_key` | `""` | IEEE Xplore API 키 — [발급](https://developer.ieee.org/) |

키워드는 `global_keywords` 배열에서 관리 (현재 20개 반도체 FA 관련 검색어).  
소스별 활성화/비활성화는 `sources.<소스명>.enabled` 값으로 제어.

---

## DB 스키마

### pre_table (논문 데이터)

| 컬럼 | 타입 | 설명 |
|------|------|------|
| seq | INT IDENTITY (PK) | 자동 증가 키 |
| url | NVARCHAR(1000) | 논문 원문 URL |
| site_name | NVARCHAR(100) | 수집 소스명 |
| keyword | NVARCHAR(500) | 검색 키워드 |
| paper_number | NVARCHAR(200) | 소스 내 고유 ID (중복 제거 기준) |
| title | NVARCHAR(2000) | 논문 제목 |
| authors | NVARCHAR(MAX) | 저자 목록 |
| published_date | DATE | 출판일 |
| doi | NVARCHAR(500) | DOI |
| abstract | NVARCHAR(MAX) | 초록 |
| citation_count | INT | 인용 횟수 |
| journal | NVARCHAR(500) | 학술지/컨퍼런스명 |
| extracted_at | DATETIME | 수집 시각 |
| loaded_at | DATETIME | DB 적재 시각 (DEFAULT GETDATE()) |

### load_log (ETL 이력)

| 컬럼 | 타입 | 설명 |
|------|------|------|
| log_id | INT IDENTITY (PK) | — |
| file_name | NVARCHAR(500) | 처리한 CSV 파일명 |
| total_rows | INT | 전체 행 수 |
| loaded_rows | INT | 적재 성공 행 수 |
| failed_rows | INT | 파싱 실패 행 수 |
| status | NVARCHAR(20) | SUCCESS / PARTIAL / FAILED |
| error_msg | NVARCHAR(MAX) | 오류 메시지 |
| started_at | DATETIME | 시작 시각 |
| finished_at | DATETIME | 완료 시각 |

> DB와 테이블은 ETL 로더 최초 실행 시 자동 생성됨.  
> 기존 테이블에 컬럼이 없으면 `ALTER TABLE`로 자동 추가 (무중단 마이그레이션).

---

## 폴더 구조

```
semiconductor_crawler/
├── config.json                    # 크롤러 설정 (키워드, 소스, API 키)
├── requirements.txt               # Python 의존성
├── start.bat                      # 크롤러 원클릭 실행
├── crawler.py                     # 메인 크롤러 스크립트
│
├── data/                          # 크롤링 결과 (.gitignore 제외)
│   └── archive/YYYY-MM-DD/        # 처리 완료 파일 보관
│
├── SemiconductorLoader/           # C# WPF ETL 로더
│   ├── db_config.example.json     # DB 설정 템플릿 (복사해서 사용)
│   └── SemiconductorLoader/
│       ├── Services/              # CsvParserService, DatabaseService, LoaderService
│       ├── ViewModels/            # MVVM — Main, Settings, History
│       └── Models/                # PaperRecord, LoadResult, LoadLogEntry
│
└── web/
    ├── backend/                   # Node.js + Express REST API
    │   └── .env.example           # DB 환경변수 템플릿 (복사해서 사용)
    └── frontend/                  # Vue 3 + Vite + DevExtreme UI
```

---

## 사내 클론 방법

```powershell
git clone https://github.com/gangsane-lee/GetIEEEData.git
cd GetIEEEData

# 웹 백엔드 환경변수
copy web\backend\.env.example web\backend\.env
# .env 열어서 DB_SERVER, DB_NAME, DB_USER, DB_PASSWORD 입력

# ETL 로더 DB 설정
copy SemiconductorLoader\db_config.example.json SemiconductorLoader\db_config.json
# db_config.json 열어서 server, data_folder, archive_folder, crawler_bat_path 수정
```

---

## 주의 사항

- `web/backend/.env` — DB 비밀번호 포함, `.gitignore` 처리됨 (커밋 금지)
- `SemiconductorLoader/db_config.json` — 절대경로·서버명 포함, `.gitignore` 처리됨
- `data/` 폴더 — 실제 수집 데이터, `.gitignore` 처리됨
- Semantic Scholar는 API 키 없이 사용 가능하나 1 req/s 제한 있음 (병렬 실행 시 일부 오류 가능)

---

## 브랜치

| 브랜치 | 용도 |
|--------|------|
| `main` | 안정 버전 |
| `develop` | 개발·테스트 |
