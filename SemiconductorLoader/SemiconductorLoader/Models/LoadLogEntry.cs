namespace SemiconductorLoader.Models;

/// <summary>load_log 테이블 1행에 대응하는 이력 항목</summary>
public class LoadLogEntry
{
    public int     LogId      { get; set; }
    public string  FileName   { get; set; } = "";
    public int     TotalRows  { get; set; }
    public int     LoadedRows { get; set; }
    public int     FailedRows { get; set; }
    public string  Status     { get; set; } = "";
    public string? ErrorMsg   { get; set; }
    public DateTime StartedAt  { get; set; }
    public DateTime FinishedAt { get; set; }

    public TimeSpan Duration => FinishedAt - StartedAt;

    public string DurationText =>
        Duration.TotalSeconds < 60
            ? $"{Duration.TotalSeconds:F1}초"
            : Duration.TotalMinutes < 60
                ? $"{(int)Duration.TotalMinutes}분 {Duration.Seconds}초"
                : $"{Duration.TotalHours:F1}시간";

    /// <summary>StatusColor 바인딩용 (DataGrid 행 색상)</summary>
    public string StatusColor => Status switch
    {
        "SUCCESS" => "#4ADE80",
        "PARTIAL" => "#FBBF24",
        "FAILED"  => "#F87171",
        _         => "#9CA3AF",
    };
}
