using System.Data;
using Microsoft.Data.SqlClient;
using SemiconductorLoader.Config;
using SemiconductorLoader.Models;

namespace SemiconductorLoader.Services;

public class DatabaseService
{
    private readonly DbSettings _db;

    // BulkCopy 컬럼 매핑 정의 (소스 DataTable 컬럼명 → DB 컬럼명)
    private static readonly (string Src, string Dst)[] BulkMappings =
    [
        ("url",            "url"),
        ("site_name",      "site_name"),
        ("keyword",        "keyword"),
        ("paper_number",   "paper_number"),
        ("title",          "title"),
        ("authors",        "authors"),
        ("published_date", "published_date"),
        ("doi",            "doi"),
        ("abstract",       "abstract"),
        ("citation_count", "citation_count"),
        ("journal",        "journal"),
        ("extracted_at",   "extracted_at"),
    ];

    public DatabaseService(DbSettings db) => _db = db;

    // ===== 연결 헬퍼 =====

    /// <summary>대상 DB 커넥션을 열어 반환한다.</summary>
    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var conn = new SqlConnection(_db.BuildConnectionString());
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>master DB 커넥션을 열어 반환한다.</summary>
    private async Task<SqlConnection> OpenMasterConnectionAsync()
    {
        var conn = new SqlConnection(_db.BuildMasterConnectionString());
        await conn.OpenAsync();
        return conn;
    }

    // ===== 연결 테스트 =====

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            await using var conn = await OpenConnectionAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ===== DB + 테이블 자동 생성 =====

