using System.Diagnostics;
using System.IO;
using System.Text;

namespace SemiconductorLoader.Services;

/// <summary>
/// CSV 크롤러 BAT 파일을 비동기로 실행하는 서비스.
///
/// 설계 원칙:
/// ① CreateNoWindow + Redirect — 새 창 없이 조용히 실행, stdout/stderr 캡처
/// ② BeginOutputReadLine / BeginErrorReadLine — 비동기 읽기 (버퍼 데드락 방지)
/// ③ StandardInput.Close() — BAT 끝의 `pause` 명령이 stdin EOF 읽고 즉시 반환
/// ④ Kill(entireProcessTree) — 타임아웃 초과 시 자식 프로세스까지 강제 종료
/// </summary>
public class CrawlerService
{
    /// <summary>
    /// BAT 파일을 실행하고 완료될 때까지 대기한다.
    /// </summary>
    /// <param name="batPath">실행할 .bat 파일의 절대 경로</param>
    /// <param name="timeoutMinutes">최대 대기 시간 (분). 초과 시 강제 종료.</param>
    /// <param name="log">각 단계와 stdout/stderr 출력을 전달할 콜백</param>
    /// <returns>정상 완료(ExitCode=0)이면 true, 실패/타임아웃이면 false</returns>
    public async Task<bool> RunAsync(string batPath, int timeoutMinutes, Action<string> log)
    {
        // ── 경로 유효성 확인 ──────────────────────────────────
        if (string.IsNullOrWhiteSpace(batPath))
        {
            log("[크롤러] BAT 경로 미설정 — 건너뜀");
            return true;
        }
        if (!File.Exists(batPath))
        {
            log($"[크롤러] 파일 없음 — 건너뜀: {batPath}");
            return true;
        }

        var batDir = Path.GetDirectoryName(batPath)!;
        log($"━━━━━ 크롤러 시작: {Path.GetFileName(batPath)} ━━━━━");

        var psi = new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            Arguments              = $"/C \"{batPath}\"",
            WorkingDirectory       = batDir,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            // BAT 내부 chcp 65001 과 일치하도록 UTF-8 지정
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        // Python stdout/stderr 를 UTF-8 로 강제 (이모지·한글 등 cp949 외 문자 처리)
        // PYTHONUTF8=1 : Python 3.7+ UTF-8 모드 (권장)
        // PYTHONIOENCODING : 구버전 호환 fallback
        psi.EnvironmentVariables["PYTHONUTF8"]       = "1";
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // stdout/stderr 비동기 수신
        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                log($"[크롤러]  {e.Data}");
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                log($"[크롤러!] {e.Data}");
        };

        var started = DateTime.Now;
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // pause 명령에 stdin EOF 전달 → 대기 없이 즉시 통과
        proc.StandardInput.Close();

        // 타임아웃 비동기 대기
        var timeoutMs = (int)TimeSpan.FromMinutes(timeoutMinutes).TotalMilliseconds;
        bool finished = await Task.Run(() => proc.WaitForExit(timeoutMs));

        if (!finished)
        {
            log($"[경고] 크롤러 제한시간 {timeoutMinutes}분 초과 — 프로세스 강제 종료");
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception ex) { log($"[크롤러] 종료 오류: {ex.Message}"); }
            return false;
        }

        // WaitForExit(timeout) 후 비동기 스트림이 완전히 소비되도록 flush
        proc.WaitForExit();

        var elapsed = (DateTime.Now - started).TotalSeconds;
        var ok = proc.ExitCode == 0;
        log($"━━━━━ 크롤러 완료: {elapsed:F1}초  (종료 코드: {proc.ExitCode}){(ok ? "" : "  ← 오류")} ━━━━━");
        return ok;
    }
}
