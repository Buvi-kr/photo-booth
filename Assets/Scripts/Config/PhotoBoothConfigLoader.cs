// =============================================================================
//  PhotoBoothConfigLoader.cs
//  포천아트밸리 천문과학관 무인 포토부스 — config.json 런타임 로더
//
//  ▪ 앱 시작 시 StreamingAssets/config.json 을 자동 로드
//  ▪ 싱글턴 패턴 — PhotoBoothConfigLoader.Instance.Config 로 전역 접근
//  ▪ F5 또는 ReloadConfig() 호출로 에디터 재빌드 없이 핫리로드 가능
//  ▪ config.json 이 없으면 샘플 파일을 자동 생성 후 로드
//  ▪ 파싱 오류 시 기본값 객체로 안전하게 Fallback
// =============================================================================

using System.IO;
using UnityEngine;
using Newtonsoft.Json;

public class PhotoBoothConfigLoader : MonoBehaviour
{
    // ── 싱글턴 ────────────────────────────────────────────────────────────────
    public static PhotoBoothConfigLoader Instance { get; private set; }

    // ── Inspector 설정 ────────────────────────────────────────────────────────
    [Header("설정 파일 이름 (StreamingAssets 기준)")]
    public string configFileName = "config.json";

    [Header("핫리로드 단축키")]
    public KeyCode hotReloadKey = KeyCode.F5;

    // ── 공개 프로퍼티 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 현재 로드된 설정 객체. 앱 전반에서 읽기 전용으로 참조.
    /// 로드 실패 시에도 null 이 아닌 기본값 객체를 반환한다.
    /// </summary>
    public PhotoBoothConfig Config { get; private set; } = new PhotoBoothConfig();

    /// <summary>마지막 로드 성공 여부.</summary>
    public bool IsLoaded { get; private set; } = false;

    /// <summary>config.json 전체 경로.</summary>
    public string ConfigFilePath => Path.Combine(Application.streamingAssetsPath, configFileName);

    // ── 이벤트 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 설정이 (재)로드될 때마다 발생.
    /// ChromaKeyController 등 구독자가 즉시 설정을 반영한다.
    /// </summary>
    public event System.Action<PhotoBoothConfig> OnConfigReloaded;

    // ── JSON 직렬화 설정 (공유) ────────────────────────────────────────────────
    private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
    {
        NullValueHandling     = NullValueHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        Formatting            = Formatting.Indented
    };