    /// <summary>
    /// ① master DB 연결 → 대상 DB가 없으면 CREATE DATABASE
    /// ② 대상 DB 연결 → pre_table, load_log가 없으면 CREATE TABLE
    /// onStep 콜백으로 각 단계를 로그에 알린다.
    /// </summary>
    public async Task EnsureDbAndTablesAsync(Action<string> onStep)
    {
        // ── 1단계: DB 존재 확인 및 생성 ──────────────────────────
        onStep("DB 존재 여부 확인 중...");
        await using (var master = await OpenMasterConnectionAsync())
        {
            // Bug 1 수정: 파라미터화 쿼리로 SQL Injection 방지
            const string checkSql = "SELECT COUNT(1) FROM sys.databases WHERE name = @dbName";
            await using var checkCmd = new SqlCommand(checkSql, master) { CommandTimeout = _db.CommandTimeout };
            checkCmd.Parameters.Add("@dbName", SqlDbType.NVarChar, 128).Value = _db.Database;

            var exists = (int)(await checkCmd.ExecuteScalarAsync() ?? 0) > 0;

            if (!exists)
            {
                onStep($"DB [{_db.Database}] 없음 — 생성 중...");
                // DB 이름은 식별자이므로 대괄호로 감싸 안전하게 처리
                var createSql = $"CREATE DATABASE [{_db.Database.Replace("]", "]]")}]";
                await using var createCmd = new SqlCommand(createSql, master) { CommandTimeout = _db.CommandTimeout };
                await createCmd.ExecuteNonQueryAsync();
                onStep($"DB [{_db.Database}] 생성 완료");
            }
            else
            {
                onStep($"DB [{_db.Database}] 확인 완료");
            }
        }

        // ── 2단계: 테이블 생성 ────────────────────────────────────
        await EnsureTablesAsync(onStep);
    }

    /// <summary>
    /// pre_table, load_log 가 없으면 생성한다 (멱등).
    /// 기존 테이블에 doi·abstract·citation_count 컬럼이 없으면 ALTER TABLE로 자동 추가.
    /// onStep 이 null 이면 로그 없이 조용히 실행.
    /// </summary>
    public async Task EnsureTablesAsync(Action<string>? onStep = null)
    {
        const string ddl = """
            IF NOT EXISTS (
                SELECT 1 FROM sys.objects
                WHERE object_id = OBJECT_ID(N'[dbo].[pre_table]') AND type = N'U'
            )
            CREATE TABLE [dbo].[pre_table] (
                seq            INT            IDENTITY(1,1)  NOT NULL,
                url            NVARCHAR(1000)                NULL,
                site_name      NVARCHAR(100)                 NULL,
                keyword        NVARCHAR(500)                 NULL,
                paper_number   NVARCHAR(200)                 NULL,
                title          NVARCHAR(2000)                NULL,
                authors        NVARCHAR(MAX)                 NULL,
                published_date DATE                          NULL,
                doi            NVARCHAR(500)                 NULL,
                abstract       NVARCHAR(MAX)                 NULL,
                citation_count INT                           NULL,
                journal        NVARCHAR(500)                 NULL,
                extracted_at   DATETIME                      NULL,
                loaded_at      DATETIME       NOT NULL       DEFAULT GETDATE(),
                CONSTRAINT PK_pre_table PRIMARY KEY (seq)
            );

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[pre_table]') AND name = 'doi')
                ALTER TABLE [dbo].[pre_table] ADD doi NVARCHAR(500) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[pre_table]') AND name = 'abstract')
                ALTER TABLE [dbo].[pre_table] ADD abstract NVARCHAR(MAX) NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[pre_table]') AND name = 'citation_count')
                ALTER TABLE [dbo].[pre_table] ADD citation_count INT NULL;

            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[pre_table]') AND name = 'journal')
                ALTER TABLE [dbo].[pre_table] ADD journal NVARCHAR(500) NULL;

            IF NOT EXISTS (
                SELECT 1 FROM sys.objects
                WHERE object_id = OBJECT_ID(N'[dbo].[load_log]') AND type = N'U'
            )
            CREATE TABLE [dbo].[load_log] (
                log_id       INT            IDENTITY(1,1)  NOT NULL,
                file_name    NVARCHAR(500)                 NOT NULL,
                total_rows   INT                           NOT NULL,
                loaded_rows  INT                           NOT NULL,
                failed_rows  INT                           NOT NULL,
                status       NVARCHAR(20)                  NOT NULL,
                error_msg    NVARCHAR(MAX)                 NULL,
                started_at   DATETIME                      NOT NULL,
                finished_at  DATETIME                      NOT NULL,
                CONSTRAINT PK_load_log PRIMARY KEY (log_id)
            );
            """;

        onStep?.Invoke("테이블 존재 여부 확인 중...");
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new SqlCommand(ddl, conn) { CommandTimeout = _db.CommandTimeout };
        await cmd.ExecuteNonQueryAsync();
        onStep?.Invoke("테이블 준비 완료 (pre_table, load_log)");
    }

    // ===== 고속 Bulk Insert =====

    /// <summary>
    /// SqlBulkCopy로 대량 적재.
    /// BatchSize 단위로 트랜잭션이 커밋되어 메모리를 절약하고 부분 실패를 최소화한다.
    /// progress: (loaded, total) 진행 상황을 배치 완료마다 보고한다.
    /// </summary>
    public async Task<int> BulkInsertAsync(
        List<PaperRecord> records,
        int batchSize = 2000,
        IProgress<(int Loaded, int Total)>? progress = null)
    {
        if (records.Count == 0) return 0;

        var table = BuildDataTable(records);
        int total = records.Count;

        await using var conn = await OpenConnectionAsync();

        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, null)
        {
            DestinationTableName = "[dbo].[pre_table]",
            BatchSize            = batchSize,
            BulkCopyTimeout      = _db.CommandTimeout,
            EnableStreaming       = true,
            NotifyAfter          = batchSize,
        };

        if (progress != null)
            bulk.SqlRowsCopied += (_, e) =>
                progress.Report(((int)e.RowsCopied, total));

        foreach (var (src, dst) in BulkMappings)
            bulk.ColumnMappings.Add(src, dst);

        await bulk.WriteToServerAsync(table);
        progress?.Report((total, total));
        return total;
    }

    // ===== 적재 결과 로그 기록 =====

    public async Task WriteLogAsync(LoadResult result)
    {
        const string sql = """
            INSERT INTO [dbo].[load_log]
                (file_name, total_rows, loaded_rows, failed_rows, status, error_msg, started_at, finished_at)
            VALUES
                (@fn, @total, @loaded, @failed, @status, @err, @start, @end)
            """;

        await using var conn = await OpenConnectionAsync();
        await using var cmd  = new SqlCommand(sql, conn) { CommandTimeout = _db.CommandTimeout };

        // Bug 2 수정: AddWithValue 대신 명시적 SqlDbType 사용 (NVARCHAR/INT/DATETIME 형 불일치 방지)
        cmd.Parameters.Add("@fn",     SqlDbType.NVarChar, 500).Value    = result.FileName;
        cmd.Parameters.Add("@total",  SqlDbType.Int).Value              = result.TotalRows;
        cmd.Parameters.Add("@loaded", SqlDbType.Int).Value              = result.LoadedRows;
        cmd.Parameters.Add("@failed", SqlDbType.Int).Value              = result.FailedRows;
        cmd.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value     = result.Status;
        cmd.Parameters.Add("@err",    SqlDbType.NVarChar, -1).Value     = (object?)result.ErrorMsg ?? DBNull.Value;
        cmd.Parameters.Add("@start",  SqlDbType.DateTime).Value         = result.StartedAt;
        cmd.Parameters.Add("@end",    SqlDbType.DateTime).Value         = result.FinishedAt;

        await cmd.ExecuteNonQueryAsync();
    }

    // ===== 이력 조회 =====

    /// <summary>load_log 최근 N건 조회 (최신순)</summary>
    public async Task<List<LoadLogEntry>> QueryLogsAsync(int topN = 500)
    {
        const string sql = """
            SELECT TOP (@topN)
                log_id, file_name, total_rows, loaded_rows, failed_rows,
                status, error_msg, started_at, finished_at
            FROM [dbo].[load_log]
            ORDER BY log_id DESC
            """;

        var result = new List<LoadLogEntry>();
        await using var conn = await OpenConnectionAsync();
        await using var cmd  = new SqlCommand(sql, conn) { CommandTimeout = _db.CommandTimeout };
        cmd.Parameters.Add("@topN", SqlDbType.Int).Value = topN;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new LoadLogEntry
            {
                LogId      = reader.GetInt32(0),
                FileName   = reader.GetString(1),
                TotalRows  = reader.GetInt32(2),
                LoadedRows = reader.GetInt32(3),
                FailedRows = reader.GetInt32(4),
                Status     = reader.GetString(5),
                ErrorMsg   = reader.IsDBNull(6) ? null : reader.GetString(6),
                StartedAt  = reader.GetDateTime(7),
                FinishedAt = reader.GetDateTime(8),
            });
        }

        return result;
    }

    /// <summary>load_log 전체 통계 집계</summary>
    public async Task<(int Total, int Success, int Failed, long LoadedRows)> QueryStatsAsync()
    {
        const string sql = """
            SELECT
                COUNT(*)                                                                       AS total,
                ISNULL(SUM(CASE WHEN status IN ('SUCCESS','PARTIAL') THEN 1 ELSE 0 END), 0)   AS success,
                ISNULL(SUM(CASE WHEN status = 'FAILED'               THEN 1 ELSE 0 END), 0)   AS failed,
                ISNULL(SUM(CAST(loaded_rows AS BIGINT)), 0)                                    AS loaded_rows
            FROM [dbo].[load_log]
            """;

        await using var conn   = await OpenConnectionAsync();
        await using var cmd    = new SqlCommand(sql, conn) { CommandTimeout = _db.CommandTimeout };
        await using var reader = await cmd.ExecuteReaderAsync();

        if (await reader.ReadAsync())
            return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3));

        return (0, 0, 0, 0L);
    }

    // ===== DataTable 빌더 =====

    private static DataTable BuildDataTable(List<PaperRecord> records)
    {
        var dt = new DataTable();
        dt.Columns.Add("url",            typeof(string));
        dt.Columns.Add("site_name",      typeof(string));
        dt.Columns.Add("keyword",        typeof(string));
        dt.Columns.Add("paper_number",   typeof(string));
        dt.Columns.Add("title",          typeof(string));
        dt.Columns.Add("authors",        typeof(string));
        dt.Columns.Add("published_date", typeof(DateTime));
        dt.Columns.Add("doi",            typeof(string));
        dt.Columns.Add("abstract",       typeof(string));
        dt.Columns.Add("citation_count", typeof(int));
        dt.Columns.Add("journal",        typeof(string));
        dt.Columns.Add("extracted_at",   typeof(DateTime));

        foreach (var r in records)
        {
            dt.Rows.Add(
                Cap(r.Url,          1000),
                Cap(r.SiteName,      100),
                Cap(r.Keyword,       500),
                Cap(r.PaperNumber,   200),
                Cap(r.Title,        2000),
                r.Authors,
                r.PublishedDate.HasValue
                    ? (object)r.PublishedDate.Value.ToDateTime(TimeOnly.MinValue)
                    : DBNull.Value,
                Cap(r.Doi,           500),
                r.Abstract,
                r.CitationCount.HasValue
                    ? (object)r.CitationCount.Value
                    : DBNull.Value,
                Cap(r.Journal, 500),
                r.ExtractedAt.HasValue
                    ? (object)r.ExtractedAt.Value
                    : DBNull.Value
            );
        }

        return dt;
    }

    /// <summary>문자열이 maxLen을 초과하면 잘라 반환. null이면 DBNull.</summary>
    private static object Cap(string? value, int maxLen)
    {
        if (value == null) return DBNull.Value;
        return value.Length <= maxLen ? (object)value : value[..maxLen];
    }
}
