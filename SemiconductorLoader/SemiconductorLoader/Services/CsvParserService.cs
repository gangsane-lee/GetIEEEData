using System.Globalization;
using System.IO;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using SemiconductorLoader.Models;

namespace SemiconductorLoader.Services;

public static class CsvParserService
{
    // 매 Parse() 호출마다 재생성하지 않도록 static readonly로 공유
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        HeaderValidated   = null,   // 헤더 검증 비활성화
        MissingFieldFound = null,   // 없는 컬럼 → null (구버전 CSV 호환)
        BadDataFound      = null,   // 불량 데이터 무시
        TrimOptions       = TrimOptions.Trim,
        Encoding          = Encoding.UTF8,
    };

    /// <summary>
    /// CSV 파일을 파싱하여 PaperRecord 리스트와 파싱 실패 행 수를 반환.
    /// 구버전 CSV (keyword / doi / abstract / citation_count 컬럼 없음)도 호환.
    /// </summary>
    public static (List<PaperRecord> Records, int FailedRows) Parse(string filePath)
    {
        var records    = new List<PaperRecord>();
        int failedRows = 0;

        using var reader = new StreamReader(filePath, detectEncodingFromByteOrderMarks: true);
        using var csv    = new CsvReader(reader, CsvConfig);

        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();

        bool hasKeyword       = headers.Contains("keyword",        StringComparer.OrdinalIgnoreCase);
        bool hasDoi           = headers.Contains("doi",            StringComparer.OrdinalIgnoreCase);
        bool hasAbstract      = headers.Contains("abstract",       StringComparer.OrdinalIgnoreCase);
        bool hasCitationCount = headers.Contains("citation_count", StringComparer.OrdinalIgnoreCase);
        bool hasJournal       = headers.Contains("journal",        StringComparer.OrdinalIgnoreCase);

        while (csv.Read())
        {
            try
            {
                var record = new PaperRecord
                {
                    Url           = csv.GetField("url")          ?? string.Empty,
                    SiteName      = csv.GetField("site_name")    ?? string.Empty,
                    Keyword       = hasKeyword       ? csv.GetField("keyword")        : null,
                    PaperNumber   = csv.GetField("paper_number") ?? string.Empty,
                    Title         = csv.GetField("title")        ?? string.Empty,
                    Authors       = csv.GetField("authors")      ?? string.Empty,
                    PublishedDate = ParseDate(csv.GetField("published_date")),
                    Doi           = hasDoi           ? csv.GetField("doi")            : null,
                    Abstract      = hasAbstract      ? csv.GetField("abstract")       : null,
                    CitationCount = hasCitationCount ? ParseInt(csv.GetField("citation_count")) : null,
                    Journal       = hasJournal       ? csv.GetField("journal")                 : null,
                    ExtractedAt   = ParseDateTime(csv.GetField("extracted_at")),
                };
                records.Add(record);
            }
            catch
            {
                failedRows++;
            }
        }

        return (records, failedRows);
    }

    // ── 날짜 파싱 ──────────────────────────────────────────────

    /// <summary>
    /// published_date 파싱 규칙:
    ///   "2025-04-07" → DateOnly(2025, 4, 7)
    ///   "2025-04"    → DateOnly(2025, 4, 1)
    ///   "2025"       → DateOnly(2025, 1, 1)
    ///   그 외 / 빈값 → null
    /// </summary>
    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
            return d1;

        if (raw.Length == 7 &&
            DateOnly.TryParseExact(raw, "yyyy-MM",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return new DateOnly(d2.Year, d2.Month, 1);

        if (raw.Length == 4 &&
            int.TryParse(raw, out var year) && year >= 1900 && year <= 2100)
            return new DateOnly(year, 1, 1);

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
            return DateOnly.FromDateTime(dt);

        return null;
    }

    private static DateTime? ParseDateTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }

    private static int? ParseInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }
}
