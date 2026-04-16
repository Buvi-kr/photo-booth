using System;
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

    private HttpListener httpListener;
    private Process cloudflaredProcess;
    private int port = 3000;
    private string photoDirectory;

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
        httpListener.Prefixes.Add($"http://127.0.0.1:{port}/");
        httpListener.Start();

        UnityEngine.Debug.Log($"[Web Server] 내부 서버가 포트 {port}에서 켜졌습니다.");
        Task.Run(() => ListenForRequests());
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

        cloudflaredProcess = new Process();
        cloudflaredProcess.StartInfo.FileName = cloudflaredPath;
        cloudflaredProcess.StartInfo.Arguments = $"tunnel --url http://127.0.0.1:{port} --http-host-header 127.0.0.1";
        cloudflaredProcess.StartInfo.UseShellExecute = false;
        cloudflaredProcess.StartInfo.RedirectStandardError = true;
        cloudflaredProcess.StartInfo.CreateNoWindow = true;

        cloudflaredProcess.ErrorDataReceived += (sender, args) =>
        {
            if (!string.IsNullOrEmpty(args.Data))
            {
                Match match = Regex.Match(args.Data, @"https://[a-zA-Z0-9-]+\.trycloudflare\.com");
                if (match.Success)
                {
                    currentTunnelUrl = match.Value;
                    isServerReady = true;
                    UnityEngine.Debug.Log($"\n🚀 [성공] 오늘의 외부 접속 주소: {currentTunnelUrl}\n");
                }
            }
        };

        cloudflaredProcess.Start();
        cloudflaredProcess.BeginErrorReadLine();
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
        if (httpListener != null && httpListener.IsListening)
        {
            httpListener.Stop();
            httpListener.Close();
        }

        if (cloudflaredProcess != null && !cloudflaredProcess.HasExited)
        {
            cloudflaredProcess.Kill();
            cloudflaredProcess.Dispose();
            UnityEngine.Debug.Log("[Tunnel] Cloudflare 터널 안전하게 종료.");
        }
    }
}