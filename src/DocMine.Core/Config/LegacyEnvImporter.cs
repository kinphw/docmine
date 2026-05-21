// 기존 .env 1회 마이그레이션 — settings.json 이 없을 때만 호출.
// DB 항목만 import (그 외 항목은 더 이상 영속화하지 않음).

namespace DocMine.Core.Config;

public static class LegacyEnvImporter
{
    /// <summary>settings.json 이 없으면 .env 후보를 탐색해 DB 정보만 import.</summary>
    /// <returns>마이그레이션 출처 .env 경로, 또는 null (마이그레이션 없음).</returns>
    public static string? ImportIfNeeded()
    {
        if (UserSettings.Exists()) return null;

        foreach (var path in CandidateEnvPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var env = ParseEnvFile(path);
                var data = FromEnv(env);
                UserSettings.Save(data);
                Console.Error.WriteLine(
                    $"[DocMine] 기존 .env 의 DB 정보를 {UserSettings.SettingsPath()} 로 마이그레이션했습니다.");
                Console.Error.WriteLine($"[DocMine]   출처: {path}");
                Console.Error.WriteLine(
                    "[DocMine]   다음부터는 .env 대신 GUI 의 '⑤ 설정' 탭에서 관리하세요.");
                return path;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DocMine] .env 마이그레이션 실패 ({path}): {ex.Message}");
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateEnvPaths()
    {
        var docmineEnv = Environment.GetEnvironmentVariable("DOCMINE_ENV");
        if (!string.IsNullOrEmpty(docmineEnv)) yield return ExpandUser(docmineEnv);
        var hwpmineEnv = Environment.GetEnvironmentVariable("HWPMINE_ENV");
        if (!string.IsNullOrEmpty(hwpmineEnv)) yield return ExpandUser(hwpmineEnv);

        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            var exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath));
            if (!string.IsNullOrEmpty(exeDir))
                yield return Path.Combine(exeDir, ".env");
        }

        yield return Path.Combine(Directory.GetCurrentDirectory(), ".env");

        var appdata = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appdata))
        {
            yield return Path.Combine(appdata, "docmine", ".env");
            yield return Path.Combine(appdata, "hwpmine", ".env");
        }
    }

    /// <summary>최소 .env 파서 — KEY=VALUE / # 주석 / 양 따옴표 / export prefix.</summary>
    private static Dictionary<string, string> ParseEnvFile(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (val.Length > 0 && val[0] != '"' && val[0] != '\'')
            {
                var hash = val.IndexOf('#');
                if (hash >= 0) val = val[..hash].TrimEnd();
            }
            if (val.Length >= 2 &&
                ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
            {
                val = val[1..^1];
            }
            dict[key] = val;
        }
        return dict;
    }

    private static UserSettingsData FromEnv(Dictionary<string, string> env)
    {
        var data = new UserSettingsData();
        if (env.TryGetValue("DB_HOST",     out var v)) data.DbHost = v;
        if (env.TryGetValue("DB_PORT",     out v) && int.TryParse(v, out var port)) data.DbPort = port;
        if (env.TryGetValue("DB_USER",     out v)) data.DbUser = v;
        if (env.TryGetValue("DB_PASSWORD", out v)) data.DbPassword = v;
        if (env.TryGetValue("DB_NAME",     out v)) data.DbName = v;
        if (env.TryGetValue("DB_TABLE",    out v)) data.DbTable = v;
        // SCAN_DRIVES / CSV_FILE / PDF_CSV_FILE / COMMIT_EVERY / COM_RESTART /
        // PARSE_TIMEOUT / PDF_WORKERS 는 코드 상수 또는 자동 결정이라 무시.
        return data;
    }

    private static string ExpandUser(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path.Length > 1 ? path[2..] : "");
        }
        return Environment.ExpandEnvironmentVariables(path);
    }
}
