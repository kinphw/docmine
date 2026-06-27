// PressExportLedger — 보도자료(press) 반출 이력(누적 장부).
//
// 내부문서 반출은 '상대 환경이 보유한 것'(매니페스트)을 받아 대조하지만, press 는
// 환경2→환경1 단방향이라 상대 매니페스트 왕복이 없다. 대신 '이쪽에서 이미 내보낸 것'을
// 스스로 누적 기록해, 다음 반출 때 신규/변경분만 가린다.
//
//   키   = (source, source_seq, file_name)   [press_document UNIQUE]
//   값   = 마지막으로 내보낸 시점의 content_hash (+ exported_at)
//   판정 = 키가 없으면 '신규', 있어도 hash 가 다르면 '변경' → 둘 다 반출 대상.
//
// 주의: 이 장부는 '보냈다는 의도'의 기록이지 '상대가 받았다는 확인'이 아니다. 전송 파일이
// 유실되면 해당 항목은 재전송되지 않으니, 그럴 땐 장부를 초기화(삭제)해 전체 재반출한다.
// 상대 적재는 UNIQUE 키 기준 upsert 라 중복 전송돼도 멱등하다.

using DocMine.Core.Config;

namespace DocMine.Core.Pipeline;

public sealed class PressExportLedger
{
    private const string Header = "source,source_seq,file_name,content_hash,exported_at";

    private readonly Dictionary<(string, string, string), (string Hash, string At)> _byKey = new();

    public int Count => _byKey.Count;

    /// <summary>기본 장부 경로 — 설정 폴더(%APPDATA%\DocMine).</summary>
    public static string DefaultPath()
        => Path.Combine(UserSettings.SettingsDir(), "press_export_ledger.csv");

    public static PressExportLedger Load(string path)
    {
        var led = new PressExportLedger();
        if (!File.Exists(path)) return led;

        using var reader = new StreamReader(path, new System.Text.UTF8Encoding(true));
        var header = reader.ReadLine();
        if (header is null) return led;

        var cols = header.Split(',')
            .Select((c, i) => (Name: c.Trim().Trim('﻿'), Idx: i))
            .ToDictionary(p => p.Name, p => p.Idx, StringComparer.OrdinalIgnoreCase);
        int Col(string n) => cols.TryGetValue(n, out var i) ? i : -1;
        int iS = Col("source"), iQ = Col("source_seq"), iF = Col("file_name"),
            iH = Col("content_hash"), iA = Col("exported_at");

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var p = CsvIngestHelpers.ParseCsvLine(line);
            string G(int i) => i >= 0 && i < p.Count ? p[i] : "";
            var s = G(iS); var q = G(iQ); var f = G(iF);
            if (s.Length == 0 && q.Length == 0 && f.Length == 0) continue;
            led._byKey[(s, q, f)] = (G(iH), G(iA));
        }
        return led;
    }

    /// <summary>이미 반출됐고 내용도 그대로면 true (= 반출 대상 아님).</summary>
    public bool IsExported(string source, string sourceSeq, string fileName, string contentHash)
        => _byKey.TryGetValue((source, sourceSeq, fileName), out var v) && v.Hash == contentHash;

    /// <summary>반출 완료한 행을 장부에 upsert.</summary>
    public void MarkExported(string source, string sourceSeq, string fileName, string contentHash, string exportedAt)
        => _byKey[(source, sourceSeq, fileName)] = (contentHash, exportedAt);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(true));
        w.Write(Header); w.Write("\r\n");
        foreach (var (key, val) in _byKey)
        {
            w.Write(CsvIngestHelpers.CsvEscape(key.Item1)); w.Write(',');
            w.Write(CsvIngestHelpers.CsvEscape(key.Item2)); w.Write(',');
            w.Write(CsvIngestHelpers.CsvEscape(key.Item3)); w.Write(',');
            w.Write(CsvIngestHelpers.CsvEscape(val.Hash));  w.Write(',');
            w.Write(CsvIngestHelpers.CsvEscape(val.At));    w.Write("\r\n");
        }
    }
}
