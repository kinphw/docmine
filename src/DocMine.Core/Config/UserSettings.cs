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

    // ── 보도자료 코퍼스(press) 연계 — 외부 stn_press_db 읽기전용 참조 ──────
    // 외부 프로젝트(stn-crawler)가 적재한 4대 기관 보도자료를 검색에 합치기 위한 설정.
    // 결합점은 DB 스키마(press_document)뿐이며 DocMine 은 SELECT 만 한다(절대 쓰지 않음).
    // press DB 가 없는 환경(환경1)에서는 런타임 프로브가 자동으로 비활성 → 아래 값은 무시됨.
    // 호스트/포트는 메인 DB 와 같은 인스턴스로 가정해 DbHost/DbPort 를 재사용한다.
    public bool   PressEnabled      { get; set; } = true;                       // 마스터 토글
    public string PressDbName       { get; set; } = "stn_press_db";
    public string PressDbUser       { get; set; } = "pdbuser";                  // 읽기전용 계정
    public string PressDbPassword   { get; set; } = "1226";
    // 원본 파일(첨부 pdf/hwp) 열람용 루트. 본문 검색은 DB 만으로 충분하고, 이 경로는
    // '원본 파일 열기' 용으로만 쓴다. 폐쇄망 등 파일이 없으면 검색에는 영향 없다.
    // 실제 경로 = <PressFilesBaseDir>\<source>\<folder>\<file_name>.
    public string PressFilesBaseDir { get; set; } = @"C:\projects\stn-crawler\data";
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
