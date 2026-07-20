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
    string SettingsPath,
    // 보도자료 코퍼스(press) — 외부 stn_press_db 읽기전용 참조. 환경에 없으면 런타임 무시.
    bool   PressEnabled,
    string PressDbName,
    string PressDbUser,
    string PressDbPassword,
    string PressFilesBaseDir)
{
    // ─ 하드코딩 상수 ─ 사용자가 바꿀 필요 없는 값들.
    //
    // 확장자 .csd: 운영 환경에서 .csv 가 Excel 자동 연결되거나 보안 솔루션의
    // 처리 대상이 되는 케이스를 피하기 위한 박건영님 운영 정책.
    // 스캔/적재 공용 단일 CSV — hwp+pdf 를 한 번에 스캔해 한 파일에 기록.
    // 적재 Runner 는 LoadCsv 후 각자 자기 확장자만 필터링하므로 단일 CSV 호환.
    public const string DefaultScanCsv = "doc_file_list.csd";

    // 적재 튜닝 — Python 판 .env 기본값과 동일.
    public const int CommitEvery         = 50;    // DB 커밋 간격 (건)
    public const int ComRestart          = 500;   // HWP COM 워커 재시작 주기 (건)
    public const int ParseTimeoutSeconds = 60;    // HWP 파싱 1건 타임아웃

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
            SettingsPath: UserSettings.SettingsPath(),
            PressEnabled:      data.PressEnabled,
            PressDbName:       data.PressDbName,
            PressDbUser:       data.PressDbUser,
            PressDbPassword:   data.PressDbPassword,
            PressFilesBaseDir: data.PressFilesBaseDir);
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
            // affected-rows semantics(1=삽입·2=갱신·0=무변경). MySqlConnector 기본은
            // CLIENT_FOUND_ROWS 라 무변경 upsert 도 1 을 돌려줘 '삽입'으로 오집계된다.
            // 반입 신규/갱신 카운트가 이 값에 의존하므로 명시적으로 끈다([[UpsertRecords]]).
            ["UseAffectedRows"] = "true",
        };
        if (useDb) dict["Database"] = DbName;
        return dict;
    }

    public string GetConnectionString(bool useDb = true)
    {
        var parts = GetDbConnectionStringDict(useDb);
        return string.Join(";", parts.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>
    /// 보도자료 코퍼스(press) 커넥션 문자열. 호스트/포트는 메인 DB 와 같은 인스턴스로
    /// 가정해 재사용하고, DB 이름·계정만 press 전용 값을 쓴다. 짧은 Connection Timeout 으로
    /// press 없는 환경에서 프로브가 빨리 실패하게 한다.
    ///
    /// 환경2(검색·반출)는 읽기전용 계정(pdbuser)이면 충분하지만, 환경1(반입)은 쓰기 권한
    /// 계정으로 설정해야 press_document 를 생성·적재할 수 있다(같은 설정값을 환경별로 다르게).
    /// useDb=false 는 DB 자체 생성(CREATE DATABASE) 단계에서 쓴다.
    /// </summary>
    public string GetPressConnectionString(bool useDb = true)
    {
        var dict = new Dictionary<string, string>
        {
            ["Server"]             = DbHost,
            ["Port"]               = DbPort.ToString(),
            ["User Id"]            = PressDbUser,
            ["Password"]           = PressDbPassword,
            ["CharSet"]            = "utf8mb4",
            ["Connection Timeout"] = "5",
            ["UseAffectedRows"]    = "true",   // 반입 신규/갱신 카운트 정확도 — 위 주석 참조.
        };
        if (useDb) dict["Database"] = PressDbName;
        return string.Join(";", dict.Select(p => $"{p.Key}={p.Value}"));
    }
}
