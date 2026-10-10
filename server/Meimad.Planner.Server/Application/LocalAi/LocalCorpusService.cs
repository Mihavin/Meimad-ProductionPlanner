using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace Meimad.Planner.Server.Application.LocalAi;

internal sealed record CorpusRoot(string RootId, string Path);
internal sealed record CorpusSettings(int Version, IReadOnlyList<CorpusRoot> Roots);
internal sealed record CorpusUpdate(int ExpectedVersion, IReadOnlyList<string> Folders);
internal sealed record CorpusIndexResult(int ConfigurationVersion, long IndexedFiles, long SkippedFiles, DateTimeOffset CompletedAt);
internal sealed record CorpusIndexProgress(string JobId, int ConfigurationVersion, string Status,
    long FilesDiscovered, long FilesProcessed, long IndexedFiles, long SkippedFiles, long BytesRead,
    string? CurrentPath, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error,
    long OcrPages = 0, bool OcrAvailable = false);
/// <summary><c>Extraction</c> is TEXT, CSV, PDF_TEXT, PDF_OCR, PDF_MIXED, XLSX, XLS, STEP, DMIS or RTF; OCR text may contain recognition errors.</summary>
internal sealed record CorpusMatch(string DocumentId, string Title, string ContentHash, string Excerpt,
    int StartCharacter, int EndCharacter, DateTimeOffset IndexedAt, string MatchReason, string Extraction = "TEXT");

