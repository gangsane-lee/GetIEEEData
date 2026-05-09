using System.IO;
using SemiconductorLoader.Config;
using SemiconductorLoader.Models;

namespace SemiconductorLoader.Services;

/// <summary>
/// CSV → DB 적재 파이프라인 오케스트레이터.
/// data/ 폴더의 CSV를 순서대로 처리하고 완료 파일을 archive/로 이동한다.
/// </summary>
public class LoaderService
{
    private readonly AppConfig       _cfg;
    private readonly DatabaseService _db;

    /// <summary>로그 메시지 발행 이벤트 (UI 스레드 외에서 호출될 수 있음)</summary>
    public event Action<string>?     OnLog;

    /// <summary>파일 1건 처리 완료 이벤트</summary>
    public event Action<LoadResult>? OnFileProcessed;

    /// <summary>DB Bulk 적재 진행률 이벤트 (loaded 건수, total 건수)</summary>
    public event Action<int, int>?   OnBulkProgress;

    public LoaderService(AppConfig cfg)
    {
        _cfg = cfg;
        _db  = new DatabaseService(cfg.Database);
    }

    public Task<bool> TestConnectionAsync() => _db.TestConnectionAsync();

    public Task InitializeAsync() => _db.EnsureDbAndTablesAsync(Log);

    // ===== 메인 실행 =====

    /// <summary>크롤러 BAT만 실행 (CrawlerBatPath 비어 있으면 no-op)</summary>
    public async Task RunCrawlerAsync()
    {
        if (string.IsNullOrWhiteSpace(_cfg.Loader.CrawlerBatPath)) return;
        Directory.CreateDirectory(_cfg.Loader.DataFolder);
        await new CrawlerService().RunAsync(
            _cfg.Loader.CrawlerBatPath,
            _cfg.Loader.CrawlerTimeoutMinutes,
            Log);
    }

    /// <summary>CSV 스캔 + DB 적재 (크롤러 실행 없음)</summary>
    public async Task RunEtlAsync()
    {
        var dataDir    = _cfg.Loader.DataFolder;
        var archiveDir = _cfg.Loader.ArchiveFolder;

        Directory.CreateDirectory(dataDir);

        // ── 1. 파일 스캔 ─────────────────────────────────────────
        var files = Directory.GetFiles(dataDir, "*.csv", SearchOption.TopDirectoryOnly)
                             .OrderBy(f => f)
                             .ToArray();

        Log($"파일 스캔: {files.Length}개 발견  ({dataDir})");

        if (files.Length == 0) return;

        // ── 2. 테이블 존재 보장 (외부 삭제 대비, 멱등 실행) ──────
        await _db.EnsureTablesAsync(Log);

        // ── 3. 파일 처리 ─────────────────────────────────────────
        foreach (var file in files)
            await ProcessFileAsync(file, archiveDir);
    }

    /// <summary>크롤러 → ETL 순서로 전체 실행 (기존 동작 유지)</summary>
    public async Task RunAsync()
    {
        await RunCrawlerAsync();
        await RunEtlAsync();
    }

    // ===== 파일 1건 처리 =====

    private async Task ProcessFileAsync(string filePath, string archiveDir)
    {
        var fileName  = Path.GetFileName(filePath);
        var startedAt = DateTime.Now;

        // ── 파일 잠금 확인 (아직 기록 중인 파일 방지) ──
        if (IsFileLocked(filePath))
        {
            Log($"[{fileName}] 파일 잠김 — 건너뜀 (다음 실행 시 재시도)");
            return;
        }

        Log($"[{fileName}] 처리 시작");

        LoadResult result;

        try
        {
            // 1. CSV 파싱
            var (records, failedRows) = CsvParserService.Parse(filePath);
            Log($"[{fileName}] 파싱 완료: {records.Count}행  (파싱 실패 {failedRows}행)");

            if (records.Count == 0 && failedRows == 0)
            {
                Log($"[{fileName}] 데이터 없음 — 건너뜀");
                return;
            }

            // 2. DB Bulk Insert
            Log($"[{fileName}] DB 적재 시작: {records.Count}건");
            var bulkProgress = new Progress<(int Loaded, int Total)>(p =>
                OnBulkProgress?.Invoke(p.Loaded, p.Total));
            int loaded = await _db.BulkInsertAsync(records, _cfg.Loader.BatchSize, bulkProgress);
            Log($"[{fileName}] DB 적재 완료: {loaded}건");

            result = new LoadResult
            {
                FileName   = fileName,
                TotalRows  = records.Count + failedRows,
                LoadedRows = loaded,
                FailedRows = failedRows,
                Status     = failedRows == 0 ? "SUCCESS" : "PARTIAL",
                StartedAt  = startedAt,
                FinishedAt = DateTime.Now,
            };
        }
        catch (Exception ex)
        {
            Log($"[{fileName}] 오류: {ex.Message}");
            result = new LoadResult
            {
                FileName   = fileName,
                TotalRows  = 0,
                LoadedRows = 0,
                FailedRows = 0,
                Status     = "FAILED",
                ErrorMsg   = ex.Message,
                StartedAt  = startedAt,
                FinishedAt = DateTime.Now,
            };
        }

        // 3. DB 로그 기록 (실패해도 이후 처리 계속)
        try
        {
            await _db.WriteLogAsync(result);
        }
        catch (Exception ex)
        {
            Log($"[{fileName}] 로그 기록 실패: {ex.Message}");
        }

        OnFileProcessed?.Invoke(result);

        // 4. 성공·부분성공 파일 → archive 이동
        if (result.Status != "FAILED")
        {
            MoveToArchive(filePath, archiveDir, fileName);
        }
        else
        {
            Log($"[{fileName}] 적재 실패 — data 폴더에 유지 (재시도 가능)");
        }
    }

    // ===== archive 이동 =====

    private void MoveToArchive(string filePath, string archiveDir, string fileName)
    {
        try
        {
            var dateDir = Path.Combine(archiveDir, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dateDir);

            var dest = Path.Combine(dateDir, fileName);

            if (File.Exists(dest))
                dest = Path.Combine(dateDir,
                    Path.GetFileNameWithoutExtension(fileName)
                    + $"_{DateTime.Now:HHmmss}.csv");

            File.Move(filePath, dest);
            Log($"[{fileName}] → archive/{DateTime.Now:yyyy-MM-dd}/");
        }
        catch (Exception ex)
        {
            Log($"[{fileName}] archive 이동 실패: {ex.Message}");
        }
    }

    // ===== 파일 잠금 확인 =====

    /// <summary>
    /// 파일이 다른 프로세스에 의해 잠겨 있으면 true 반환.
    /// 크롤러가 아직 기록 중인 파일을 처리하지 않도록 보호한다.
    /// </summary>
    private static bool IsFileLocked(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            // 다른 프로세스가 파일을 점유 중 (크롤러가 아직 기록 중)
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 권한 없음도 처리 불가능하므로 잠긴 것으로 간주
            return true;
        }
    }

    // ===== 로그 헬퍼 =====

    private void Log(string message) =>
        OnLog?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}");
}
