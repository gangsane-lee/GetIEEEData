"""
semiconductor_crawler/crawler.py
반도체 FA(Failure Analysis) 학술 논문 수집기

11개 학술 데이터 소스에서 config.json에 정의된 키워드로 병렬 수집 후 CSV 저장.
수집 필드: url, site_name, keyword, paper_number, title, authors,
           published_date, doi, abstract, citation_count, extracted_at
"""

import csv
import json
import os
import re
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime
from urllib.parse import quote

import requests
from requests.adapters import HTTPAdapter
from tqdm import tqdm
from urllib3.util.retry import Retry

# ──────────────────────────────────────────
# 경로 상수
# ──────────────────────────────────────────
_BASE_DIR   = os.path.dirname(os.path.abspath(__file__))
CONFIG_FILE = os.path.join(_BASE_DIR, "config.json")
MASTER_CSV  = os.path.join(_BASE_DIR, "master_log.csv")
DATA_DIR    = os.path.join(_BASE_DIR, "data")

CSV_FIELDS = [
    "url", "site_name", "keyword", "paper_number", "title",
    "authors", "published_date", "doi", "abstract",
    "citation_count", "journal", "extracted_at",
]
MASTER_FIELDS = ["site_name", "paper_number", "extracted_at"]


# ──────────────────────────────────────────
# 네트워크 세션
# ──────────────────────────────────────────
def get_secure_session() -> requests.Session:
    session = requests.Session()
    retry = Retry(
        total=3,
        backoff_factor=1,
        status_forcelist=[429, 500, 502, 503, 504],
        allowed_methods=["GET"],
    )
    adapter = HTTPAdapter(max_retries=retry)
    session.mount("https://", adapter)
    session.mount("http://",  adapter)
    session.headers.update({
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Academic Crawler"
    })
    return session


