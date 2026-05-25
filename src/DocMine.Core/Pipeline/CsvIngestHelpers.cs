// CSV 로딩 + DB 기존 키 조회 — HWP/PDF Runner 공유 헬퍼.
// 원래 PdfInsertRunner 안에 있던 private 메서드를 분리.

using DocMine.Core.Config;
using DocMine.Core.Db;

namespace DocMine.Core.Pipeline;

public sealed record CsvRow(
    string Directory,
    string Filename,
    string Extension,
    long   SizeBytes,
    string Modified);

public static class CsvIngestHelpers
{
    /// <summary>utf-8-sig BOM 처리 + 컬럼명 매핑.</summary>
    public static List<CsvRow> LoadCsv(string csvPath)
    {
        var rows = new List<CsvRow>();
        using var reader = new StreamReader(csvPath, new System.Text.UTF8Encoding(true));
        var header = reader.ReadLine();
        if (header is null) return rows;

        var cols = header.Split(',')
            .Select((c, i) => (Name: c.Trim().Trim('﻿'), Idx: i))
            .ToDictionary(p => p.Name, p => p.Idx, StringComparer.OrdinalIgnoreCase);
        int Col(string name) => cols.TryGetValue(name, out var i) ? i : -1;
        var iDir = Col("directory"); var iFn = Col("filename");
        var iExt = Col("extension"); var iSz = Col("size_bytes"); var iMt = Col("modified");

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = ParseCsvLine(line);
            if (parts.Count <= Math.Max(iDir, iFn)) continue;
            rows.Add(new CsvRow(
                Directory: parts[iDir],
                Filename:  parts[iFn],
                Extension: iExt >= 0 && iExt < parts.Count ? parts[iExt] : "",
                SizeBytes: iSz >= 0 && iSz < parts.Count && long.TryParse(parts[iSz], out var sz) ? sz : 0,
                Modified:  iMt >= 0 && iMt < parts.Count ? parts[iMt] : ""));
        }
        return rows;
    }

    public static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuote)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuote = false;
                }
                else sb.Append(ch);
            }
            else
            {
                if (ch == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else if (ch == '"' && sb.Length == 0) inQuote = true;
                else sb.Append(ch);
            }
        }
        result.Add(sb.ToString());
        return result;
    }

    /// <summary>(directory, filename) chunked lookup — 이미 DB 에 있는 행은 skip.</summary>
    public static HashSet<(string, string)> LoadExistingKeys(
        AppConfig cfg, DocumentRepository repo, List<CsvRow> rows)
    {
        var keys = rows
            .Where(r => !string.IsNullOrEmpty(r.Directory) && !string.IsNullOrEmpty(r.Filename))
            .Select(r => (r.Directory, r.Filename))
            .ToHashSet();
        if (keys.Count == 0) return keys;

        var existing = new HashSet<(string, string)>();
        const int chunkSize = 500;
        var keyList = keys.ToList();
        using var conn = repo.OpenConnection();
        for (var i = 0; i < keyList.Count; i += chunkSize)
        {
            var chunk = keyList.Skip(i).Take(chunkSize).ToList();
            var placeholders = string.Join(", ", chunk.Select((_, j) => $"(@d{j}, @f{j})"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT directory, filename FROM `{cfg.DbTable}` " +
                $"WHERE (directory, filename) IN ({placeholders})";
            for (var j = 0; j < chunk.Count; j++)
            {
                cmd.Parameters.AddWithValue($"@d{j}", chunk[j].Directory);
                cmd.Parameters.AddWithValue($"@f{j}", chunk[j].Filename);
            }
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                existing.Add((rdr.GetString(0), rdr.GetString(1)));
        }
        return existing;
    }
}
