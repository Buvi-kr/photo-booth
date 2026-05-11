using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;

public class QRServerManager : MonoBehaviour
{
    public static QRServerManager Instance { get; private set; }

    [Header("서버 상태")]
    public string currentTunnelUrl = "";
    public bool isServerReady = false;

    [Header("QR 코드 UI 연결")]
    public RawImage qrCodeDisplay;

    [Header("자동 복구 설정 (cloudflared 사망 시 자동 재시작)")]
    [Tooltip("cloudflared 사망 감지 시 자동 재시작 활성화")]
    public bool autoRestartOnDeath = true;
    [Tooltip("재시작 시도 사이 최소 간격 (초). 너무 짧으면 Cloudflare rate limit 위험")]
    [Range(5f, 300f)] public float restartCooldownSeconds = 15f;
    [Tooltip("1시간 내 최대 재시작 횟수 (rate limit/무한루프 방지)")]
    [Range(1, 20)] public int maxRestartsPerHour = 6;
    [Tooltip("앱 시작 후 이 시간 이내에는 강제 재시작 보류 (정상 부팅 대기)")]
    [Range(3f, 30f)] public float initialBootGracePeriod = 10f;

    private HttpListener httpListener;
    private Process cloudflaredProcess;
    private int port = 3000;
    private string photoDirectory;

    // ── 진단용 (cloudflared 동작/사망 원인 추적) ──
    private string _tunnelLogPath;
    private System.DateTime _tunnelStartTime;
    private readonly object _logFileLock = new object();
    private int _stderrLineCount = 0;
    private int _stdoutLineCount = 0;

    // ── 자동 재시작 상태 ──
    private volatile bool _restartRequested = false;   // 워커 스레드에서 set 가능
    private volatile bool _intentionalKill = false;    // 재시작용 의도된 kill 표시
    private bool _restartInProgress = false;
    private System.DateTime _lastRestartTime = System.DateTime.MinValue;
    private Queue<System.DateTime> _recentRestartTimes = new Queue<System.DateTime>();

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void Start()
    {
        photoDirectory = Path.Combine(Application.dataPath, "MyPhotoBooth");
        if (!Directory.Exists(photoDirectory))
            Directory.CreateDirectory(photoDirectory);

        StartLocalWebServer();
        StartCloudflareTunnel();
    }

    // ──────────────────────────────────────────
    // 1. 미니 웹서버
    // ──────────────────────────────────────────
    private void StartLocalWebServer()
    {
        httpListener = new HttpListener();
        bool started = false;

        for (int i = 0; i < 10; i++)
        {
            try
            {
                httpListener.Prefixes.Clear();
                httpListener.Prefixes.Add($"http://127.0.0.1:{port}/");
                httpListener.Start();
                started = true;
                break;
            }
            catch (HttpListenerException)
            {
                port++; // 포트가 사용중이면 다음 포트 시도
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[Web Server] 알 수 없는 오류: {ex.Message}");
                break;
            }
        }

        if (started)
        {
            UnityEngine.Debug.Log($"[Web Server] 내부 서버가 포트 {port}에서 켜졌습니다.");
            Task.Run(() => ListenForRequests());
        }
        else
        {
            UnityEngine.Debug.LogError("[Web Server] 포트 할당에 실패했습니다. 이전 백그라운드 프로세스가 남아있는지 확인하세요.");
        }
    }

