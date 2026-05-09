using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using SemiconductorLoader.Config;
using SemiconductorLoader.Models;
using SemiconductorLoader.Services;
using Timer = System.Timers.Timer;

namespace SemiconductorLoader.ViewModels;

// ── 간단한 RelayCommand ─────────────────────────────────────────
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?>      _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    { _execute = execute; _canExecute = canExecute; }

    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
    public void Execute(object? p)    => _execute(p);
}

// ── MainViewModel ───────────────────────────────────────────────
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private AppConfig     _cfg;
    private LoaderService _loader;

    // ── 타이머 (AutoReset=false: 실행 완료 후 수동 재시작) ──────
    private readonly Timer _crawlerTimer;
    private readonly Timer _etlTimer;
    private readonly Timer _healthTimer;

    private volatile bool _dbConnected;
    private int           _runGuard;   // 0=free, 1=running (Interlocked)
    private bool          _disposed;

    // ── Bindable 속성 ──────────────────────────────────────────
    private string _dbStatus    = "연결 확인 중...";
    private string _status      = "초기화 중";
    private string _lastRunInfo = "-";
    private bool   _isRunning;
    private int    _progressValue;
    private string _progressText = "";
    private bool   _isProgressing;

    private bool   _isCrawlerEnabled;
    private bool   _isEtlEnabled;
    private int    _crawlerIntervalMin;
    private int    _etlIntervalMin;
    private string _crawlerNextRunTime = "-";
    private string _etlNextRunTime     = "-";
    private string _crawlerStatus      = "중지됨";
    private string _etlStatus          = "중지됨";

    public string DbStatus      { get => _dbStatus;      private set { _dbStatus      = value; OnPC(); } }
    public string Status        { get => _status;        private set { _status        = value; OnPC(); } }
    public string LastRunInfo   { get => _lastRunInfo;   private set { _lastRunInfo   = value; OnPC(); } }
    public bool   IsRunning     { get => _isRunning;     private set { _isRunning     = value; OnPC(); InvalidateCommands(); } }
    public int    ProgressValue { get => _progressValue; private set { _progressValue = value; OnPC(); } }
    public string ProgressText  { get => _progressText;  private set { _progressText  = value; OnPC(); } }
    public bool   IsProgressing { get => _isProgressing; private set { _isProgressing = value; OnPC(); } }

    public string CrawlerNextRunTime { get => _crawlerNextRunTime; private set { _crawlerNextRunTime = value; OnPC(); } }
    public string EtlNextRunTime     { get => _etlNextRunTime;     private set { _etlNextRunTime     = value; OnPC(); } }
    public string CrawlerStatus      { get => _crawlerStatus;      private set { _crawlerStatus      = value; OnPC(); } }
    public string EtlStatus          { get => _etlStatus;          private set { _etlStatus          = value; OnPC(); } }

    /// <summary>크롤러 스케줄 ON/OFF — 토글 시 타이머 자동 시작·중지</summary>
    public bool IsCrawlerEnabled
    {
        get => _isCrawlerEnabled;
        set
        {
            if (_isCrawlerEnabled == value) return;
            _isCrawlerEnabled = value;
            OnPC();
            if (value) StartCrawlerSchedule();
            else       StopCrawlerSchedule();
        }
    }

    /// <summary>ETL 스케줄 ON/OFF — 토글 시 타이머 자동 시작·중지</summary>
    public bool IsEtlEnabled
    {
        get => _isEtlEnabled;
        set
        {
            if (_isEtlEnabled == value) return;
            _isEtlEnabled = value;
            OnPC();
            if (value) StartEtlSchedule();
            else       StopEtlSchedule();
        }
    }

    public int CrawlerIntervalMin
    {
        get => _crawlerIntervalMin;
        set { var v = Math.Max(1, value); if (_crawlerIntervalMin == v) return; _crawlerIntervalMin = v; OnPC(); }
    }

    public int EtlIntervalMin
    {
        get => _etlIntervalMin;
        set { var v = Math.Max(1, value); if (_etlIntervalMin == v) return; _etlIntervalMin = v; OnPC(); }
    }

    public ObservableCollection<string> Logs { get; } = new();

    public ICommand RunNowCommand { get; }

    // ── 생성자 ──────────────────────────────────────────────────
    public MainViewModel(AppConfig cfg)
    {
        _cfg                = cfg;
        _crawlerIntervalMin = cfg.Loader.CrawlerIntervalMinutes;
        _etlIntervalMin     = cfg.Loader.ScanIntervalMinutes;
        _isCrawlerEnabled   = cfg.Loader.CrawlerEnabled;
        _isEtlEnabled       = cfg.Loader.EtlEnabled;

        _loader = new LoaderService(cfg);
        SubscribeLoader();

        RunNowCommand = new RelayCommand(_ => _ = RunNowManualAsync(), _ => !IsRunning);

        _crawlerTimer = new Timer { AutoReset = false };
        _crawlerTimer.Elapsed += async (_, _) => await RunCrawlerOnlyAsync();

        _etlTimer = new Timer { AutoReset = false };
        _etlTimer.Elapsed += async (_, _) => await RunEtlCycleAsync();

        // DB 헬스 체크: 5분 간격
        _healthTimer = new Timer(TimeSpan.FromMinutes(5).TotalMilliseconds) { AutoReset = true };
        _healthTimer.Elapsed += async (_, _) => await HealthCheckAsync();
    }

    // ── 로더 이벤트 구독 관리 ──────────────────────────────────
    private void SubscribeLoader()
    {
        _loader.OnLog           += OnLoaderLog;
        _loader.OnFileProcessed += OnFileProcessed;
        _loader.OnBulkProgress  += OnBulkProgress;
    }

    private void UnsubscribeLoader()
    {
        _loader.OnLog           -= OnLoaderLog;
        _loader.OnFileProcessed -= OnFileProcessed;
        _loader.OnBulkProgress  -= OnBulkProgress;
    }

    private void OnLoaderLog(string msg) => AddLog(msg);

    private void OnFileProcessed(LoadResult r) =>
        Dispatch(() => LastRunInfo = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}   {r.LoadedRows}건 적재  [{r.Status}]");

    private void OnBulkProgress(int loaded, int total) =>
        Dispatch(() =>
        {
            ProgressValue = total > 0 ? (int)(loaded * 100L / total) : 0;
            ProgressText  = $"DB 적재 중...  {loaded:N0} / {total:N0}건  ({ProgressValue}%)";
            IsProgressing = loaded < total;
        });

    // ── 초기화 (앱 시작 시 한 번 호출) ────────────────────────
    public async Task InitializeAsync()
    {
        AddLog("─────────────────────────────────────────");
        AddLog("반도체 논문 DB 적재기 시작");
        AddLog($"data 폴더: {_cfg.Loader.DataFolder}");
        AddLog($"DB: {_cfg.Database.DisplayInfo}");

        bool ok = await _loader.TestConnectionAsync();

        if (ok)
        {
            _dbConnected = true;
            DbStatus = $"● 연결됨   ({_cfg.Database.DisplayInfo})";
            AddLog("DB 연결 성공");

            try
            {
                await _loader.InitializeAsync();
                AddLog("테이블 준비 완료 (pre_table, load_log)");
            }
            catch (Exception ex)
            {
                AddLog($"[경고] 테이블 초기화 오류: {ex.Message}");
            }

            _healthTimer.Start();

            if (_isCrawlerEnabled) StartCrawlerSchedule();
            if (_isEtlEnabled)     StartEtlSchedule();
            if (!_isCrawlerEnabled && !_isEtlEnabled)
                Status = "대기 중 (모든 스케줄 중지됨)";
        }
        else
        {
            _dbConnected = false;
            DbStatus = $"✕ 연결 실패   ({_cfg.Database.DisplayInfo})";
            AddLog("DB 연결 실패 — 설정 탭에서 접속 정보를 확인하세요.");
            Status = "DB 연결 실패";
        }
    }

    // ── 설정 변경 적용 ─────────────────────────────────────────
    public async Task ApplySettingsAsync(AppConfig newCfg)
    {
        _crawlerTimer.Stop();
        _etlTimer.Stop();
        _healthTimer.Stop();

        UnsubscribeLoader();
        _cfg    = newCfg;
        _loader = new LoaderService(newCfg);
        SubscribeLoader();

        AddLog("─────────────────────────────────────────");
        AddLog($"설정 변경 적용 중: {newCfg.Database.DisplayInfo}");

        _crawlerIntervalMin = newCfg.Loader.CrawlerIntervalMinutes;
        _etlIntervalMin     = newCfg.Loader.ScanIntervalMinutes;
        _isCrawlerEnabled   = newCfg.Loader.CrawlerEnabled;
        _isEtlEnabled       = newCfg.Loader.EtlEnabled;
        Dispatch(() =>
        {
            OnPC(nameof(CrawlerIntervalMin));
            OnPC(nameof(EtlIntervalMin));
            OnPC(nameof(IsCrawlerEnabled));
            OnPC(nameof(IsEtlEnabled));
        });

        bool ok = await _loader.TestConnectionAsync();

        Dispatch(() =>
        {
            if (ok)
            {
                _dbConnected = true;
                DbStatus = $"● 연결됨   ({newCfg.Database.DisplayInfo})";
                AddLog("DB 연결 성공 — 설정이 적용되었습니다.");
                _healthTimer.Start();
                if (_isCrawlerEnabled) StartCrawlerSchedule();
                if (_isEtlEnabled)     StartEtlSchedule();
                if (!_isCrawlerEnabled && !_isEtlEnabled)
                    Status = "대기 중 (모든 스케줄 중지됨)";
            }
            else
            {
                _dbConnected = false;
                DbStatus = $"✕ 연결 실패   ({newCfg.Database.DisplayInfo})";
                AddLog("DB 연결 실패 — 설정을 다시 확인하세요.");
                Status = "DB 연결 실패";
            }
        });
    }

    // ── DB 헬스 체크 ───────────────────────────────────────────
    private async Task HealthCheckAsync()
    {
        if (!_dbConnected) return;
        bool ok = await _loader.TestConnectionAsync();
        if (!ok)
        {
            _dbConnected = false;
            Dispatch(() =>
            {
                DbStatus = $"⚠ 연결 이상   ({_cfg.Database.DisplayInfo})";
                AddLog("[경고] DB 연결 상태 이상 감지 — 설정 탭에서 재연결 또는 설정을 확인하세요.");
            });
        }
    }

    // ── 스케줄 제어 ─────────────────────────────────────────────

    private void StartCrawlerSchedule()
    {
        _crawlerTimer.Interval = TimeSpan.FromMinutes(_crawlerIntervalMin).TotalMilliseconds;
        _crawlerTimer.Start();
        Dispatch(() =>
        {
            CrawlerStatus      = "스케줄 실행 중";
            CrawlerNextRunTime = DateTime.Now.AddMinutes(_crawlerIntervalMin).ToString("yyyy-MM-dd HH:mm:ss");
            // Bug 3 수정: CommandManager는 UI 스레드(Dispatch 내부)에서 호출해야 함
            InvalidateCommands();
        });
        AddLog($"크롤러 스케줄 시작 ({_crawlerIntervalMin}분 간격)");
    }

    private void StopCrawlerSchedule()
    {
        _crawlerTimer.Stop();
        Dispatch(() =>
        {
            CrawlerStatus      = "중지됨";
            CrawlerNextRunTime = "-";
            InvalidateCommands();
        });
        AddLog("크롤러 스케줄 중지");
    }

    private void StartEtlSchedule()
    {
        _etlTimer.Interval = TimeSpan.FromMinutes(_etlIntervalMin).TotalMilliseconds;
        _etlTimer.Start();
        Dispatch(() =>
        {
            EtlStatus      = "스케줄 실행 중";
            EtlNextRunTime = DateTime.Now.AddMinutes(_etlIntervalMin).ToString("yyyy-MM-dd HH:mm:ss");
            Status         = ComputeStatus();
            InvalidateCommands();
        });
        AddLog($"ETL 스케줄 시작 ({_etlIntervalMin}분 간격)");
    }

    private void StopEtlSchedule()
    {
        _etlTimer.Stop();
        Dispatch(() =>
        {
            EtlStatus      = "중지됨";
            EtlNextRunTime = "-";
            Status         = ComputeStatus();
            InvalidateCommands();
        });
        AddLog("ETL 스케줄 중지");
    }

    private void RestartCrawlerTimer()
    {
        if (!_isCrawlerEnabled) return;
        _crawlerTimer.Interval = TimeSpan.FromMinutes(_crawlerIntervalMin).TotalMilliseconds;
        _crawlerTimer.Start();
        Dispatch(() => CrawlerNextRunTime = DateTime.Now.AddMinutes(_crawlerIntervalMin).ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private void RestartEtlTimer()
    {
        if (!_isEtlEnabled) return;
        _etlTimer.Interval = TimeSpan.FromMinutes(_etlIntervalMin).TotalMilliseconds;
        _etlTimer.Start();
        Dispatch(() => EtlNextRunTime = DateTime.Now.AddMinutes(_etlIntervalMin).ToString("yyyy-MM-dd HH:mm:ss"));
    }

    // ── 크롤러 단독 실행 (크롤러 타이머 콜백) ──────────────────
    private async Task RunCrawlerOnlyAsync()
    {
        if (Interlocked.CompareExchange(ref _runGuard, 1, 0) != 0)
        {
            RestartCrawlerTimer();
            return;
        }

        Dispatch(() => CrawlerStatus = "실행 중...");

        try
        {
            await _loader.RunCrawlerAsync();
        }
        catch (Exception ex)
        {
            AddLog($"[크롤러 오류] {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _runGuard, 0);
            Dispatch(() => CrawlerStatus = _isCrawlerEnabled ? "스케줄 실행 중" : "중지됨");
            RestartCrawlerTimer();
        }
    }

    // ── ETL 사이클 실행 (ETL 타이머 콜백) ──────────────────────
    private async Task RunEtlCycleAsync()
    {
        if (Interlocked.CompareExchange(ref _runGuard, 1, 0) != 0)
        {
            RestartEtlTimer();
            return;
        }

        Dispatch(() => { IsRunning = true; EtlStatus = "실행 중..."; Status = "실행 중..."; });
        AddLog("━━━━━ 실행 시작 ━━━━━");

        try
        {
            if (_isCrawlerEnabled)
                await _loader.RunCrawlerAsync();

            await _loader.RunEtlAsync();
        }
        catch (Exception ex)
        {
            AddLog($"[오류] {ex.Message}");
        }
        finally
        {
            AddLog("━━━━━ 실행 완료 ━━━━━");
            Dispatch(() =>
            {
                IsRunning     = false;
                IsProgressing = false;
                ProgressValue = 0;
                ProgressText  = "";
                EtlStatus     = _isEtlEnabled ? "스케줄 실행 중" : "중지됨";
                Status        = ComputeStatus();
            });
            Interlocked.Exchange(ref _runGuard, 0);
            RestartEtlTimer();
        }
    }

    // ── 수동 실행 ───────────────────────────────────────────────
    private async Task RunNowManualAsync()
    {
        if (Interlocked.CompareExchange(ref _runGuard, 1, 0) != 0) return;

        _crawlerTimer.Stop();
        _etlTimer.Stop();

        Dispatch(() => { IsRunning = true; Status = "실행 중..."; });
        AddLog("━━━━━ 수동 실행 시작 ━━━━━");

        try
        {
            bool crawlerOn = _isCrawlerEnabled;
            bool etlOn     = _isEtlEnabled;

            if (!crawlerOn && !etlOn)
                await _loader.RunAsync();
            else
            {
                if (crawlerOn) await _loader.RunCrawlerAsync();
                if (etlOn)     await _loader.RunEtlAsync();
            }
        }
        catch (Exception ex)
        {
            AddLog($"[오류] {ex.Message}");
        }
        finally
        {
            AddLog("━━━━━ 수동 실행 완료 ━━━━━");
            Dispatch(() =>
            {
                IsRunning     = false;
                IsProgressing = false;
                ProgressValue = 0;
                ProgressText  = "";
                Status        = ComputeStatus();
            });
            Interlocked.Exchange(ref _runGuard, 0);
            if (_isCrawlerEnabled) RestartCrawlerTimer();
            if (_isEtlEnabled)     RestartEtlTimer();
        }
    }

    // ── 헬퍼 ────────────────────────────────────────────────────

    /// <summary>현재 스케줄 상태에 따른 Status 문자열 계산 (중복 로직 통합).</summary>
    private string ComputeStatus() =>
        _isEtlEnabled      ? "스케줄 실행 중" :
        _isCrawlerEnabled  ? "크롤러 스케줄 실행 중" :
                             "대기 중";

    private void AddLog(string message)
    {
        Dispatch(() =>
        {
            Logs.Add(message);
            while (Logs.Count > 1000)
                Logs.RemoveAt(0);
        });
    }

    /// <summary>
    /// UI 스레드에서 액션을 비동기 실행.
    /// CommandManager 등 UI 전용 API는 반드시 이 안에서 호출해야 한다.
    /// </summary>
    private static void Dispatch(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);

    /// <summary>CommandManager를 UI 스레드에서 안전하게 무효화.</summary>
    private static void InvalidateCommands() =>
        CommandManager.InvalidateRequerySuggested();

    // ── IDisposable ─────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _crawlerTimer.Stop();
        _etlTimer.Stop();
        _healthTimer.Stop();
        _crawlerTimer.Dispose();
        _etlTimer.Dispose();
        _healthTimer.Dispose();
        UnsubscribeLoader();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
