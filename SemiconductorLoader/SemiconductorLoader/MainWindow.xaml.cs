using System.Windows;
using System.Windows.Controls;
using SemiconductorLoader.Config;
using SemiconductorLoader.ViewModels;

namespace SemiconductorLoader;

public partial class MainWindow : Window
{
    private readonly MainViewModel     _mainVm;
    private readonly SettingsViewModel _settingsVm;
    private readonly HistoryViewModel  _historyVm;

    public MainWindow()
    {
        InitializeComponent();

        var cfg    = AppConfig.Load();
        _mainVm     = new MainViewModel(cfg);
        _settingsVm = new SettingsViewModel(cfg);
        _historyVm  = new HistoryViewModel(cfg);

        // 각 탭에 DataContext 설정
        monitorTab.DataContext  = _mainVm;
        settingsTab.DataContext = _settingsVm;
        historyTab.DataContext  = _historyVm;

        // PasswordBox는 데이터 바인딩 불가 — 초기값 직접 설정
        PwBox.Password = _settingsVm.Password;

        // 설정 저장 시: 서비스 재생성 + 이력 DB 갱신
        _settingsVm.SettingsSaved += cfg =>
        {
            _ = _mainVm.ApplySettingsAsync(cfg);
            _historyVm.UpdateSettings(cfg);
        };
    }

    // ── 윈도우 로드 ────────────────────────────────────────────
    private async void Window_Loaded(object sender, RoutedEventArgs e)
        => await _mainVm.InitializeAsync();

    // ── 로그 지우기 ────────────────────────────────────────────
    private void ClearLog_Click(object sender, RoutedEventArgs e)
        => _mainVm.Logs.Clear();

    // ── PasswordBox 변경 핸들러 ─────────────────────────────────
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _settingsVm.Password = ((PasswordBox)sender).Password;

    // ── 폴더 찾아보기 ───────────────────────────────────────────
    private void BrowseDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "데이터 폴더 선택",
            InitialDirectory = _settingsVm.DataFolder
        };
        if (dialog.ShowDialog() == true)
            _settingsVm.DataFolder = dialog.FolderName;
    }

    private void BrowseArchiveFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "아카이브 폴더 선택",
            InitialDirectory = _settingsVm.ArchiveFolder
        };
        if (dialog.ShowDialog() == true)
            _settingsVm.ArchiveFolder = dialog.FolderName;
    }

    private void BrowseCrawlerBat_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "크롤러 BAT 파일 선택",
            Filter = "배치 파일 (*.bat)|*.bat|모든 파일 (*.*)|*.*",
            InitialDirectory = string.IsNullOrWhiteSpace(_settingsVm.CrawlerBatPath)
                ? System.IO.Path.GetDirectoryName(_settingsVm.DataFolder) ?? ""
                : System.IO.Path.GetDirectoryName(_settingsVm.CrawlerBatPath) ?? "",
        };
        if (dialog.ShowDialog() == true)
            _settingsVm.CrawlerBatPath = dialog.FileName;
    }

    // ── 이력 탭 선택 시 자동 로드 ──────────────────────────────
    private async void TabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.Source is System.Windows.Controls.TabControl && historyTab.IsSelected)
            await _historyVm.LoadAsync();
    }
}