    private async Task ListenForRequests()
    {
        while (httpListener.IsListening)
        {
            try
            {
                HttpListenerContext context = await httpListener.GetContextAsync();
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;

                string rawPath = request.Url.AbsolutePath;

                // /raw/파일명 → 실제 이미지 파일 전송
                if (rawPath.StartsWith("/raw/"))
                {
                    string fileName = rawPath.Substring("/raw/".Length);
                    string filePath = Path.Combine(photoDirectory, fileName);

                    if (File.Exists(filePath))
                    {
                        byte[] fileBytes = File.ReadAllBytes(filePath);

                        // JPG / PNG 확장자에 따라 Content-Type 자동 결정
                        string ext = Path.GetExtension(fileName).ToLower();
                        response.ContentType = ext == ".jpg" || ext == ".jpeg"
                            ? "image/jpeg"
                            : "image/png";

                        response.ContentLength64 = fileBytes.Length;
                        response.StatusCode = (int)HttpStatusCode.OK;
                        await response.OutputStream.WriteAsync(fileBytes, 0, fileBytes.Length);
                    }
                    else
                    {
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                    }
                }
                // /Photo_파일명 → 모바일 다운로드 페이지
                else if (rawPath.StartsWith("/Photo_"))
                {
                    string fileName = rawPath.TrimStart('/');
                    string displayName = Uri.UnescapeDataString(fileName);

                    // 다운로드 파일명도 확장자 맞춤
                    string downloadName = "천문과학관_우주사진" +
                        Path.GetExtension(fileName).ToLower();

                    string htmlResponse = $@"<!DOCTYPE html>
<html lang='ko'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>천문과학관 포토부스</title>
    <style>
        body {{ margin:0; padding:20px; background:#0b0c10; color:#fff;
               display:flex; flex-direction:column; align-items:center;
               font-family:'Malgun Gothic',sans-serif; }}
        .title {{ font-size:1.3rem; font-weight:bold; margin-bottom:20px;
                  color:#66fcf1; text-align:center; line-height:1.4; }}
        img {{ max-width:100%; height:auto; border-radius:15px;
               box-shadow:0 8px 20px rgba(0,0,0,.6); margin-bottom:25px;
               border:2px solid #45a29e; }}
        .download-btn {{ background:#45a29e; color:#0b0c10; text-decoration:none;
                         font-size:1.2rem; font-weight:bold; padding:15px 30px;
                         border-radius:30px; display:inline-block;
                         width:80%; max-width:300px; text-align:center; }}
        .download-btn:active {{ background:#66fcf1; transform:scale(.98); }}
        .guide {{ font-size:.9rem; color:#c5c6c7; margin-top:15px;
                  text-align:center; word-break:keep-all; }}
    </style>
</head>
<body>
    <div class='title'>🌌 천문과학관 🌌<br>우주 탐험 기념사진 🚀</div>
    <img src='/raw/{fileName}' alt='우주 배경 합성 사진' />
    <a href='/raw/{fileName}' download='{downloadName}' class='download-btn'>
        📥 앨범에 사진 저장하기
    </a>
    <div class='guide'>
        (아이폰 등 일부 기기는 사진을 길게 눌러 '저장'을 선택해주세요)
    </div>
</body>
</html>";

                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(htmlResponse);
                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    response.StatusCode = (int)HttpStatusCode.OK;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                }

                response.OutputStream.Close();
            }
            catch (Exception)
            {
                // 정상 종료 시 발생하는 예외 무시
            }
        }
    }

    // ──────────────────────────────────────────
    // 2. Cloudflare 터널
    // ──────────────────────────────────────────
    private void StartCloudflareTunnel()
    {
        string cloudflaredPath = Path.Combine(Application.streamingAssetsPath, "cloudflared.exe");

        if (!File.Exists(cloudflaredPath))
        {
            UnityEngine.Debug.LogError("[Tunnel] StreamingAssets 폴더에 cloudflared.exe 파일이 없습니다!");
            return;
        }

        // ── 진단 로그 파일 경로 준비 (날짜별) ──
        InitTunnelLogFile();

        // ── 잔존 cloudflared 프로세스 강제 종료 (이전 실행 충돌 방지) ──
        // _intentionalKill = true 로 표시 → Exited 이벤트가 자동재시작 루프 트리거 안 하게
        _intentionalKill = true;
        try
        {
            var existing = System.Diagnostics.Process.GetProcessesByName("cloudflared");
            foreach (var p in existing)
            {
                try { p.Kill(); p.WaitForExit(1000); } catch { }
                p.Dispose();
            }
            if (existing.Length > 0)
            {
                string msg = $"[Tunnel] 잔존 cloudflared 프로세스 {existing.Length}개 강제 종료 완료.";
                UnityEngine.Debug.Log(msg);
                WriteTunnelLog($"[CLEANUP] {msg}");
            }
        }
        catch (Exception ex)
        {
            string msg = "[Tunnel] 잔존 프로세스 종료 중 오류: " + ex.Message;
            UnityEngine.Debug.LogWarning(msg);
            WriteTunnelLog($"[CLEANUP-ERR] {msg}");
        }
        // 잔존 정리 완료 → 의도된 kill 플래그 해제 (이후 사망은 비정상으로 간주)
        _intentionalKill = false;

        cloudflaredProcess = new Process();
        cloudflaredProcess.StartInfo.FileName = cloudflaredPath;
        cloudflaredProcess.StartInfo.Arguments = $"tunnel --url http://127.0.0.1:{port} --http-host-header 127.0.0.1";
        cloudflaredProcess.StartInfo.UseShellExecute = false;
        cloudflaredProcess.StartInfo.RedirectStandardError  = true;
        cloudflaredProcess.StartInfo.RedirectStandardOutput = true;  // ✨ stdout 도 캡처
        cloudflaredProcess.StartInfo.CreateNoWindow = true;
        cloudflaredProcess.EnableRaisingEvents = true;  // ✨ Exited 이벤트 받기 위함

        // ── stderr (cloudflared 는 대부분 stderr 로 출력) ──
        cloudflaredProcess.ErrorDataReceived += (sender, args) =>
        {
            if (string.IsNullOrEmpty(args.Data)) return;

            _stderrLineCount++;
            WriteTunnelLog($"[stderr] {args.Data}");

            // URL 추출 (성공 신호)
            Match match = Regex.Match(args.Data, @"https://[a-zA-Z0-9-]+\.trycloudflare\.com");
            if (match.Success && !isServerReady)
            {
                currentTunnelUrl = match.Value;
                isServerReady = true;
                double bootSec = (System.DateTime.Now - _tunnelStartTime).TotalSeconds;
                string ok = $"\n🚀 [성공] 오늘의 외부 접속 주소: {currentTunnelUrl} (부팅 {bootSec:F1}s)\n";
                UnityEngine.Debug.Log(ok);
                WriteTunnelLog($"[URL]   {currentTunnelUrl}");
                WriteTunnelLog($"[READY] isServerReady=true (부팅 {bootSec:F1}s, stderr {_stderrLineCount}줄째)");
            }
        };

        // ── stdout (예외적이지만 일부 메시지가 여기로 올 수도) ──
        cloudflaredProcess.OutputDataReceived += (sender, args) =>
        {
            if (string.IsNullOrEmpty(args.Data)) return;
            _stdoutLineCount++;
            WriteTunnelLog($"[stdout] {args.Data}");
        };

        // ── 프로세스 종료 감지 (좀비/크래시/세션만료 진단의 핵심) ──
        cloudflaredProcess.Exited += (sender, args) =>
        {
            try
            {
                int code = cloudflaredProcess.ExitCode;
                System.TimeSpan uptime = System.DateTime.Now - _tunnelStartTime;
                string codeMeaning = ExitCodeMeaning(code);
                bool intentional = _intentionalKill;
                string msg = $"[EXITED] cloudflared 종료. ExitCode={code} ({codeMeaning}), " +
                             $"가동시간={uptime.TotalHours:F2}h ({uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}), " +
                             $"stderr {_stderrLineCount}줄/stdout {_stdoutLineCount}줄, intentional={intentional}";
                UnityEngine.Debug.LogWarning("🔴 " + msg);
                WriteTunnelLog(msg);
                WriteTunnelLog($"[STATE] isServerReady 직전값: {isServerReady}, URL: {currentTunnelUrl}");

                // 죽었으면 서버 상태 갱신 (다음 사진은 QR 없이 처리되도록)
                isServerReady = false;
                currentTunnelUrl = "";

                // 의도된 kill (재시작용)이 아니면 자동 재시작 요청 플래그 ON
                // Update() 가 main thread 에서 이 플래그를 보고 코루틴 실행
                if (!intentional && autoRestartOnDeath)
                {
                    _restartRequested = true;
                    WriteTunnelLog("[RESTART] 비정상 종료 감지 → 자동 재시작 요청됨");
                }
            }
            catch (Exception ex)
            {
                WriteTunnelLog($"[EXITED-ERR] 종료 핸들러 오류: {ex.Message}");
            }
        };

        _tunnelStartTime    = System.DateTime.Now;
        _stderrLineCount    = 0;
        _stdoutLineCount    = 0;
        WriteTunnelLog($"[START] cloudflared 시작. cmd: {cloudflaredProcess.StartInfo.FileName} " +
                       $"{cloudflaredProcess.StartInfo.Arguments}");

        cloudflaredProcess.Start();
        cloudflaredProcess.BeginErrorReadLine();
        cloudflaredProcess.BeginOutputReadLine();

        WriteTunnelLog($"[START] PID={cloudflaredProcess.Id} 부팅 시작됨.");
    }

    /// <summary>
    /// 날짜별 진단 로그 파일 경로 초기화. MyPhotoBooth/logs/tunnel_yyyyMMdd.log
    /// 자동 정리 코루틴이 Photo_*.jpg 만 대상으로 하므로 로그는 영향 없음.
    /// </summary>
    private void InitTunnelLogFile()
    {
        try
        {
            string logDir = Path.Combine(photoDirectory, "logs");
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            _tunnelLogPath = Path.Combine(logDir, $"tunnel_{System.DateTime.Now:yyyyMMdd}.log");

            // 세션 헤더 (재시작/재부팅 구분에 핵심)
            WriteTunnelLog($"\n========== 새 세션 시작 {System.DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========");
            WriteTunnelLog($"[ENV] OS={SystemInfo.operatingSystem}, Unity={Application.unityVersion}");
            WriteTunnelLog($"[ENV] DeviceName={SystemInfo.deviceName}, port={port}");
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[Tunnel/Log] 로그 파일 초기화 실패: {ex.Message}");
            _tunnelLogPath = null;
        }
    }

    /// <summary>
    /// 디스크 로그 파일에 1줄 기록. 스레드 안전 (stderr/stdout 이 다른 스레드에서 호출됨).
    /// 실패해도 앱 동작에 영향 없도록 모든 예외 흡수.
    /// </summary>
    private void WriteTunnelLog(string line)
    {
        if (string.IsNullOrEmpty(_tunnelLogPath)) return;
        try
        {
            string stamped = $"[{System.DateTime.Now:HH:mm:ss.fff}] {line}\n";
            lock (_logFileLock)
            {
                File.AppendAllText(_tunnelLogPath, stamped);
            }
        }
        catch
        {
            // 로그 실패는 무시 (앱 동작 보호)
        }
    }

    /// <summary>cloudflared 종료 코드 의미를 사람이 읽을 수 있게 변환.</summary>
    private string ExitCodeMeaning(int code)
    {
        switch (code)
        {
            case 0:             return "정상 종료";
            case 1:             return "일반 에러 (네트워크/터널 실패 가능)";
            case 2:             return "잘못된 인자";
            case -1:            return "Kill() 호출됨";
            case -1073741819:   return "Access Violation (크래시)";
            case -1073741510:   return "사용자 강제 종료 (Ctrl+C)";
            default:            return $"미분류 코드";
        }
    }

    // ──────────────────────────────────────────
    // 5. 자동 재시작 (cloudflared 사망 또는 수동 요청 시)
    // ──────────────────────────────────────────

    /// <summary>
    /// 외부(PhotoCaptureManager 등)에서 명시적으로 재시작 요청.
    /// 실제 재시작은 Update() 에서 메인 스레드로 처리됨 (스레드 안전).
    /// 모든 가드(쿨다운, rate limit, 부팅 grace period)는 AttemptRestart() 에서 적용.
    /// </summary>
    public void RequestRestart()
    {
        _restartRequested = true;
    }

    /// <summary>
    /// 메인 스레드 진입점. Process.Exited 는 워커 스레드에서 호출되므로
    /// 코루틴 시작 같은 Unity API 는 여기서 처리.
    /// </summary>
    private void Update()
    {
        if (_restartRequested && !_restartInProgress)
        {
            _restartRequested = false;
            StartCoroutine(AttemptRestart());
        }
    }

    /// <summary>
    /// 견고한 자동 재시작 코루틴. 다중 가드:
    ///   ① 부팅 grace period 이내 → 재시작 보류 (정상 부팅 대기)
    ///   ② 시간당 최대 횟수 초과 → 보류 (Cloudflare rate limit 보호)
    ///   ③ 마지막 재시작 후 쿨다운 미충족 → 보류 (연속 재시작 차단)
    /// 모든 결정은 디스크 로그에 기록 → 사후 진단 가능.
    /// </summary>
    private IEnumerator AttemptRestart()
    {
        _restartInProgress = true;

        // 잠깐 양보 (Exited 이벤트 직후 즉시 재시작하면 OS가 못 따라옴)
        yield return new WaitForSeconds(1f);

        System.DateTime now = System.DateTime.Now;

        // ── Guard 1: 부팅 grace period ──
        double sinceStart = (now - _tunnelStartTime).TotalSeconds;
        if (sinceStart < initialBootGracePeriod)
        {
            WriteTunnelLog($"[RESTART] ⏳ 부팅 중 ({sinceStart:F1}s < {initialBootGracePeriod}s grace) → 재시작 보류");
            _restartInProgress = false;
            yield break;
        }

        // ── Guard 2: 시간당 횟수 제한 ──
        // 1시간 지난 항목 제거
        while (_recentRestartTimes.Count > 0 && (now - _recentRestartTimes.Peek()).TotalHours >= 1.0)
            _recentRestartTimes.Dequeue();

        if (_recentRestartTimes.Count >= maxRestartsPerHour)
        {
            WriteTunnelLog($"[RESTART] ❌ 시간당 한도({maxRestartsPerHour})초과 → 재시작 차단. " +
                           $"Cloudflare rate limit 우려로 1시간 후 자동 재허용.");
            _restartInProgress = false;
            yield break;
        }

        // ── Guard 3: 마지막 재시작 후 쿨다운 ──
        double sinceLast = (now - _lastRestartTime).TotalSeconds;
        if (sinceLast < restartCooldownSeconds)
        {
            WriteTunnelLog($"[RESTART] ⏳ 쿨다운 중 ({sinceLast:F1}s < {restartCooldownSeconds}s) → 보류");
            _restartInProgress = false;
            yield break;
        }

        // ── 통과! 재시작 실행 ──
        _lastRestartTime = now;
        _recentRestartTimes.Enqueue(now);
        WriteTunnelLog($"[RESTART] 🔄 자동 재시작 #{_recentRestartTimes.Count}/시간 실행");

        // 상태 초기화
        isServerReady = false;
        currentTunnelUrl = "";

        // StartCloudflareTunnel 은 자체적으로 잔존 프로세스 정리 후 새로 시작
        StartCloudflareTunnel();

        _restartInProgress = false;
    }

    // ──────────────────────────────────────────
    // 3. QR 생성 (촬영 + 재합성 둘 다 호출)
    // ──────────────────────────────────────────
    public void GenerateQRCodeForFile(string fileName)
    {
        if (!isServerReady || string.IsNullOrEmpty(currentTunnelUrl))
        {
            UnityEngine.Debug.LogError("⚠️ 서버가 아직 주소를 발급받지 못했습니다.");
            return;
        }

        string fullUrl = $"{currentTunnelUrl}/{fileName}";

        if (qrCodeDisplay != null) qrCodeDisplay.texture = null;
        StartCoroutine(DownloadQRCode(fullUrl));
    }

    private IEnumerator DownloadQRCode(string url)
    {
        string apiUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=512x512&data={UnityWebRequest.EscapeURL(url)}";

        using (UnityWebRequest uwr = UnityWebRequestTexture.GetTexture(apiUrl))
        {
            yield return uwr.SendWebRequest();

            if (uwr.result == UnityWebRequest.Result.Success)
            {
                Texture2D texture = DownloadHandlerTexture.GetContent(uwr);
                if (qrCodeDisplay != null) qrCodeDisplay.texture = texture;
                UnityEngine.Debug.Log($"[QR] 생성 완료: {url}");
            }
            else
            {
                UnityEngine.Debug.LogError("⚠️ QR 생성 실패: " + uwr.error);
            }
        }
    }

    // ──────────────────────────────────────────
    // 4. 앱 종료 시 정리
    // ──────────────────────────────────────────
    private void OnApplicationQuit()
    {
        if (httpListener != null)
        {
            try
            {
                if (httpListener.IsListening)
                    httpListener.Stop();
                httpListener.Close();
            }
            catch (Exception) {}
            finally
            {
                httpListener = null;
            }
        }

        if (cloudflaredProcess != null)
        {
            // 앱 종료에 의한 의도된 kill → 자동 재시작 트리거 방지
            _intentionalKill = true;
            try
            {
                bool wasAlive = !cloudflaredProcess.HasExited;
                if (wasAlive) cloudflaredProcess.Kill();

                System.TimeSpan uptime = System.DateTime.Now - _tunnelStartTime;
                WriteTunnelLog($"[SHUTDOWN] 앱 종료 시점. cloudflared 살아있음={wasAlive}, " +
                               $"가동시간={uptime.TotalHours:F2}h, isServerReady={isServerReady}");
            }
            catch (Exception ex)
            {
                WriteTunnelLog($"[SHUTDOWN-ERR] 종료 중 오류: {ex.Message}");
            }
            finally
            {
                try { cloudflaredProcess.Dispose(); } catch { }
                cloudflaredProcess = null;
            }
            UnityEngine.Debug.Log("[Tunnel] Cloudflare 터널 안전하게 종료.");
            WriteTunnelLog($"========== 세션 종료 {System.DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========\n");
        }
    }

    private void OnDestroy()
    {
        OnApplicationQuit();
    }
}