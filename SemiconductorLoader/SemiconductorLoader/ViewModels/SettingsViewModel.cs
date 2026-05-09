using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SemiconductorLoader.Config;
using SemiconductorLoader.Services;

namespace SemiconductorLoader.ViewModels;

/// <summary>DB 접속 정보 및 적재 설정을 관리하는 ViewModel</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppConfig _cfg;

    // ── DB 설정 ────────────────────────────────────────────────────
    private string _server;
    private int    _port;
    private string _database;
    private string _authMode;
    private string _username;
    private string _password;
    private int    _connectionTimeout;
    private int    _commandTimeout;

    public string Server            { get => _server;            set { _server            = value; OnPC(); } }
    public int    Port              { get => _port;              set { _port              = Math.Max(1, value); OnPC(); } }
    public string Database          { get => _database;          set { _database          = value; OnPC(); } }
    public string Username          { get => _username;          set { _username          = value; OnPC(); } }
    public string Password          { get => _password;          set { _password          = value; OnPC(); } }
    public int    ConnectionTimeout { get => _connectionTimeout; set { _connectionTimeout = Math.Max(1, value); OnPC(); } }
    public int    CommandTimeout    { get => _commandTimeout;    set { _commandTimeout    = Math.Max(1, value); OnPC(); } }

    public string AuthMode
    {
        get => _authMode;
        set
        {
            var v = value?.ToLower() ?? "sql";
            if (_authMode == v) return;
            _authMode = v;
            OnPC();
            OnPC(nameof(IsWindowsAuth));
            OnPC(nameof(IsSqlAuth));
        }
    }

    public bool IsWindowsAuth
    {
        get => string.Equals(_authMode, "windows", StringComparison.OrdinalIgnoreCase);
        set => AuthMode = value ? "windows" : "sql";
    }

    public bool IsSqlAuth
    {
        get => !IsWindowsAuth;
        set => IsWindowsAuth = !value;
    }

    // ── 적재 설정 ──────────────────────────────────────────────────
    private int    _scanIntervalMinutes;
    private int    _batchSize;
    private string _dataFolder;
    private string _archiveFolder;

    public int    ScanIntervalMinutes { get => _scanIntervalMinutes; set { _scanIntervalMinutes = Math.Max(1, value); OnPC(); } }
    public int    BatchSize           { get => _batchSize;           set { _batchSize           = Math.Max(100, value); OnPC(); } }
    public string DataFolder          { get => _dataFolder;          set { _dataFolder          = value; OnPC(); } }
    public string ArchiveFolder       { get => _archiveFolder;       set { _archiveFolder       = value; OnPC(); } }

    // ── 크롤러 설정 ────────────────────────────────────────────────
    private string _crawlerBatPath;
    private int    _crawlerTimeoutMinutes;
    private bool   _crawlerEnabled;
    private bool   _etlEnabled;
    private int    _crawlerIntervalMinutes;

    /// <summary>ETL 전 실행할 BAT 파일 경로. 비어 있으면 크롤러 단계 건너뜀.</summary>
    public string CrawlerBatPath
    {
        get => _crawlerBatPath;
        set { _crawlerBatPath = value; OnPC(); }
    }
    /// <summary>크롤러 최대 대기 시간(분). 초과 시 강제 종료 후 ETL 진행.</summary>
    public int CrawlerTimeoutMinutes
    {
        get => _crawlerTimeoutMinutes;
        set { _crawlerTimeoutMinutes = Math.Max(1, value); OnPC(); }
    }
    /// <summary>크롤러 스케줄 활성화 여부</summary>
    public bool CrawlerEnabled
    {
        get => _crawlerEnabled;
        set { _crawlerEnabled = value; OnPC(); }
    }
    /// <summary>ETL 스케줄 활성화 여부</summary>
    public bool EtlEnabled
    {
        get => _etlEnabled;
        set { _etlEnabled = value; OnPC(); }
    }
    /// <summary>크롤러 독립 실행 주기 (분)</summary>
    public int CrawlerIntervalMinutes
    {
        get => _crawlerIntervalMinutes;
        set { _crawlerIntervalMinutes = Math.Max(1, value); OnPC(); }
    }

    // ── 상태 ──────────────────────────────────────────────────────
    private string _testResult  = "";
    private bool   _isTestOk;
    private string _saveResult  = "";
    private bool   _isTesting;

    public string TestResult  { get => _testResult;  private set { _testResult  = value; OnPC(); } }
    public bool   IsTestOk    { get => _isTestOk;    private set { _isTestOk    = value; OnPC(); } }
    public string SaveResult  { get => _saveResult;  private set { _saveResult  = value; OnPC(); } }
    public bool   IsTesting   { get => _isTesting;   private set { _isTesting   = value; OnPC(); CommandManager.InvalidateRequerySuggested(); } }

    // ── 명령 ──────────────────────────────────────────────────────
    public ICommand TestConnectionCommand { get; }
    public ICommand SaveCommand           { get; }

    // ── 이벤트 ─────────────────────────────────────────────────────
    /// <summary>저장 완료 시 새 설정을 전달 (MainViewModel이 구독하여 서비스 재생성)</summary>
    public event Action<AppConfig>? SettingsSaved;

    // ── 생성자 ──────────────────────────────────────────────────────
    public SettingsViewModel(AppConfig cfg)
    {
        _cfg = cfg;

        // DB 설정 초기화
        _server            = cfg.Database.Server;
        _port              = cfg.Database.Port;
        _database          = cfg.Database.Database;
        _authMode          = cfg.Database.AuthMode?.ToLower() ?? "sql";
        _username          = cfg.Database.Username;
        _password          = cfg.Database.Password;
        _connectionTimeout = cfg.Database.ConnectionTimeout;
        _commandTimeout    = cfg.Database.CommandTimeout;

        // 적재 설정 초기화
        _scanIntervalMinutes  = cfg.Loader.ScanIntervalMinutes;
        _batchSize            = cfg.Loader.BatchSize;
        _dataFolder           = cfg.Loader.DataFolder;
        _archiveFolder        = cfg.Loader.ArchiveFolder;

        // 크롤러 설정 초기화
        _crawlerBatPath          = cfg.Loader.CrawlerBatPath;
        _crawlerTimeoutMinutes   = cfg.Loader.CrawlerTimeoutMinutes;
        _crawlerEnabled          = cfg.Loader.CrawlerEnabled;
        _etlEnabled              = cfg.Loader.EtlEnabled;
        _crawlerIntervalMinutes  = cfg.Loader.CrawlerIntervalMinutes;

        TestConnectionCommand = new RelayCommand(
            _ => _ = TestConnectionAsync(),
            _ => !IsTesting);

        SaveCommand = new RelayCommand(_ => SaveSettings());
    }

    // ── 연결 테스트 ────────────────────────────────────────────────
    private async Task TestConnectionAsync()
    {
        IsTesting  = true;
        TestResult = "연결 테스트 중...";
        IsTestOk   = false;
        SaveResult = "";

        try
        {
            var tempDb = new DbSettings
            {
                Server            = Server,
                Port              = Port,
                Database          = Database,
                AuthMode          = AuthMode,
                Username          = Username,
                Password          = Password,
                ConnectionTimeout = ConnectionTimeout,
                CommandTimeout    = CommandTimeout,
            };
            bool ok = await new DatabaseService(tempDb).TestConnectionAsync();
            TestResult = ok ? "● 연결 성공" : "✕ 연결 실패 — 설정을 확인하세요";
            IsTestOk   = ok;
        }
        catch (Exception ex)
        {
            TestResult = $"✕ 오류: {ex.Message}";
            IsTestOk   = false;
        }
        finally
        {
            IsTesting = false;
        }
    }

    // ── 설정 저장 ──────────────────────────────────────────────────
    private void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(Server))   { SaveResult = "⚠ 서버 주소를 입력하세요.";       return; }
        if (string.IsNullOrWhiteSpace(Database))  { SaveResult = "⚠ 데이터베이스 이름을 입력하세요."; return; }
        if (string.IsNullOrWhiteSpace(DataFolder)){ SaveResult = "⚠ 데이터 폴더를 지정하세요.";     return; }

        try
        {
            // AppConfig 업데이트
            _cfg.Database.Server            = Server;
            _cfg.Database.Port              = Port;
            _cfg.Database.Database          = Database;
            _cfg.Database.AuthMode          = AuthMode;
            _cfg.Database.Username          = Username;
            _cfg.Database.Password          = Password;
            _cfg.Database.ConnectionTimeout = ConnectionTimeout;
            _cfg.Database.CommandTimeout    = CommandTimeout;

            _cfg.Loader.ScanIntervalMinutes  = ScanIntervalMinutes;
            _cfg.Loader.BatchSize            = BatchSize;
            _cfg.Loader.DataFolder           = DataFolder;
            _cfg.Loader.ArchiveFolder        = ArchiveFolder;
            _cfg.Loader.CrawlerBatPath        = CrawlerBatPath;
            _cfg.Loader.CrawlerTimeoutMinutes = CrawlerTimeoutMinutes;
            _cfg.Loader.CrawlerEnabled        = CrawlerEnabled;
            _cfg.Loader.EtlEnabled            = EtlEnabled;
            _cfg.Loader.CrawlerIntervalMinutes = CrawlerIntervalMinutes;

            _cfg.Save();

            SaveResult = $"✓ 설정 저장 완료  ({_cfg.ConfigFilePath})";
            TestResult = "";

            SettingsSaved?.Invoke(_cfg);
        }
        catch (Exception ex)
        {
            SaveResult = $"✕ 저장 실패: {ex.Message}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
