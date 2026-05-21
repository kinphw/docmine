// 드라이브 재귀 스캐너 — Python docmine/scanner.py 의 포팅 + UX 개선.
//
// Python 판 대비 .NET 고유 개선:
//   - EnumerationOptions.IgnoreInaccessible = true
//     권한 거부 디렉토리/파일을 silent skip (Python os.walk 와 동일 동작).
//     기존 try/catch 누적이 시스템 폴더 많은 C:\ 콜드 스캔 초반에 큰 비용.
//   - 2초 throttled "현재 폴더" hint
//     첫 milestone(1,000건) 도달 전까지 UI 침묵 문제 해결.
//     별도 Task 에서 ctx.CurrentDir 을 주기적으로 publish.
//   - AttributesToSkip += ReparsePoint
//     Windows junction(예: C:\Documents and Settings → C:\Users) 무한 루프 방지.
//     사용자가 명시한 root 자체는 그대로 진입.
//
// 출력 CSV 포맷은 Python 판과 완전 동일 — utf-8-sig BOM + 5컬럼.

using System.Globalization;
using System.Text;

namespace DocMine.Core.Scanning;

public sealed record ScannedFile(
    string Directory,
    string Filename,
    string Extension,
    long   SizeBytes,
    string Modified);

public sealed record ScanResult(
    IReadOnlyList<ScannedFile> Files,
    int InaccessibleCount);