internal sealed class LocalCorpusService(SqliteDatabase database, TimeProvider clock, IHostApplicationLifetime lifetime,
    CorpusDocumentReader? documents = null)
{
    private const string Key = "local_ai_approved_corpus";
    private readonly CorpusDocumentReader reader = documents ?? new(new WindowsPageTextRecognizer());
    private readonly SemaphoreSlim indexGate = new(1, 1);
    private readonly object jobGate = new();
    private IndexJob? currentJob;

    private sealed record Document(string Id, string RootId, string Relative, string Hash, string Content, long Bytes, string Extraction = "TEXT");

    private sealed class IndexJob(string id, int configurationVersion, DateTimeOffset startedAt, CancellationTokenSource cancellation, bool ocrAvailable)
    {
        private readonly object gate = new();
        private long filesDiscovered, filesProcessed, indexedFiles, skippedFiles, bytesRead, ocrPages;
        private string status = "QUEUED";
        private string? currentPath, error;
        private DateTimeOffset? completedAt;
        internal string Id { get; } = id;
        internal int ConfigurationVersion { get; } = configurationVersion;
        internal DateTimeOffset StartedAt { get; } = startedAt;
        internal CancellationToken Token => cancellation.Token;
        internal CorpusIndexProgress Snapshot()
        {
            lock (gate) return new(Id, ConfigurationVersion, status, filesDiscovered, filesProcessed,
                indexedFiles, skippedFiles, bytesRead, currentPath, StartedAt, completedAt, error, ocrPages, ocrAvailable);
        }
        internal void Scanning() { lock (gate) status = "SCANNING"; }
        internal void Discovered(string path) { lock (gate) { filesDiscovered++; currentPath = path; } }
        internal void Indexed(string path, long bytes, int recognizedPages)
        { lock (gate) { filesProcessed++; indexedFiles++; bytesRead += bytes; ocrPages += recognizedPages; currentPath = path; } }
        internal void Skipped(string path)
        { lock (gate) { filesProcessed++; skippedFiles++; currentPath = path; } }
        internal void Completed(DateTimeOffset at)
        { lock (gate) { status = "COMPLETED"; currentPath = null; completedAt = at; } }
        internal void Cancelled(DateTimeOffset at, string reason)
        { lock (gate) { status = "CANCELLED"; currentPath = null; completedAt = at; error = reason; } }
        internal void Failed(DateTimeOffset at, Exception exception)
        { lock (gate) { status = "FAILED"; currentPath = null; completedAt = at; error = exception.Message; } }
        internal void Cancel(string reason)
        {
            lock (gate) error = reason;
            cancellation.Cancel();
        }
    }

    internal async Task<CorpusSettings> ReadAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadAsync(connection, null, token);
    }

    private static async Task<CorpusSettings> ReadAsync(SqliteConnection connection, SqliteTransaction? tx, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT value FROM application_settings WHERE key=$key"; command.Parameters.AddWithValue("$key", Key);
        var json = (string?)await command.ExecuteScalarAsync(token);
        return json is null ? new(0, []) : JsonSerializer.Deserialize<CorpusSettings>(json)!;
    }

    internal async Task<CorpusSettings> UpdateAsync(CorpusUpdate update, string actor, CancellationToken token)
    {
        if (update.Folders is null || update.Folders.Count > 16) throw new ArgumentException("Approve at most 16 folders.");
        var paths = update.Folders.Select(path =>
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path)
                || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException("Use ordinary absolute Server-local or factory share folder paths.");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!)) throw new ArgumentException("Approve a specific document folder, not a disk or share root.");
            return full;
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await using var connection = await database.OpenConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, tx, token);
        if (current.Version != update.ExpectedVersion) throw new InvalidOperationException("Corpus configuration changed. Refresh and review the current folders.");
        var saved = new CorpusSettings(current.Version + 1, paths.Select(path => new CorpusRoot(
            current.Roots.FirstOrDefault(root => string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase))?.RootId ?? Guid.NewGuid().ToString("N"), path)).ToArray());
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO application_settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value; DELETE FROM local_ai_documents; DELETE FROM local_ai_documents_staging;";
        command.Parameters.AddWithValue("$key", Key); command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(saved));
        await command.ExecuteNonQueryAsync(token);
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json,reason_code) VALUES($id,'ai_corpus_changed',$at,$actor,'{}','approved_folder_list_changed')";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("O")); command.Parameters.AddWithValue("$actor", actor);
        await command.ExecuteNonQueryAsync(token); await tx.CommitAsync(token);
        CancelCurrentJob("Approved folder configuration changed before indexing completed.");
        return saved;
    }

    internal async Task<CorpusIndexProgress> StartIndexingAsync(int expectedVersion, CancellationToken token)
    {
        var settings = await ReadAsync(token);
        if (settings.Version != expectedVersion) throw new InvalidOperationException("Corpus configuration changed. Refresh before indexing.");
        lock (jobGate)
        {
            if (currentJob is { } running && running.Snapshot().Status is "QUEUED" or "SCANNING")
                throw new InvalidOperationException("Document indexing is already running.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            var job = new IndexJob(Guid.NewGuid().ToString("N"), settings.Version, clock.GetUtcNow(), cancellation, reader.OcrAvailable);
            currentJob = job;
            _ = Task.Run(async () =>
            {
                try { await RunIndexAsync(settings, job.Token, job); }
                catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
                { job.Cancelled(clock.GetUtcNow(), job.Snapshot().Error ?? "Indexing was cancelled."); }
                catch (Exception exception) { job.Failed(clock.GetUtcNow(), exception); }
                finally { cancellation.Dispose(); }
            }, CancellationToken.None);
            return job.Snapshot();
        }
    }

    internal CorpusIndexProgress? ReadIndexProgress(string jobId)
    {
        lock (jobGate) return currentJob is { } job && string.Equals(job.Id, jobId, StringComparison.Ordinal) ? job.Snapshot() : null;
    }

    private void CancelCurrentJob(string reason)
    {
        lock (jobGate)
        {
            if (currentJob is { } job && job.Snapshot().Status is "QUEUED" or "SCANNING") job.Cancel(reason);
        }
    }

    // Kept for service-level callers and existing tests. HTTP callers use StartIndexingAsync so
    // a rebuild is independent of the request lifetime.
    internal async Task<CorpusIndexResult> IndexAsync(int expectedVersion, CancellationToken token)
    {
        var settings = await ReadAsync(token);
        if (settings.Version != expectedVersion) throw new InvalidOperationException("Corpus configuration changed. Refresh before indexing.");
        return await RunIndexAsync(settings, token, null);
    }

    private async Task<CorpusIndexResult> RunIndexAsync(CorpusSettings settings, CancellationToken token, IndexJob? job)
    {
        await indexGate.WaitAsync(token);
        try
        {
            job?.Scanning();
            var jobId = job?.Id ?? Guid.NewGuid().ToString("N");
            var indexed = 0L; var skipped = 0L;
            await using var connection = await database.OpenConnectionAsync(token);
            await using (var cleanup = connection.CreateCommand())
            {
                cleanup.CommandText = "DELETE FROM local_ai_documents_staging WHERE job_id=$job";
                cleanup.Parameters.AddWithValue("$job", jobId); await cleanup.ExecuteNonQueryAsync(token);
            }
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO local_ai_documents_staging(job_id,id,root_id,relative_path,content_hash,content,indexed_at,configuration_version,search_text,extraction) VALUES($job,$id,$root,$path,$hash,$content,$at,$version,$search,$extraction)";
            // Text extracted from a PDF, workbook, STEP or RTF file is cached by file hash as soon as it is read, so
            // neither a later rebuild nor a retry after a cancelled one extracts an unchanged file again.
            await using var cacheRead = connection.CreateCommand();
            cacheRead.CommandText = "SELECT content,extraction FROM local_ai_extraction_cache WHERE content_hash=$hash";
            await using var cacheWrite = connection.CreateCommand();
            cacheWrite.CommandText = "INSERT INTO local_ai_extraction_cache(content_hash,extraction,content,extracted_at) VALUES($hash,$extraction,$content,$at) ON CONFLICT(content_hash) DO NOTHING";
            async Task<(string Text, string Extraction)?> Cached(string hash)
            {
                cacheRead.Parameters.Clear(); cacheRead.Parameters.AddWithValue("$hash", hash);
                await using var row = await cacheRead.ExecuteReaderAsync(token);
                return await row.ReadAsync(token) ? (row.GetString(0), row.GetString(1)) : null;
            }
            foreach (var root in settings.Roots)
            {
                token.ThrowIfCancellationRequested();
                RecoveryPaths.RejectLinks(root.Path);
                foreach (var path in EnumerateFiles(root.Path, token))
                {
                    token.ThrowIfCancellationRequested();
                    job?.Discovered(path);
                    if (!CorpusDocumentReader.Supported(path)) { skipped++; job?.Skipped(path); continue; }
                    try
                    {
                        var relative = RelativePath(root.Path, path);
                        var content = await ReadFileAsync(root.Path, relative, Cached, token);
                        var document = new Document(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root.RootId + ":" + relative.ToUpperInvariant()))),
                            root.RootId, relative, content.Hash, content.Text, content.Bytes, content.Extraction);
                        var at = clock.GetUtcNow().ToString("O");
                        if (CorpusDocumentReader.IsCached(document.Extraction))
                        {
                            cacheWrite.Parameters.Clear();
                            cacheWrite.Parameters.AddWithValue("$hash", document.Hash); cacheWrite.Parameters.AddWithValue("$extraction", document.Extraction);
                            cacheWrite.Parameters.AddWithValue("$content", document.Content); cacheWrite.Parameters.AddWithValue("$at", at);
                            await cacheWrite.ExecuteNonQueryAsync(token);
                        }
                        insert.Parameters.Clear();
                        insert.Parameters.AddWithValue("$job", jobId); insert.Parameters.AddWithValue("$id", document.Id);
                        insert.Parameters.AddWithValue("$root", document.RootId); insert.Parameters.AddWithValue("$path", document.Relative);
                        insert.Parameters.AddWithValue("$hash", document.Hash); insert.Parameters.AddWithValue("$content", document.Content);
                        insert.Parameters.AddWithValue("$at", at); insert.Parameters.AddWithValue("$version", settings.Version);
                        insert.Parameters.AddWithValue("$search", (document.Relative + "\n" + document.Content).ToUpperInvariant());
                        insert.Parameters.AddWithValue("$extraction", document.Extraction);
                        await insert.ExecuteNonQueryAsync(token);
                        indexed++; job?.Indexed(path, document.Bytes, content.OcrPages);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
                    { skipped++; job?.Skipped(path); }
                }
            }
            await using var tx = connection.BeginTransaction(deferred: false);
            if ((await ReadAsync(connection, tx, token)).Version != settings.Version) throw new InvalidOperationException("Approved folders changed during indexing; rebuild from the current configuration.");
            await using var swap = connection.CreateCommand(); swap.Transaction = tx;
            // Cached extracted text that no current document uses (removed folders or changed files) is dropped with the swap.
            swap.CommandText = "DELETE FROM local_ai_documents; INSERT INTO local_ai_documents(id,root_id,relative_path,content_hash,content,indexed_at,configuration_version,search_text,extraction) SELECT id,root_id,relative_path,content_hash,content,indexed_at,configuration_version,search_text,extraction FROM local_ai_documents_staging WHERE job_id=$job; DELETE FROM local_ai_documents_staging WHERE job_id=$job; DELETE FROM local_ai_extraction_cache WHERE content_hash NOT IN (SELECT content_hash FROM local_ai_documents);";
            swap.Parameters.AddWithValue("$job", jobId); await swap.ExecuteNonQueryAsync(token); await tx.CommitAsync(token);
            var completed = clock.GetUtcNow(); job?.Completed(completed);
            return new(settings.Version, indexed, skipped, completed);
        }
        finally { indexGate.Release(); }
    }

    internal async Task<IReadOnlyList<CorpusMatch>> SearchAsync(string query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length is < 2 or > 128) throw new ArgumentException("Search for a literal phrase of 2–128 characters.");
        var settings = await ReadAsync(token);
        var candidates = new List<(Document Doc, DateTimeOffset At)>();
        await using (var connection = await database.OpenConnectionAsync(token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,root_id,relative_path,content_hash,content,indexed_at,extraction FROM local_ai_documents WHERE configuration_version=$version AND instr(search_text,$query)>0 ORDER BY relative_path,id LIMIT 100";
            command.Parameters.AddWithValue("$version", settings.Version); command.Parameters.AddWithValue("$query", query.ToUpperInvariant());
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) candidates.Add((new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), 0, reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
        }
        var matches = new List<CorpusMatch>();
        foreach (var (document, at) in candidates)
        {
            if (matches.Count == 20) break;
            if (!await IsUnchangedAsync(settings, document, token)) continue;
            var position = Math.Max(0, document.Content.IndexOf(query, StringComparison.OrdinalIgnoreCase) - 100);
            var length = Math.Min(500, document.Content.Length - position);
            matches.Add(new(document.Id, document.Relative, document.Hash, document.Content.Substring(position, length), position, position + length, at,
                "Literal phrase match in an approved document" + ExtractionNote(document.Extraction) + ". Historical reference only; applicability is unverified.", document.Extraction));
        }
        if ((await ReadAsync(token)).Version != settings.Version) throw new InvalidOperationException("Corpus visibility changed; repeat the search.");
        return matches;
    }

    /// <summary>
    /// Keyword search for the chat assistant: documents containing any of the words, ranked by how many
    /// distinct words they contain, optionally limited to documents whose path or text names a part number
    /// (tried as given, then without its dash suffix). Each document carries excerpts around the
    /// densest word matches (up to three). Identical copies of a file are returned once. Results are verified against the current file like the literal search.
    /// </summary>
    internal async Task<IReadOnlyList<CorpusMatch>> SearchKeywordsAsync(string query, string? partNumber, int limit, CancellationToken token)
    {
        var terms = KeywordTerms(query);
        if (terms.Count == 0 && string.IsNullOrWhiteSpace(partNumber)) throw new ArgumentException("Search for at least one word of two or more characters.");
        var scopes = new List<string?>();
        if (!string.IsNullOrWhiteSpace(partNumber))
        {
            var scope = partNumber.Trim().ToUpperInvariant();
            scopes.Add(scope);
            var dash = scope.LastIndexOf('-');
            if (dash > 3) scopes.Add(scope[..dash]);
        }
        else scopes.Add(null);
        var settings = await ReadAsync(token);
        foreach (var scope in scopes)
        {
            var candidates = new List<(Document Doc, DateTimeOffset At)>();
            await using (var connection = await database.OpenConnectionAsync(token))
            await using (var command = connection.CreateCommand())
            {
                var score = terms.Count == 0 ? "1" : string.Join("+", terms.Select((_, i) => $"(instr(search_text,$t{i})>0)"));
                command.CommandText = $"""
                    SELECT id,root_id,relative_path,content_hash,content,indexed_at,extraction,({score}) AS score FROM local_ai_documents
                    WHERE configuration_version=$version AND ($scope IS NULL OR instr(search_text,$scope)>0) AND ({score})>0
                    ORDER BY score DESC, relative_path, id LIMIT 60
                    """;
                command.Parameters.AddWithValue("$version", settings.Version);
                command.Parameters.AddWithValue("$scope", (object?)scope ?? DBNull.Value);
                for (var i = 0; i < terms.Count; i++) command.Parameters.AddWithValue("$t" + i, terms[i]);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) candidates.Add((new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), 0, reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
            }
            var matches = new List<CorpusMatch>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (document, at) in candidates)
            {
                if (seen.Count >= Math.Clamp(limit, 1, 20)) break;
                // The same file copied into several folders is shown once.
                if (seen.Contains(document.Hash) || !await IsUnchangedAsync(settings, document, token)) continue;
                seen.Add(document.Hash);
                foreach (var (start, end) in Excerpts(document.Content, terms.Count == 0 && scope is not null ? [scope] : terms))
                    matches.Add(new(document.Id, document.Relative, document.Hash, document.Content[start..end], start, end, at,
                        "Keyword match in an approved document" + ExtractionNote(document.Extraction) + ".", document.Extraction));
            }
            if ((await ReadAsync(token)).Version != settings.Version) throw new InvalidOperationException("Corpus visibility changed; repeat the search.");
            if (matches.Count > 0 || scope == scopes[^1]) return matches;
        }
        return [];
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "THE", "AND", "FOR", "ARE", "WHAT", "WHICH", "WITH", "FROM", "THIS", "THAT", "HOW", "WHO", "WHEN", "WHERE", "IN", "OF", "ON",
        "TO", "IS", "IT", "AN", "A", "AT", "BY", "OR", "BE", "DO", "DOES", "PART", "PARTS", "SHOW", "TELL", "ME", "FIND", "ANY", "ALL",
    };

    internal static IReadOnlyList<string> KeywordTerms(string? query)
    {
        var terms = new List<string>();
        var words = new string((query ?? "").ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '/' ? c : ' ').ToArray());
        foreach (var raw in words.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var term = raw.Trim('-', '.', '/');
            if (term.Length < 2 || term.Length > 64 || StopWords.Contains(term)) continue;
            // A crude plural stem so TOLERANCES also finds TOLERANCE.
            if (term.Length > 4 && term.EndsWith('S') && !term.EndsWith("SS", StringComparison.Ordinal) && term.All(char.IsLetter)) term = term[..^1];
            if (!terms.Contains(term)) terms.Add(term);
            if (terms.Count == 8) break;
        }
        return terms;
    }

    private static IEnumerable<(int Start, int End)> Excerpts(string content, IReadOnlyList<string> terms)
    {
        const int before = 250, after = 450;
        var hits = new List<(int Position, int Term)>();
        for (var t = 0; t < terms.Count; t++)
            for (var at = content.IndexOf(terms[t], StringComparison.OrdinalIgnoreCase); at >= 0 && hits.Count < 400;
                 at = content.IndexOf(terms[t], at + terms[t].Length, StringComparison.OrdinalIgnoreCase))
                hits.Add((at, t));
        if (hits.Count == 0) { yield return (0, Math.Min(content.Length, before + after)); yield break; }
        var windows = hits.Select(hit => (Start: Math.Max(0, hit.Position - before), End: Math.Min(content.Length, hit.Position + after)))
            .Select(window => (window.Start, window.End, Score: hits.Where(x => x.Position >= window.Start && x.Position < window.End).Select(x => x.Term).Distinct().Count()))
            .OrderByDescending(window => window.Score).ThenBy(window => window.Start).ToList();
        var chosen = new List<(int Start, int End)>();
        foreach (var window in windows)
        {
            if (chosen.Count == 3) break;
            if (chosen.All(other => window.Start >= other.End || window.End <= other.Start)) chosen.Add((window.Start, window.End));
        }
        foreach (var window in chosen.OrderBy(window => window.Start)) yield return window;
    }

    private static string ExtractionNote(string extraction) => extraction switch
    {
        "PDF_TEXT" => " (PDF text)",
        "PDF_OCR" => " (PDF recognized by OCR; characters may be misread)",
        "PDF_MIXED" => " (PDF; some pages recognized by OCR, characters may be misread)",
        "CSV" => " (CSV spreadsheet)",
        "XLSX" => " (Excel workbook; values as last saved)",
        "XLS" => " (Excel 97-2003 workbook; values as last saved)",
        "DMIS" => " (CMM inspection program; nominal values and tolerances as programmed, confirm against the current drawing)",
        "RTF" => " (RTF document)",
        "STEP" => " (STEP 3D model metadata; no dimensions)",
        _ => "",
    };

    private static async Task<bool> IsUnchangedAsync(CorpusSettings settings, Document document, CancellationToken token)
    {
        var root = settings.Roots.FirstOrDefault(root => root.RootId == document.RootId);
        if (root is null) return false;
        try { return await CurrentHashAsync(root.Path, document.Relative, token) == document.Hash; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException) { return false; }
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var folder = pending.Pop(); RecoveryPaths.RejectLinks(folder);
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)) yield return path;
            foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            { RecoveryPaths.RejectLinks(directory); pending.Push(directory); }
        }
    }

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    internal async Task<bool> IsCurrentAsync(string id, string hash, CancellationToken token)
    {
        var settings = await ReadAsync(token); string? rootId = null, relative = null;
        await using (var connection = await database.OpenConnectionAsync(token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT root_id,relative_path FROM local_ai_documents WHERE id=$id AND content_hash=$hash AND configuration_version=$version";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$version", settings.Version);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) { rootId = reader.GetString(0); relative = reader.GetString(1); }
        }
        var root = settings.Roots.FirstOrDefault(x => x.RootId == rootId); if (root is null || relative is null) return false;
        try { return await CurrentHashAsync(root.Path, relative, token) == hash && (await ReadAsync(token)).Version == settings.Version; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException) { return false; }
    }

    private async Task<CorpusFileContent> ReadFileAsync(string root, string relative,
        Func<string, Task<(string Text, string Extraction)?>>? cached, CancellationToken token)
    {
        var path = RecoveryPaths.Resolve(root, relative);
        var content = await reader.ReadAsync(path, cached, token);
        RecoveryPaths.RejectLinks(path);
        return content;
    }

    /// <summary>Stored text is shown only while the file still has the hash it was extracted from.</summary>
    private static async Task<string> CurrentHashAsync(string root, string relative, CancellationToken token)
    {
        var path = RecoveryPaths.Resolve(root, relative);
        var hash = await CorpusDocumentReader.HashAsync(path, token);
        RecoveryPaths.RejectLinks(path);
        return hash;
    }
}
