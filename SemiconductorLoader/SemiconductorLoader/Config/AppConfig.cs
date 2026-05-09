using System.IO;
using Newtonsoft.Json;

namespace SemiconductorLoader.Config;

public class DbSettings
{
    [JsonProperty("server")]             public string Server            { get; set; } = "127.0.0.1";
    [JsonProperty("port")]               public int    Port              { get; set; } = 1433;
    [JsonProperty("database")]           public string Database          { get; set; } = "AutoReport";
    /// <summary>"windows" = Windows 통합 인증 / "sql" = SQL Server 인증 (기본값)</summary>
    [JsonProperty("auth_mode")]          public string AuthMode          { get; set; } = "sql";
    [JsonProperty("username")]           public string Username          { get; set; } = "";
    [JsonProperty("password")]           public string Password          { get; set; } = "";
    [JsonProperty("connection_timeout")] public int    ConnectionTimeout { get; set; } = 30;
    [JsonProperty("command_timeout")]    public int    CommandTimeout    { get; set; } = 300;

    private bool IsWindows =>
        string.Equals(AuthMode, "windows", StringComparison.OrdinalIgnoreCase);

    public string BuildConnectionString() => BuildConnectionStringCore(Database);

    /// <summary>DB 자동 생성 시 master DB에 먼저 연결하기 위한 커넥션 문자열</summary>
    public string BuildMasterConnectionString() => BuildConnectionStringCore("master");

    private string BuildConnectionStringCore(string dbName)
    {
        var dataSource = Port == 1433 ? Server : $"{Server},{Port}";
        var auth = IsWindows
            ? "Integrated Security=True;"
            : $"User Id={Username};Password={Password};";

        return $"Data Source={dataSource};Database={dbName};" +
               auth +
               $"Connect Timeout={ConnectionTimeout};" +
               $"TrustServerCertificate=True;";
    }

    public string DisplayInfo => IsWindows
        ? $"{Database}@{Server} (Windows 인증)"
        : $"{Database}@{Server}:{Port} (SQL 인증)";
}

public class LoaderSettings
{
    [JsonProperty("scan_interval_minutes")]  public int    ScanIntervalMinutes  { get; set; } = 5;
    [JsonProperty("batch_size")]             public int    BatchSize            { get; set; } = 2000;
    [JsonProperty("data_folder")]            public string DataFolder           { get; set; } = "";
    [JsonProperty("archive_folder")]         public string ArchiveFolder        { get; set; } = "";

    /// <summary>ETL 전에 실행할 CSV 크롤러 BAT 파일 경로. 비어 있으면 크롤러 단계 건너뜀.</summary>
    [JsonProperty("crawler_bat_path")]       public string CrawlerBatPath       { get; set; } = "";

    /// <summary>크롤러 프로세스 최대 대기 시간 (분). 초과 시 강제 종료 후 ETL 진행.</summary>
    [JsonProperty("crawler_timeout_minutes")] public int   CrawlerTimeoutMinutes { get; set; } = 30;

    /// <summary>크롤러 스케줄 활성화 여부</summary>
    [JsonProperty("crawler_enabled")]        public bool  CrawlerEnabled        { get; set; } = true;

    /// <summary>ETL 스케줄 활성화 여부</summary>
    [JsonProperty("etl_enabled")]            public bool  EtlEnabled            { get; set; } = true;

    /// <summary>크롤러 독립 실행 주기 (분). ETL과 별도로 설정.</summary>
    [JsonProperty("crawler_interval_minutes")] public int CrawlerIntervalMinutes { get; set; } = 60;
}

public class AppConfig
{
    [JsonProperty("database")] public DbSettings     Database { get; set; } = new();
    [JsonProperty("loader")]   public LoaderSettings Loader   { get; set; } = new();

    /// <summary>현재 설정 파일 경로 (저장 시 동일 경로에 기록)</summary>
    [JsonIgnore]
    public string ConfigFilePath { get; private set; } = "";

    private const string ConfigFileName = "db_config.json";

    /// <summary>
    /// db_config.json을 exe 위치에서부터 상위 6단계까지 탐색하여 로드.
    /// 파일이 없으면 기본값 사용. data/archive 경로는 자동 탐색.
    /// </summary>
    public static AppConfig Load()
    {
        var configPath = FindFile(ConfigFileName);
        AppConfig cfg;

        if (configPath != null)
        {
            var json = File.ReadAllText(configPath, System.Text.Encoding.UTF8);
            cfg = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
        }
        else
        {
            cfg = new AppConfig();
        }

        cfg.ConfigFilePath = configPath ?? Path.Combine(AppBaseDir, ConfigFileName);

        // data / archive 경로 결정 (비어 있으면 exe 위치에서 상위 탐색)
        if (string.IsNullOrWhiteSpace(cfg.Loader.DataFolder))
            cfg.Loader.DataFolder = FindDataRoot() ?? Path.Combine(AppBaseDir, "data");

        if (string.IsNullOrWhiteSpace(cfg.Loader.ArchiveFolder))
            cfg.Loader.ArchiveFolder = Path.Combine(cfg.Loader.DataFolder, "archive");

        // crawler BAT 자동 탐색 (비어 있을 때만)
        if (string.IsNullOrWhiteSpace(cfg.Loader.CrawlerBatPath))
        {
            var bat = FindFile("start.bat");
            if (bat != null) cfg.Loader.CrawlerBatPath = bat;
        }

        return cfg;
    }

    /// <summary>현재 설정을 ConfigFilePath 경로에 JSON으로 저장</summary>
    public void Save()
    {
        var data = new { database = Database, loader = Loader };
        var json = JsonConvert.SerializeObject(data, Formatting.Indented);
        File.WriteAllText(ConfigFilePath, json, System.Text.Encoding.UTF8);
    }

    // ── 내부 헬퍼 ──────────────────────────────────────────────

    private static string AppBaseDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>
    /// exe 기준으로 상위를 최대 7단계까지 탐색하며 name에 해당하는 파일 또는 디렉터리를 찾는다.
    /// isDirectory=true 이면 디렉터리, false 이면 파일로 검사한다.
    /// </summary>
    private static string? FindPath(string name, bool isDirectory)
    {
        var dir = AppBaseDir;
        for (int i = 0; i < 7; i++)
        {
            var path = Path.Combine(dir, name);
            if (isDirectory ? Directory.Exists(path) : File.Exists(path)) return path;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static string? FindFile(string fileName)    => FindPath(fileName, isDirectory: false);
    private static string? FindDataRoot()               => FindPath("data",   isDirectory: true);
}
