using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using SemiconductorLoader.Config;
using SemiconductorLoader.Models;
using SemiconductorLoader.Services;

namespace SemiconductorLoader.ViewModels;

/// <summary>ETL 작업 이력 및 통계를 표시하는 ViewModel</summary>
public sealed class HistoryViewModel : INotifyPropertyChanged
{
    private DatabaseService _db;

    // ── 바인딩 속성 ────────────────────────────────────────────────
    private string _statsText  = "통계 로드 중...";
    private string _loadStatus = "";
    private bool   _isLoading;

    public string StatsText  { get => _statsText;  private set { _statsText  = value; OnPC(); } }
    public string LoadStatus { get => _loadStatus; private set { _loadStatus = value; OnPC(); } }
    public bool   IsLoading  { get => _isLoading;  private set { _isLoading  = value; OnPC(); } }

    public ObservableCollection<LoadLogEntry> Entries { get; } = new();

    // ── 명령 ──────────────────────────────────────────────────────
    public ICommand RefreshCommand { get; }

    // ── 생성자 ──────────────────────────────────────────────────────
    public HistoryViewModel(AppConfig cfg)
    {
        _db = new DatabaseService(cfg.Database);
        RefreshCommand = new RelayCommand(_ => _ = LoadAsync(), _ => !IsLoading);
    }

    /// <summary>설정 변경 시 DatabaseService를 새 설정으로 교체</summary>
    public void UpdateSettings(AppConfig cfg)
        => _db = new DatabaseService(cfg.Database);

    // ── 이력 로드 ──────────────────────────────────────────────────
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading  = true;
        LoadStatus = "로딩 중...";

        try
        {
            var logsTask  = _db.QueryLogsAsync(500);
            var statsTask = _db.QueryStatsAsync();

            await Task.WhenAll(logsTask, statsTask);

            var logs  = logsTask.Result;
            var stats = statsTask.Result;

            // await 이후 UI 스레드(SynchronizationContext)에서 실행됨 — Dispatch 불필요
            Entries.Clear();
            foreach (var e in logs)
                Entries.Add(e);

            StatsText  = $"총 {stats.Total:N0}회 실행   ·   " +
                         $"성공 {stats.Success:N0}회   ·   " +
                         $"실패 {stats.Failed:N0}회   ·   " +
                         $"누적 {stats.LoadedRows:N0}건 적재";
            LoadStatus = $"{logs.Count}건 표시  (최신순)";
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Contains("Invalid object name")
                ? "이력 테이블 없음 — 첫 번째 ETL 실행 후 다시 확인하세요."
                : $"로드 실패: {ex.Message}";

            StatsText  = "통계 없음";
            LoadStatus = msg;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