# ──────────────────────────────────────────
# 마스터 CSV (중복 제거용 인덱스)
# ──────────────────────────────────────────
def load_master_data(filepath: str) -> set:
    master: set = set()
    if not os.path.exists(filepath):
        return master
    with open(filepath, "r", encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            sn = row.get("site_name", "")
            pn = row.get("paper_number", "")
            if sn and pn:
                master.add((sn, pn))
    return master


def save_master_data(filepath: str, new_papers: list) -> None:
    file_exists = os.path.exists(filepath)
    with open(filepath, "a", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=MASTER_FIELDS)
        if not file_exists:
            writer.writeheader()
        for p in new_papers:
            writer.writerow({
                "site_name":    p["site_name"],
                "paper_number": p["paper_number"],
                "extracted_at": p["extracted_at"],
            })


# ──────────────────────────────────────────
# 내부 유틸
# ──────────────────────────────────────────
def _now() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def _paper(*, url="", site_name="", paper_number="", title="",
           authors="", published_date="", doi="", abstract="",
           citation_count=None, journal="", extracted_at="") -> dict:
    """CSV_FIELDS 순서와 일치하는 논문 딕셔너리 기본 틀을 반환.
    keyword는 main()에서 일괄 주입하므로 여기서는 빈 문자열로 초기화."""
    return {
        "url":            url,
        "site_name":      site_name,
        "keyword":        "",
        "paper_number":   paper_number,
        "title":          title,
        "authors":        authors,
        "published_date": published_date,
        "doi":            doi,
        "abstract":       abstract,
        "citation_count": citation_count,
        "journal":        journal,
        "extracted_at":   extracted_at,
    }


def _safe_text(el) -> str:
    """ElementTree 엘리먼트가 None이면 빈 문자열 반환."""
    return (el.text or "") if el is not None else ""


def _strip_html(text: str) -> str:
    """JATS/HTML 태그 제거 (CrossRef abstract 등)."""
    return re.sub(r"<[^>]+>", "", text or "").strip()


def _openalex_abstract(item: dict) -> str:
    """abstract_inverted_index(단어→위치 매핑)를 일반 텍스트로 복원."""
    inv = item.get("abstract_inverted_index") or {}
    if not inv:
        return ""
    pairs = sorted(((w, min(pos)) for w, pos in inv.items()), key=lambda x: x[1])
    return " ".join(w for w, _ in pairs)


def _base_doi(identifier) -> str:
    """dcidentifier(문자열 또는 리스트)에서 DOI(10.으로 시작) 추출."""
    items = identifier if isinstance(identifier, list) else [identifier]
    for v in items:
        if isinstance(v, str) and v.startswith("10."):
            return v
    return ""


def _base_abstract(desc) -> str:
    """dcdescription(문자열 또는 리스트)에서 첫 번째 값 반환."""
    if isinstance(desc, list):
        return desc[0] if desc else ""
    return desc or ""


# ──────────────────────────────────────────
# Fetcher 함수 (11개 소스)
# ──────────────────────────────────────────
def fetch_arxiv(c: dict, s: requests.Session, t: int) -> list:
    res = s.get(
        "http://export.arxiv.org/api/query",
        params={
            "search_query": f"all:{c.get('query', '')}",
            "sortBy":       "submittedDate",
            "sortOrder":    "descending",
            "max_results":  c.get("max_results_per_source", 20),
        },
        timeout=t,
    )
    res.raise_for_status()
    ns   = {"atom": "http://www.w3.org/2005/Atom", "arxiv": "http://arxiv.org/schemas/atom"}
    root = ET.fromstring(res.content)
    now  = _now()
    papers = []
    for e in root.findall("atom:entry", ns):
        id_text = _safe_text(e.find("atom:id", ns))
        if not id_text:
            continue
        papers.append(_paper(
            url            = id_text,
            site_name      = "arXiv",
            paper_number   = id_text.split("/abs/")[-1],
            title          = _safe_text(e.find("atom:title",     ns)).replace("\n", " ").strip(),
            authors        = ", ".join(
                                _safe_text(a.find("atom:name", ns))
                                for a in e.findall("atom:author", ns)
                             ),
            published_date = _safe_text(e.find("atom:published", ns))[:10],
            doi            = _safe_text(e.find("arxiv:doi",      ns)).strip(),
            abstract       = _safe_text(e.find("atom:summary",   ns)).replace("\n", " ").strip(),
            citation_count = None,
            journal        = _safe_text(e.find("arxiv:journal_ref", ns)).strip(),
            extracted_at   = now,
        ))
    return papers


def fetch_semantic_scholar(c: dict, s: requests.Session, t: int) -> list:
    data = s.get(
        "https://api.semanticscholar.org/graph/v1/paper/search",
        params={
            "query":  c.get("query", ""),
            "limit":  min(c.get("max_results_per_source", 20), 100),
            "fields": "title,authors,year,url,externalIds,abstract,citationCount,publicationVenue",
        },
        timeout=t,
    ).json()
    now = _now()
    papers = []
    for i in data.get("data", []):
        pid = i.get("paperId", "")
        if not pid:
            continue
        papers.append(_paper(
            url            = i.get("url", ""),
            site_name      = "Semantic Scholar",
            paper_number   = pid,
            title          = i.get("title", "") or "",
            authors        = ", ".join(a.get("name", "") for a in i.get("authors", [])),
            published_date = str(i.get("year", "")),
            doi            = (i.get("externalIds") or {}).get("DOI", ""),
            abstract       = i.get("abstract", "") or "",
            citation_count = i.get("citationCount"),
            journal        = (i.get("publicationVenue") or {}).get("name", ""),
            extracted_at   = now,
        ))
    return papers


def fetch_openalex(c: dict, s: requests.Session, t: int) -> list:
    data = s.get(
        "https://api.openalex.org/works",
        params={
            "search":   c.get("query", ""),
            "per-page": c.get("max_results_per_source", 20),
            "sort":     "publication_date:desc",
        },
        timeout=t,
    ).json()
    now = _now()
    papers = []
    for i in data.get("results", []):
        oa_id = i.get("id", "")
        papers.append(_paper(
            url            = oa_id,
            site_name      = "OpenAlex",
            paper_number   = oa_id.split("/")[-1],
            title          = i.get("title", "") or "",
            authors        = ", ".join(
                                a.get("author", {}).get("display_name", "")
                                for a in i.get("authorships", [])
                             ),
            published_date = i.get("publication_date", ""),
            doi            = (i.get("doi") or "").replace("https://doi.org/", ""),
            abstract       = _openalex_abstract(i),
            citation_count = i.get("cited_by_count"),
            journal        = ((i.get("primary_location") or {}).get("source") or {}).get("display_name", ""),
            extracted_at   = now,
        ))
    return papers


def fetch_crossref(c: dict, s: requests.Session, t: int) -> list:
    data = s.get(
        "https://api.crossref.org/works",
        params={
            "query": c.get("query", ""),
            "rows":  c.get("max_results_per_source", 20),
            "sort":  "published",
            "order": "desc",
        },
        timeout=t,
    ).json()
    now = _now()
    papers = []
    for i in data.get("message", {}).get("items", []):
        doi = i.get("DOI", "")
        if not doi:
            continue
        date_parts = i.get("published", {}).get("date-parts", [[""]])[0]
        papers.append(_paper(
            url            = i.get("URL", ""),
            site_name      = "CrossRef",
            paper_number   = doi,
            title          = (i.get("title") or [""])[0],
            authors        = ", ".join(a.get("family", "") for a in i.get("author", [])),
            published_date = "-".join(map(str, date_parts)),
            doi            = doi,
            abstract       = _strip_html(i.get("abstract", "")),
            citation_count = i.get("is-referenced-by-count"),
            journal        = (i.get("container-title") or [""])[0],
            extracted_at   = now,
        ))
    return papers


def fetch_europepmc(c: dict, s: requests.Session, t: int) -> list:
    data = s.get(
        "https://www.ebi.ac.uk/europepmc/webservices/rest/search",
        params={
            "query":      c.get("query", ""),
            "format":     "json",
            "pageSize":   c.get("max_results_per_source", 20),
            "resultType": "core",
        },
        timeout=t,
    ).json()
    now = _now()
    papers = []
    for i in data.get("resultList", {}).get("result", []):
        pid = i.get("id", "")
        if not pid:
            continue
        papers.append(_paper(
            url            = f"https://europepmc.org/article/{i.get('source', '')}/{pid}",
            site_name      = "Europe PMC",
            paper_number   = pid,
            title          = i.get("title", ""),
            authors        = i.get("authorString", ""),
            published_date = i.get("firstPublicationDate", ""),
            doi            = i.get("doi", "") or "",
            abstract       = i.get("abstractText", "") or "",
            citation_count = i.get("citedByCount"),
            journal        = i.get("journalTitle", "") or "",
            extracted_at   = now,
        ))
    return papers


def fetch_core(c: dict, s: requests.Session, t: int) -> list:
    if not c.get("core_api_key"):
        return []
    data = s.get(
        "https://api.core.ac.uk/v3/search/works",
        headers={"Authorization": f"Bearer {c['core_api_key']}"},
        params={"q": c.get("query", ""), "limit": c.get("max_results_per_source", 20)},
        timeout=t,
    ).json()
    now = _now()
    papers = []
    for i in data.get("results", []):
        pid = str(i.get("id", ""))
        if not pid:
            continue
        journals_list = i.get("journals") or []
        core_journal  = journals_list[0].get("title", "") if journals_list else (i.get("publisher", "") or "")
        papers.append(_paper(
            url            = i.get("downloadUrl", ""),
            site_name      = "CORE",
            paper_number   = pid,
            title          = i.get("title", "") or "",
            authors        = ", ".join(a.get("name", "") for a in (i.get("authors") or [])),
            published_date = (i.get("publishedDate") or "")[:10],
            doi            = i.get("doi", "") or "",
            abstract       = i.get("abstract", "") or "",
            citation_count = None,
            journal        = core_journal,
            extracted_at   = now,
        ))
    return papers


def fetch_base(c: dict, s: requests.Session, t: int) -> list:
    data = s.get(
        "https://api.base-search.net/cgi-bin/BaseHttpSearchInterface.fcgi",
        params={
            "func":   "PerformSearch",
            "query":  c.get("query", ""),
            "hits":   c.get("max_results_per_source", 20),
            "format": "json",
        },
        timeout=t,
    ).json()
    now = _now()
    papers = []
    for i in data.get("response", {}).get("docs", []):
        ident      = i.get("dcidentifier", "")
        pn         = (ident[0] if isinstance(ident, list) else ident) or ""
        title_raw  = i.get("dctitle", "")
        title      = (title_raw[0] if isinstance(title_raw, list) else title_raw) or ""
        creator    = i.get("dccreator", "")
        authors    = ", ".join(creator) if isinstance(creator, list) else str(creator or "")
        src_raw      = i.get("dcsource", "") or ""
        base_journal = (src_raw[0] if isinstance(src_raw, list) else src_raw) or ""
        papers.append(_paper(
            url            = i.get("dclink", ""),
            site_name      = "BASE",
            paper_number   = pn,
            title          = title,
            authors        = authors,
            published_date = str(i.get("dcyear", "")),
            doi            = _base_doi(ident),
            abstract       = _base_abstract(i.get("dcdescription", "")),
            citation_count = None,
            journal        = base_journal,
            extracted_at   = now,
        ))
    return papers


def fetch_ieee_xplore(c: dict, s: requests.Session, t: int) -> list:
    if not c.get("ieee_api_key"):
        return []
    data = s.get(
        "http://ieeexploreapi.ieee.org/api/v1/search/articles",
        params={
            "apikey":      c["ieee_api_key"],
            "format":      "json",
            "max_records": c.get("max_results_per_source", 20),
            "querytext":   c.get("query", ""),
        },
        timeout=t,
    ).json()
    now = _now()
    papers = []
    for i in data.get("articles", []):
        an = str(i.get("article_number", ""))
        if not an:
            continue
        papers.append(_paper(
            url            = i.get("pdf_url", i.get("document_link", "")),
            site_name      = "IEEE Xplore",
            paper_number   = an,
            title          = i.get("title", ""),
            authors        = ", ".join(
                                a.get("full_name", "")
                                for a in i.get("authors", {}).get("authors", [])
                             ),
            published_date = i.get("publication_date", ""),
            doi            = i.get("doi", "") or "",
            abstract       = i.get("abstract", "") or "",
            citation_count = i.get("citing_paper_count"),
            journal        = i.get("publication_title", "") or "",
            extracted_at   = now,
        ))
    return papers


def fetch_google_scholar(c: dict, s: requests.Session, t: int) -> list:
    try:
        from scholarly import scholarly
    except ImportError:
        return []
    limit  = min(c.get("max_results_per_source", 5), 10)
    now    = _now()
    papers = []
    try:
        gen = scholarly.search_pubs(c.get("query", ""))
        for _ in range(limit):
            try:
                pub = next(gen)
            except StopIteration:
                break
            bib   = pub.get("bib", {})
            title = bib.get("title", "")
            key   = pub.get("pub_url", "") or title
            papers.append(_paper(
                url            = pub.get("pub_url", ""),
                site_name      = "Google Scholar",
                paper_number   = str(abs(hash(key)))[:15],
                title          = title,
                authors        = " and ".join(bib.get("author", [])),
                published_date = str(bib.get("pub_year", "")),
                doi            = bib.get("doi", "") or "",
                abstract       = bib.get("abstract", "") or "",
                citation_count = pub.get("num_citations"),
                journal        = bib.get("venue", "") or "",
                extracted_at   = now,
            ))
    except Exception:
        pass
    return papers


def fetch_plos(c: dict, s: requests.Session, t: int) -> list:
    data = s.get(
        "https://api.plos.org/search",
        params={
            "q":    c.get("query", ""),
            "rows": c.get("max_results_per_source", 20),
            "fl":   "id,title_display,author_display,publication_date,abstract,journal",
        },
        timeout=t,
    ).json()
    now = _now()
    papers = []
    for i in data.get("response", {}).get("docs", []):
        pid = i.get("id", "")
        if not pid:
            continue
        ab_raw   = i.get("abstract", [])
        abstract = (
            ab_raw[0] if isinstance(ab_raw, list) and ab_raw
            else str(ab_raw) if ab_raw
            else ""
        )
        papers.append(_paper(
            url            = f"https://journals.plos.org/plosone/article?id={pid}",
            site_name      = "PLOS",
            paper_number   = pid,
            title          = i.get("title_display", ""),
            authors        = ", ".join(i.get("author_display", [])),
            published_date = i.get("publication_date", "")[:10],
            doi            = pid,
            abstract       = abstract,
            citation_count = None,
            journal        = i.get("journal", "") or "",
            extracted_at   = now,
        ))
    return papers


def fetch_doaj(c: dict, s: requests.Session, t: int) -> list:
    query = quote(c.get("query", ""), safe="")   # 공백/특수문자 URL 인코딩
    data = s.get(
        f"https://doaj.org/api/search/articles/{query}",
        params={"pageSize": c.get("max_results_per_source", 20)},
        timeout=t,
    ).json()
    now = _now()
    papers = []
    for i in data.get("results", []):
        pid = i.get("id", "")
        if not pid:
            continue
        bib   = i.get("bibjson", {})
        doi   = next(
            (x.get("id", "") for x in bib.get("identifier", [])
             if isinstance(x, dict) and x.get("type", "").lower() == "doi"),
            ""
        )
        links = bib.get("link", [{}])
        url   = links[0].get("url", "") if links else ""
        papers.append(_paper(
            url            = url,
            site_name      = "DOAJ",
            paper_number   = pid,
            title          = bib.get("title", ""),
            authors        = ", ".join(a.get("name", "") for a in bib.get("author", [])),
            published_date = str(bib.get("year", "")),
            doi            = doi,
            abstract       = bib.get("abstract", "") or "",
            citation_count = None,
            journal        = (bib.get("journal") or {}).get("title", "") or "",
            extracted_at   = now,
        ))
    return papers


# ──────────────────────────────────────────
FETCH_FUNCTIONS: dict = {
    "arxiv":            fetch_arxiv,
    "semantic_scholar": fetch_semantic_scholar,
    "openalex":         fetch_openalex,
    "crossref":         fetch_crossref,
    "europepmc":        fetch_europepmc,
    "core":             fetch_core,
    "base":             fetch_base,
    "ieee_xplore":      fetch_ieee_xplore,
    "google_scholar":   fetch_google_scholar,
    "plos":             fetch_plos,
    "doaj":             fetch_doaj,
}


# ──────────────────────────────────────────
# 메인 엔진
# ──────────────────────────────────────────
def main() -> None:
    os.makedirs(DATA_DIR, exist_ok=True)

    try:
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            config: dict = json.load(f)
    except Exception as e:
        print(f"[ERROR] 설정 파일 읽기 실패: {e}")
        return

    master_records = load_master_data(MASTER_CSV)
    session        = get_secure_session()

    global_keywords = config.get("global_keywords", [])
    active_sources  = {
        name: cfg
        for name, cfg in config.get("sources", {}).items()
        if cfg.get("enabled", False) and name in FETCH_FUNCTIONS
    }

    if not active_sources:
        print("[ERROR] 활성화된 소스가 없습니다. config.json을 확인하세요.")
        return

    # ── 태스크 목록 생성 ──
    tasks = []
    for keyword in global_keywords:
        for source_name, src_cfg in active_sources.items():
            task_cfg = src_cfg.copy()
            task_cfg["query"]               = keyword
            task_cfg["max_results_per_source"] = src_cfg.get(
                "max_results", config.get("max_results_per_source", 20)
            )
            tasks.append((source_name, keyword, task_cfg))

    n_src = len(active_sources)
    print(
        f"총 {n_src}개 소스 x {len(global_keywords)}개 키워드 "
        f"= {len(tasks)}개 태스크 시작...\n"
    )

    # ── 진행률 바 초기화 ──
    bars = {
        name: tqdm(total=len(global_keywords), desc=f"{name:20}", position=i, leave=True)
        for i, name in enumerate(active_sources)
    }
    new_counts: dict = {name: 0 for name in active_sources}
    err_counts: dict = {name: 0 for name in active_sources}
    all_new_papers: list = []

    timeout     = config.get("timeout_sec", 60)
    max_workers = min(len(tasks), config.get("max_workers", 15))

    # ── 병렬 수집 ──
    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        future_map = {
            executor.submit(FETCH_FUNCTIONS[src], cfg, session, timeout): (src, kw)
            for src, kw, cfg in tasks
        }

        for future in as_completed(future_map):
            source_name, keyword = future_map[future]
            try:
                for p in future.result():
                    p["keyword"] = keyword
                    key = (p["site_name"], p["paper_number"])
                    if p["paper_number"] and key not in master_records:
                        all_new_papers.append(p)
                        master_records.add(key)
                        new_counts[source_name] += 1
            except Exception:
                err_counts[source_name] += 1
            finally:
                bar = bars[source_name]
                postfix = f"신규 {new_counts[source_name]}건"
                if err_counts[source_name]:
                    postfix += f" | 오류 {err_counts[source_name]}건"
                bar.set_postfix_str(postfix)
                bar.update(1)

    print("\n" * n_src)

    # ── 결과 저장 ──
    min_new = config.get("min_new_papers", 1)
    if len(all_new_papers) < min_new:
        print(f"[INFO] 신규 {len(all_new_papers)}건 < 최소 {min_new}건 → 저장 생략")
        return

    filename = os.path.join(
        DATA_DIR,
        f"advanced_fa_papers_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv",
    )
    with open(filename, "w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=CSV_FIELDS, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(all_new_papers)

    save_master_data(MASTER_CSV, all_new_papers)

    total_err = sum(err_counts.values())
    print(f"[완료] {len(all_new_papers)}건 저장 → {filename}")
    if total_err:
        failed = {k: v for k, v in err_counts.items() if v}
        print(f"[경고] 수집 오류 총 {total_err}건 발생: {failed}")


if __name__ == "__main__":
    main()