public static class DriveScanner
{
    public static readonly IReadOnlySet<string> DefaultHwpExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".hwp", ".hwpx" };

    public static readonly IReadOnlySet<string> DefaultPdfExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" };

    // 사용자 문서가 있을 가능성 0 인데 트리가 깊어 시간만 잡아먹는 폴더.
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "Windows", "ProgramData",
    };

    // OS 단에서 권한 거부 / Hidden / System / ReparsePoint 는 silent skip.
    // 권한 거부 throw/catch 누적 비용 제거가 핵심.
    private static readonly EnumerationOptions EnumOpts = new()
    {
        IgnoreInaccessible    = true,
        RecurseSubdirectories = false,  // 재귀는 우리가 직접 — SKIP_DIRS prune 위해
        AttributesToSkip      = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <summary>여러 루트(드라이브) 재귀 스캔.</summary>
    /// <param name="onProgress">1,000건 단위 milestone 콜백.</param>
    /// <param name="onRootStart">루트 진입 시점 콜백.</param>
    /// <param name="onCurrentDir">살아있음 신호. <paramref name="currentDirInterval"/> 주기로 현재 처리 중인 폴더 통보.</param>
    /// <param name="currentDirInterval">현재 폴더 hint 주기. 기본 2초.</param>
    /// <param name="excludeDirs">절대 경로 prefix 가 일치하면 하위 트리 전체 skip. OneDrive 등 동기화 폴더 회피용.</param>
    public static ScanResult Scan(
        IEnumerable<string> roots,
        IReadOnlySet<string> extensions,
        Action<int>? onProgress = null,
        Action<string>? onRootStart = null,
        Action<string>? onCurrentDir = null,
        TimeSpan? currentDirInterval = null,
        IReadOnlyList<string>? excludeDirs = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = new ScanContext(extensions, NormalizeExcludeDirs(excludeDirs), cancellationToken);
        var interval = currentDirInterval ?? TimeSpan.FromSeconds(2);

        // 백그라운드 hint ticker — 콜백 null 이면 skip.
        using var hintCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hintTask = onCurrentDir != null
            ? Task.Run(() => HintLoopAsync(ctx, interval, onCurrentDir, hintCts.Token))
            : Task.CompletedTask;

        try
        {
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onRootStart?.Invoke(root);
                ScanRecursive(root, ctx, onProgress);
            }
        }
        finally
        {
            hintCts.Cancel();
            try { hintTask.Wait(500); } catch { /* shutdown 잡소리 무시 */ }
        }

        return new ScanResult(ctx.Files, ctx.Inaccessible);
    }

    private static async Task HintLoopAsync(
        ScanContext ctx,
        TimeSpan interval,
        Action<string> onCurrentDir,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                var dir = ctx.CurrentDir;
                if (!string.IsNullOrEmpty(dir)) onCurrentDir(dir);
            }
        }
        catch (OperationCanceledException) { /* 정상 종료 */ }
    }

    private static void ScanRecursive(
        string dirPath,
        ScanContext ctx,
        Action<int>? onProgress)
    {
        ctx.Token.ThrowIfCancellationRequested();
        ctx.CurrentDir = dirPath;  // hint ticker 가 읽음

        // Files + Dirs 를 한 번의 readdir 로 받고, FileInfo 의 메타데이터를 그대로 사용 —
        // 매 매칭 파일에 new FileInfo(path) 로 추가 stat 호출하던 회귀 제거.
        // .NET 의 NtQueryDirectoryFile 한 번에 Length/LastWriteTime 모두 fetch.
        DirectoryInfo di;
        IEnumerable<FileSystemInfo> entries;
        try
        {
            di = new DirectoryInfo(dirPath);
            entries = di.EnumerateFileSystemInfos("*", EnumOpts);
        }
        catch (UnauthorizedAccessException) { ctx.Inaccessible++; return; }
        catch (DirectoryNotFoundException)  { return; }
        catch (IOException)                 { ctx.Inaccessible++; return; }

        // SKIP_DIRS / '$' prefix / 사용자 예외 폴더 — enumerate 시점에 prune.
        var subdirs = new List<string>();
        foreach (var fsi in entries)
        {
            if (fsi is FileInfo fi)
            {
                if (!ctx.Extensions.Contains(fi.Extension)) continue;
                try
                {
                    ctx.Files.Add(new ScannedFile(
                        Directory: dirPath,
                        Filename:  fi.Name,
                        Extension: fi.Extension.ToLowerInvariant(),
                        SizeBytes: fi.Length,           // 캐싱됨 — 추가 stat 없음
                        Modified:  fi.LastWriteTime.ToString(
                                       "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                }
                catch (UnauthorizedAccessException) { ctx.Inaccessible++; }
                catch (IOException)                 { ctx.Inaccessible++; }
            }
            else if (fsi is DirectoryInfo)
            {
                var name = fsi.Name;
                if (SkipDirs.Contains(name))                       continue;
                if (name.StartsWith('$'))                          continue;
                if (IsExcluded(fsi.FullName, ctx.ExcludePrefixes)) continue;
                subdirs.Add(fsi.FullName);
            }
        }

        var milestone = ctx.Files.Count / 1000;
        if (milestone > ctx.LastMilestone)
        {
            ctx.LastMilestone = milestone;
            onProgress?.Invoke(ctx.Files.Count);
        }

        foreach (var sub in subdirs)
            ScanRecursive(sub, ctx, onProgress);
    }

    /// <summary>
    /// 사용자 지정 예외 폴더 정규화 — 절대 경로 + 끝에 \ 보장. prefix 매칭 효율.
    /// 빈 항목/유효하지 않은 경로는 silent skip.
    /// </summary>
    private static List<string> NormalizeExcludeDirs(IReadOnlyList<string>? raw)
    {
        var result = new List<string>();
        if (raw is null) return result;
        foreach (var p in raw)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                var norm = Path.GetFullPath(p).TrimEnd('\\') + "\\";
                result.Add(norm.ToUpperInvariant());
            }
            catch { /* 잘못된 경로는 무시 */ }
        }
        return result;
    }

    private static bool IsExcluded(string subDir, IReadOnlyList<string> excludePrefixes)
    {
        if (excludePrefixes.Count == 0) return false;
        string normalized;
        try { normalized = (Path.GetFullPath(subDir).TrimEnd('\\') + "\\").ToUpperInvariant(); }
        catch { return false; }
        foreach (var prefix in excludePrefixes)
        {
            // 정확히 일치하거나 prefix 로 시작하면 excluded.
            if (normalized.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Python 판과 100% 동일한 utf-8-sig BOM + 5컬럼 CSV.</summary>
    public static void WriteCsv(IEnumerable<ScannedFile> rows, string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var w = new StreamWriter(outPath, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        w.WriteLine("directory,filename,extension,size_bytes,modified");
        foreach (var r in rows)
        {
            w.Write(CsvEscape(r.Directory));   w.Write(',');
            w.Write(CsvEscape(r.Filename));    w.Write(',');
            w.Write(CsvEscape(r.Extension));   w.Write(',');
            w.Write(r.SizeBytes.ToString(CultureInfo.InvariantCulture)); w.Write(',');
            w.Write(CsvEscape(r.Modified));
            w.Write("\r\n");
        }
    }

    private static string CsvEscape(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    // ─ 내부 상태 ─────────────────────────────────────────────────────
    private sealed class ScanContext
    {
        public readonly List<ScannedFile> Files = new();
        public readonly IReadOnlySet<string> Extensions;
        public readonly IReadOnlyList<string> ExcludePrefixes;
        public readonly CancellationToken Token;
        // hint ticker 가 별도 스레드에서 읽으므로 volatile.
        // string 참조 대입은 .NET 메모리 모델상 atomic.
        public volatile string CurrentDir = "";
        public int LastMilestone;
        public int Inaccessible;

        public ScanContext(IReadOnlySet<string> ext, IReadOnlyList<string> excludePrefixes, CancellationToken token)
        {
            Extensions = ext;
            ExcludePrefixes = excludePrefixes;
            Token = token;
        }
    }
}
