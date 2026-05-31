// DocMine 사용자 설정 영속화 — %APPDATA%\DocMine\settings.json.
//
// 영속화 대상은 'PC 마다 달라야 하는' DB 접속 정보뿐.
// CSV 경로/적재 튜닝/스캔 드라이브는 모두 코드 상수 또는 런타임 자동 결정 →
// settings.json 에 두지 않음.
//
// 저장 위치:
//   Windows  : %APPDATA%\DocMine\settings.json
//   기타     : ~/.docmine/settings.json

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocMine.Core.Config;

/// <summary>JSON 직렬화 대상 DTO.</summary>
//
// System.Text.Json 은 기본적으로 public *property* 만 직렬화 — public field
// 는 무시되어 빈 {} 가 저장됨. 그래서 set/get 자동 구현 property 사용.
public sealed class UserSettingsData
{
    public string DbHost     { get; set; } = "127.0.0.1";
    public int    DbPort     { get; set; } = 3306;
    public string DbUser     { get; set; } = "root";
    public string DbPassword { get; set; } = "";
    public string DbName     { get; set; } = "hwp_documents";
    public string DbTable    { get; set; } = "documents";

    // 스캔 예외 절대 경로 — HWP/PDF 스캔 공통.
    // 등록된 경로의 하위 트리는 enumerate 자체를 안 함 (재귀 prune).
    // 예: C:\Users\<user>\OneDrive 추가 시 동기화 폴더 전부 skip.
    public List<string> ScanExcludeDirs { get; set; } = new();

    // PDF 워커 프로세스 수. 0 = 자동 (논리 CPU 수).
    public int PdfWorkers { get; set; } = 0;
}

public static class UserSettings
{
    public static string SettingsDir()
    {
        var appdata = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appdata))
            return Path.Combine(appdata, "DocMine");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docmine");
    }

    public static string SettingsPath() => Path.Combine(SettingsDir(), "settings.json");

    public static bool Exists() => File.Exists(SettingsPath());

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static UserSettingsData Load()
    {
        var path = SettingsPath();
        if (!File.Exists(path)) return new UserSettingsData();
        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<UserSettingsData>(json, JsonOpts);
            return data ?? new UserSettingsData();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new UserSettingsData();
        }
    }

    public static void Save(UserSettingsData data)
    {
        Directory.CreateDirectory(SettingsDir());
        var json = JsonSerializer.Serialize(data, JsonOpts);
        File.WriteAllText(SettingsPath(), json);
    }
}
