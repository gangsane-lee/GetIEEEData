namespace SemiconductorLoader.Models;

/// <summary>CSV 1행에 대응하는 논문 레코드</summary>
public class PaperRecord
{
    public string    Url           { get; set; } = string.Empty;
    public string    SiteName      { get; set; } = string.Empty;
    public string?   Keyword       { get; set; }               // 구버전 CSV에 없을 수 있음 → null
    public string    PaperNumber   { get; set; } = string.Empty;
    public string    Title         { get; set; } = string.Empty;
    public string    Authors       { get; set; } = string.Empty;
    public DateOnly? PublishedDate { get; set; }               // "YYYY" 또는 "YYYY-MM-DD" 파싱
    public string?   Doi           { get; set; }               // 없는 경우 null
    public string?   Abstract      { get; set; }               // 없는 경우 null
    public int?      CitationCount { get; set; }               // 인용 수 (미지원 소스는 null)
    public string?   Journal       { get; set; }               // 학술지/컨퍼런스명 (없는 경우 null)
    public DateTime? ExtractedAt   { get; set; }
}

/// <summary>파일 1건의 적재 결과</summary>
public class LoadResult
{
    public string   FileName    { get; set; } = string.Empty;
    public int      TotalRows   { get; set; }
    public int      LoadedRows  { get; set; }
    public int      FailedRows  { get; set; }
    public string   Status      { get; set; } = "SUCCESS";     // SUCCESS | PARTIAL | FAILED
    public string?  ErrorMsg    { get; set; }
    public DateTime StartedAt   { get; set; }
    public DateTime FinishedAt  { get; set; }
}
