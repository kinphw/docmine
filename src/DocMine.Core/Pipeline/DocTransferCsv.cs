// DocTransferCsv — 환경 간 이전(반출/반입) 페이로드 CSV.
//
// manifest(폴더+파일명, 대조용 경량)와 달리 이건 body_text 까지 담은 "전송본"이다.
// 환경1에서 선택 반출 → 환경2에서 반입하면 파일 재파싱 없이 레코드를 그대로 적재한다.
//
// ★ body_text 는 줄바꿈·콤마·따옴표를 포함하므로, 줄 단위 파서(CsvIngestHelpers.ParseCsvLine)
//   로는 못 읽는다. 여기 Read 는 따옴표 안의 개행까지 다루는 RFC4180 스트리밍 파서를 쓴다.
//
// 컬럼: directory,filename,extension,size_bytes,modified,parse_status,error_msg,parsed_at,body_text
//   (body_text 는 가장 크므로 맨 끝. 매핑은 헤더명 기준이라 순서가 바뀌어도 무방.)

using System.Globalization;
using System.Text;
using DocMine.Core.Db;

namespace DocMine.Core.Pipeline;

public static class DocTransferCsv
{
    private const string Header =
        "directory,filename,extension,size_bytes,modified,parse_status,error_msg,parsed_at,body_text";

    public static void Write(IEnumerable<DocRecord> records, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new StreamWriter(path, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        w.Write(Header); w.Write("\r\n");

        foreach (var r in records)
        {
            w.Write(E(r.Directory));                 w.Write(',');
            w.Write(E(r.Filename));                  w.Write(',');
            w.Write(E(r.Extension ?? ""));           w.Write(',');
            w.Write(r.FileSize.ToString(CultureInfo.InvariantCulture)); w.Write(',');
            w.Write(E(r.FileMtime ?? ""));           w.Write(',');
            w.Write(E(r.ParseStatus ?? ""));         w.Write(',');
            w.Write(E(r.ErrorMsg ?? ""));            w.Write(',');
            w.Write(E(r.ParsedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "")); w.Write(',');
            w.Write(E(r.BodyText ?? ""));
            w.Write("\r\n");
        }
    }

    public static List<DocRecord> Read(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(true));
        return Parse(reader);
    }

    // 헤더 1줄 + 데이터 N줄을 DocRecord 로. (TextReader 분리로 단위 테스트 용이)
    public static List<DocRecord> Parse(TextReader reader)
    {
        var rows = ParseRecords(reader);
        var list = new List<DocRecord>();
        if (rows.Count == 0) return list;

        var header = rows[0];
        int Col(string name) => header.FindIndex(h => h.Trim().Trim('﻿').Equals(name, StringComparison.OrdinalIgnoreCase));
        int iDir = Col("directory"), iFn = Col("filename"), iExt = Col("extension"),
            iSz = Col("size_bytes"), iMt = Col("modified"), iSt = Col("parse_status"),
            iErr = Col("error_msg"), iPa = Col("parsed_at"), iBody = Col("body_text");

        for (var r = 1; r < rows.Count; r++)
        {
            var f = rows[r];
            string G(int i) => i >= 0 && i < f.Count ? f[i] : "";
            var dir = G(iDir); var fn = G(iFn);
            if (string.IsNullOrEmpty(dir) && string.IsNullOrEmpty(fn)) continue;   // 빈 줄 무시

            list.Add(new DocRecord(
                Directory:   dir,
                Filename:    fn,
                Extension:   G(iExt),
                FileSize:    long.TryParse(G(iSz), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sz) ? sz : 0,
                FileMtime:   NullIfEmpty(G(iMt)),
                BodyText:    NullIfEmpty(G(iBody)),
                ParseStatus: string.IsNullOrWhiteSpace(G(iSt)) ? "success" : G(iSt).Trim(),
                ErrorMsg:    NullIfEmpty(G(iErr)),
                ParsedAt:    DateTime.TryParse(G(iPa), CultureInfo.InvariantCulture, DateTimeStyles.None, out var pa) ? pa : null));
        }
        return list;
    }

    // RFC4180 스트리밍 파서 — 따옴표 안의 콤마/개행/이중따옴표를 모두 처리.
    private static List<List<string>> ParseRecords(TextReader reader)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false, recordHasData = false;
        int ci;

        while ((ci = reader.Read()) != -1)
        {
            var c = (char)ci;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }  // "" → "
                    else inQuotes = false;
                }
                else field.Append(c);   // 따옴표 안: 개행 포함 그대로
            }
            else
            {
                switch (c)
                {
                    case '"' when field.Length == 0: inQuotes = true; break;
                    case ',':  record.Add(field.ToString()); field.Clear(); recordHasData = true; break;
                    case '\r': break;   // CRLF — \n 에서 처리
                    case '\n':
                        record.Add(field.ToString()); field.Clear();
                        records.Add(record); record = new List<string>(); recordHasData = false;
                        break;
                    default: field.Append(c); recordHasData = true; break;
                }
            }
        }
        // 마지막 줄(개행 없이 끝난 경우) 마무리.
        if (field.Length > 0 || record.Count > 0 || recordHasData)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }

    private static string E(string s) => CsvIngestHelpers.CsvEscape(s);
    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