    // ── 라이프사이클 ──────────────────────────────────────────────────────────

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
            return;
        }

        LoadConfig();
    }

    private void Update()
    {
        if (Input.GetKeyDown(hotReloadKey))
            ReloadConfig();
    }

    // ── 공개 메서드 ───────────────────────────────────────────────────────────

    /// <summary>
    /// config.json 을 디스크에서 다시 읽어 Config 를 갱신한다.
    /// OnConfigReloaded 이벤트를 통해 구독자에게 알린다.
    /// </summary>
    public void ReloadConfig()
    {
        Debug.Log("[ConfigLoader] 리로드 시작...");
        LoadConfig();
    }

    /// <summary>
    /// 현재 Config 객체를 파일 시스템의 config.json 에 저장한다.
    /// AdminPanelController 의 설정 변경 사항 반영에 사용된다.
    /// </summary>
    public void SaveConfig()
    {
        if (Config == null) return;
        
        try
        {
            string path = ConfigFilePath;
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string json = JsonConvert.SerializeObject(Config, _jsonSettings);
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
            Debug.Log("[ConfigLoader] config.json 저장 완료: " + path);
            
            // 저장 후 핫리로드 이벤트를 발생시켜 즉시 반영
            OnConfigReloaded?.Invoke(Config);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[ConfigLoader] 설정 저장 실패: " + ex.Message);
        }
    }

    // ── 내부 로직 ─────────────────────────────────────────────────────────────

    private void LoadConfig()
    {
        string path = ConfigFilePath;

        if (!File.Exists(path))
        {
            Debug.LogWarning("[ConfigLoader] config.json 없음. 샘플 파일 자동 생성 → " + path);
            CreateDefaultConfigFile(path);

            if (!File.Exists(path))
            {
                Debug.LogError("[ConfigLoader] 샘플 파일 생성 실패.");
                IsLoaded = false;
                return;
            }
        }

        try
        {
            string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var loaded  = JsonConvert.DeserializeObject<PhotoBoothConfig>(json, _jsonSettings);

            if (loaded == null)
            {
                Debug.LogWarning("[ConfigLoader] 파싱 결과 null. 기본값 Fallback.");
                Config   = new PhotoBoothConfig();
                IsLoaded = false;
                return;
            }

            Config   = loaded;
            IsLoaded = true;

            PrintConfigSummary();
            OnConfigReloaded?.Invoke(Config);
        }
        catch (JsonException ex)
        {
            Debug.LogError("[ConfigLoader] JSON 파싱 오류! " + ex.Message + " → config.json 문법을 확인하세요.");
            Config   = new PhotoBoothConfig();
            IsLoaded = false;
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[ConfigLoader] 예외: " + ex.Message);
            Config   = new PhotoBoothConfig();
            IsLoaded = false;
        }
    }

    /// <summary>
    /// 현장 배경 3종 예시가 담긴 샘플 config.json 을 StreamingAssets 에 생성한다.
    /// </summary>
    private void CreateDefaultConfigFile(string path)
    {
        var defaultConfig = new PhotoBoothConfig
        {
            Camera = new CameraConfig
            {
                UseDefaultDevice = true,
                DeviceName = "",
                RequestedWidth = 3840,
                RequestedHeight = 2160,
                RequestedFPS = 30
            },
            Global = new GlobalChromaConfig
            {
                TargetColor        = "#00B140",
                MasterSensitivity  = 35f,
                MasterSmoothness   = 8f,
                MasterSpillRemoval = 15f,
                MasterLumaWeight   = 0f,
                MasterEdgeChoke    = 0f,
                MasterPreBlur      = 0f,
                MasterCrop         = new CropConfig { Top = 0, Bottom = 0, Left = 0, Right = 0, FadeX = 0, FadeY = 0 }
            },
            Backgrounds = new System.Collections.Generic.List<BackgroundConfig>
            {
                new BackgroundConfig
                {
                    BgName    = "constellation",
                    Transform = new TransformConfig { Zoom = 100f, MoveX = 0f, MoveY = 0f, Rotation = 0f },
                    Color     = new ColorGradingConfig { Brightness = 0f, Contrast = 100f, Saturation = 100f, Hue = 0f }
                },
                new BackgroundConfig
                {
                    BgName    = "aurora",
                    Transform = new TransformConfig { Zoom = 105f, MoveX = 0f, MoveY = -2f, Rotation = 0f },
                    Color     = new ColorGradingConfig { Brightness = 5f, Contrast = 110f, Saturation = 115f, Hue = 5f }
                },
                new BackgroundConfig
                {
                    BgName    = "nebula",
                    Transform = new TransformConfig { Zoom = 102f, MoveX = 0f, MoveY = 0f, Rotation = 0f },
                    Color     = new ColorGradingConfig { Brightness = -5f, Contrast = 105f, Saturation = 95f, Hue = -3f }
                }
            }
        };

        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string json = JsonConvert.SerializeObject(defaultConfig, _jsonSettings);
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
            Debug.Log("[ConfigLoader] 샘플 config.json 생성 완료: " + path);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[ConfigLoader] 샘플 생성 실패: " + ex.Message);
        }
    }

    private void PrintConfigSummary()
    {
        var g       = Config.Global;
        int bgCount = Config.Backgrounds?.Count ?? 0;

        Debug.Log(
            "[ConfigLoader] config.json 로드 성공! 배경=" + bgCount + "개" +
            " | TargetColor=" + g.TargetColor +
            " | Sensitivity=" + g.MasterSensitivity.ToString("F2") +
            " | Smoothness=" + g.MasterSmoothness.ToString("F2") +
            " | SpillRemoval=" + g.MasterSpillRemoval.ToString("F2")
        );

        for (int i = 0; i < bgCount; i++)
        {
            var bg = Config.Backgrounds[i];
            Debug.Log("  [" + i + "] \"" + bg.BgName + "\"" +
                      (bg.Chroma.UseLocalChroma ? " [LocalChroma ON]" : ""));
        }

        if (bgCount == 0)
            Debug.LogWarning("[ConfigLoader] 배경 0개! config.json 의 'backgrounds' 배열을 확인하세요.");
    }
}
