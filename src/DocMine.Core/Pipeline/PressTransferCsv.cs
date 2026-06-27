// PressTransferCsv — 보도자료(press) 반출 페이로드 CSV (본문 content 포함).
//
// 상대 환경(환경1)의 press_document 에 그대로 적재할 수 있도록 press 네이티브 컬럼을
// 모두 싣는다. 적재는 press 쪽(stn-crawler 등)이 담당한다(이 도구는 반출까지).
//
// ★ 대용량 회피: 선택분이 6.8만 행(본문 ~640MB)에 달할 수 있어, id 를 청크로 끊어
//   본문을 조회하며 곧바로 기록한다(전량을 메모리에 올리지 않는 스트리밍).
//
// 컬럼: source,source_seq,folder,published_date,post_title,file_name,file_ext,
//       file_url,content,char_count,content_hash   (content 가 가장 크므로 뒤쪽 배치)

using System.Globalization;
using System.Text;
using DocMine.Core.Db;

namespace DocMine.Core.Pipeline;

public static class PressTransferCsv
{
    public const string Header =
        "source,source_seq,folder,published_date,post_title,file_name,file_ext,file_url,content,char_count,content_hash";

    /// <summary>선택된 press id 들을 청크 스트리밍으로 CSV 기록. 반환 = 실제 기록 행수.</summary>
    public static int Write(
        PressCorpusService svc, IReadOnlyList<int> ids, string path,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new StreamWriter(path, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        w.Write(Header); w.Write("\r\n");

        int written = 0;
        const int chunk = 500;
        for (var i = 0; i < ids.Count; i += chunk)
        {
            ct.ThrowIfCancellationRequested();
            var slice = ids.Skip(i).Take(chunk).ToList();
            foreach (var r in svc.LoadExportRecords(slice))
            {
                w.Write(E(r.Source));     w.Write(',');
                w.Write(E(r.SourceSeq));  w.Write(',');
                w.Write(E(r.Folder));     w.Write(',');
                w.Write(E(r.PublishedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "")); w.Write(',');
                w.Write(E(r.PostTitle));  w.Write(',');
                w.Write(E(r.FileName));   w.Write(',');
                w.Write(E(r.FileExt));    w.Write(',');
                w.Write(E(r.FileUrl));    w.Write(',');
                w.Write(E(r.Content ?? "")); w.Write(',');
                w.Write(r.CharCount.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                w.Write(E(r.ContentHash));
                w.Write("\r\n");
                written++;
            }
            progress?.Invoke(Math.Min(i + chunk, ids.Count), ids.Count);
        }
        return written;
    }

    /// <summary>
    /// 반출 CSV → PressFullRecord 스트리밍 읽기(반입용). content 가 커서 전량을 메모리에
    /// 올리지 않도록 한 행씩 yield 한다 — 소비자가 배치로 upsert. 헤더명으로 컬럼 매핑.
    /// </summary>
    public static IEnumerable<PressFullRecord> ReadStreaming(TextReader reader)
    {
        using var e = EnumerateCsvRecords(reader).GetEnumerator();
        if (!e.MoveNext()) yield break;
        var header = e.Current;
        int Col(string n) => header.FindIndex(h => h.Trim().Trim('﻿').Equals(n, StringComparison.OrdinalIgnoreCase));
        int iS = Col("source"), iQ = Col("source_seq"), iFo = Col("folder"), iPd = Col("published_date"),
            iPt = Col("post_title"), iFn = Col("file_name"), iFe = Col("file_ext"), iFu = Col("file_url"),
            iC = Col("content"), iCc = Col("char_count"), iH = Col("content_hash");

        while (e.MoveNext())
        {
            var f = e.Current;
            string G(int i) => i >= 0 && i < f.Count ? f[i] : "";
            var src = G(iS); var seq = G(iQ); var fn = G(iFn);
            if (src.Length == 0 && seq.Length == 0 && fn.Length == 0) continue;   // 빈 줄 무시
            yield return new PressFullRecord(
                Source:        src,
                SourceSeq:     seq,
                Folder:        G(iFo),
                PublishedDate: DateTime.TryParse(G(iPd), CultureInfo.InvariantCulture, DateTimeStyles.None, out var pd) ? pd : null,
                PostTitle:     G(iPt),
                FileName:      fn,
                FileExt:       G(iFe),
                FileUrl:       G(iFu),
                Content:       NullIfEmpty(G(iC)),
                CharCount:     int.TryParse(G(iCc), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cc) ? cc : 0,
                ContentHash:   G(iH));
        }
    }

    // RFC4180 스트리밍 파서 — 따옴표 안의 콤마/개행/이중따옴표 처리. 한 레코드씩 yield.
    private static IEnumerable<List<string>> EnumerateCsvRecords(TextReader reader)
    {
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
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else
            {
                switch (c)
                {
                    case '"' when field.Length == 0: inQuotes = true; break;
                    case ',': record.Add(field.ToString()); field.Clear(); recordHasData = true; break;
                    case '\r': break;
                    case '\n':
                        record.Add(field.ToString()); field.Clear();
                        yield return record; record = new List<string>(); recordHasData = false;
                        break;
                    default: field.Append(c); recordHasData = true; break;
                }
            }
        }
        if (field.Length > 0 || record.Count > 0 || recordHasData)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }

    private static string E(string s) => CsvIngestHelpers.CsvEscape(s);
    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
