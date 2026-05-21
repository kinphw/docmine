// DocMine 전역 설정.
//
// 영속화 대상 = DB 접속 정보뿐 (PC 마다 다름).
// 그 외 모든 값은 코드 상수 또는 런타임 자동 결정 — 사용자가 바꿀 일 없음.
//
// 첫 실행 시 settings.json 이 없으면 LegacyEnvImporter 가 기존 .env 의 DB 항목을
// 1회 마이그레이션. 그 후 .env 는 무시.

namespace DocMine.Core.Config;

public sealed record AppConfig(
    string DbHost,
    int    DbPort,
    string DbUser,
    string DbPassword,
    string DbName,
    string DbTable,
    IReadOnlyList<string> ScanExcludeDirs,
    string SettingsPath)
{
    // ─ 하드코딩 상수 ─ 사용자가 바꿀 필요 없는 값들.
    //
    // 확장자 .csd: 운영 환경에서 .csv 가 Excel 자동 연결되거나 보안 솔루션의
    // 처리 대상이 되는 케이스를 피하기 위한 박건영님 운영 정책.
    public const string DefaultHwpCsv = "hwp_file_list.csd";
    public const string DefaultPdfCsv = "pdf_file_list.csd";

    // 적재 튜닝 — Python 판 .env 기본값과 동일.
    public const int CommitEvery         = 50;    // DB 커밋 간격 (건)
    public const int ComRestart          = 500;   // HWP COM 워커 재시작 주기 (건)
    public const int ParseTimeoutSeconds = 60;    // HWP 파싱 1건 타임아웃

    // PDF 워커 수 = 논리 CPU. PdfPig 가 CPU-바운드라 코어 수 그대로.
    public static int PdfWorkers => Math.Max(1, Environment.ProcessorCount);

    // ─ 전역 캐시 ─────────────────────────────────────────────────────
    private static AppConfig _current = BuildFromSettings();
    public static AppConfig Current => _current;

    /// <summary>설정 탭에서 DB 정보 저장 후 호출 — 다음 사용처가 새 값을 봄.</summary>
    public static void Reload() => _current = BuildFromSettings();

    private static AppConfig BuildFromSettings()
    {
        // settings.json 이 없으면 .env 1회 마이그레이션 시도.
        LegacyEnvImporter.ImportIfNeeded();

        var data = UserSettings.Load();
        return new AppConfig(
            DbHost:   data.DbHost,
            DbPort:   data.DbPort,
            DbUser:   data.DbUser,
            DbPassword: data.DbPassword,
            DbName:   data.DbName,
            DbTable:  data.DbTable,
            ScanExcludeDirs: data.ScanExcludeDirs.ToList(),
            SettingsPath: UserSettings.SettingsPath());
    }

    public Dictionary<string, string> GetDbConnectionStringDict(bool useDb = true)
    {
        var dict = new Dictionary<string, string>
        {
            ["Server"]   = DbHost,
            ["Port"]     = DbPort.ToString(),
            ["User Id"]  = DbUser,
            ["Password"] = DbPassword,
            ["CharSet"]  = "utf8mb4",
        };
        if (useDb) dict["Database"] = DbName;
        return dict;
    }

    public string GetConnectionString(bool useDb = true)
    {
        var parts = GetDbConnectionStringDict(useDb);
        return string.Join(";", parts.Select(p => $"{p.Key}={p.Value}"));
    }
}
