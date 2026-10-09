using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Application.LocalAi;

internal sealed record CorpusRoot(string RootId, string Path);
internal sealed record CorpusSettings(int Version, IReadOnlyList<CorpusRoot> Roots);
internal sealed record CorpusUpdate(int ExpectedVersion, IReadOnlyList<string> Folders);
internal sealed record CorpusIndexResult(int ConfigurationVersion, int IndexedFiles, int SkippedFiles, DateTimeOffset CompletedAt);
internal sealed record CorpusMatch(string DocumentId, string Title, string ContentHash, string Excerpt,
    int StartCharacter, int EndCharacter, DateTimeOffset IndexedAt, string MatchReason);

internal sealed class LocalCorpusService(SqliteDatabase database, TimeProvider clock)
{
    private const string Key = "local_ai_approved_corpus";
    private readonly SemaphoreSlim indexGate = new(1, 1);
    private sealed record Document(string Id, string RootId, string Relative, string Hash, string Content);
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
        command.CommandText = "INSERT INTO application_settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value; DELETE FROM local_ai_documents;";
        command.Parameters.AddWithValue("$key", Key); command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(saved));
        await command.ExecuteNonQueryAsync(token);
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json,reason_code) VALUES($id,'ai_corpus_changed',$at,$actor,'{}','approved_folder_list_changed')";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("O")); command.Parameters.AddWithValue("$actor", actor);
        await command.ExecuteNonQueryAsync(token); await tx.CommitAsync(token);
        return saved;
    }
    internal async Task<CorpusIndexResult> IndexAsync(int expectedVersion, CancellationToken token)
    {
        if (!await indexGate.WaitAsync(0, token)) throw new InvalidOperationException("Document indexing is already running.");
        try
        {
            var settings = await ReadAsync(token);
            if (settings.Version != expectedVersion) throw new InvalidOperationException("Corpus configuration changed. Refresh before indexing.");
            var documents = new List<Document>(); var skipped = 0; long totalBytes = 0; var visited = 0;
            foreach (var root in settings.Roots)
            {
                token.ThrowIfCancellationRequested();
                RecoveryPaths.RejectLinks(root.Path);
                // Explicitly nonrecursive: subfolders require their own approval.
                foreach (var path in Directory.EnumerateFiles(root.Path, "*", SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > 10000) throw new ArgumentException("Index exceeds 10,000 directory entries; narrow approved folders.");
                    if (!Supported(path)) { skipped++; continue; }
                    try
                    {
                        var content = await ReadFileAsync(root.Path, Path.GetFileName(path), token);
                        totalBytes += Encoding.UTF8.GetByteCount(content.Text);
                        if (documents.Count >= 5000 || totalBytes > 32 * 1024 * 1024) throw new ArgumentException("Index exceeds 5,000 documents or 32 MiB; narrow approved folders.");
                        documents.Add(new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root.RootId + ":" + Path.GetFileName(path).ToUpperInvariant()))),
                            root.RootId, Path.GetFileName(path), content.Hash, content.Text));
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
                    { skipped++; }
                }
            }
            await using var connection = await database.OpenConnectionAsync(token);
            await using var tx = connection.BeginTransaction(deferred: false);
            if ((await ReadAsync(connection, tx, token)).Version != settings.Version) throw new InvalidOperationException("Approved folders changed during indexing; rebuild from the current configuration.");
            await using var command = connection.CreateCommand(); command.Transaction = tx;
            command.CommandText = "DELETE FROM local_ai_documents"; await command.ExecuteNonQueryAsync(token);
            var now = clock.GetUtcNow();
            foreach (var document in documents)
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO local_ai_documents VALUES($id,$root,$path,$hash,$content,$at,$version,$search)";
                command.Parameters.AddWithValue("$id", document.Id); command.Parameters.AddWithValue("$root", document.RootId);
                command.Parameters.AddWithValue("$path", document.Relative); command.Parameters.AddWithValue("$hash", document.Hash);
                command.Parameters.AddWithValue("$content", document.Content); command.Parameters.AddWithValue("$at", now.ToString("O")); command.Parameters.AddWithValue("$version", settings.Version);
                command.Parameters.AddWithValue("$search", (document.Relative + "\n" + document.Content).ToUpperInvariant());
                await command.ExecuteNonQueryAsync(token);
            }
            await tx.CommitAsync(token);
            return new(settings.Version, documents.Count, skipped, now);
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
            command.CommandText = "SELECT id,root_id,relative_path,content_hash,content,indexed_at FROM local_ai_documents WHERE configuration_version=$version AND instr(search_text,$query)>0 ORDER BY relative_path,id LIMIT 100";
            command.Parameters.AddWithValue("$version", settings.Version); command.Parameters.AddWithValue("$query", query.ToUpperInvariant());
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) candidates.Add((new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
        }
        var matches = new List<CorpusMatch>();
        foreach (var (document, at) in candidates)
        {
            if (matches.Count == 20) break;
            var root = settings.Roots.FirstOrDefault(root => root.RootId == document.RootId);
            if (root is null) continue;
            try
            {
                var current = await ReadFileAsync(root.Path, document.Relative, token);
                if (current.Hash != document.Hash) continue;
                var position = Math.Max(0, current.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) - 100);
                var length = Math.Min(500, current.Text.Length - position);
                matches.Add(new(document.Id, document.Relative, document.Hash, current.Text.Substring(position, length), position, position + length, at,
                    "Literal phrase match in an approved text document. Historical reference only; applicability is unverified."));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException) { }
        }
        if ((await ReadAsync(token)).Version != settings.Version) throw new InvalidOperationException("Corpus visibility changed; repeat the search.");
        return matches;
    }
    private static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md";
    internal async Task<bool> IsCurrentAsync(string id, string hash, CancellationToken token)
    {
        var settings = await ReadAsync(token);
        string? rootId = null, relative = null;
        await using (var connection = await database.OpenConnectionAsync(token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT root_id,relative_path FROM local_ai_documents WHERE id=$id AND content_hash=$hash AND configuration_version=$version";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$version", settings.Version);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token)) { rootId = reader.GetString(0); relative = reader.GetString(1); }
        }
        var root = settings.Roots.FirstOrDefault(x => x.RootId == rootId);
        if (root is null || relative is null) return false;
        try
        {
            return (await ReadFileAsync(root.Path, relative, token)).Hash == hash && (await ReadAsync(token)).Version == settings.Version;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException) { return false; }
    }
    private static async Task<(string Hash, string Text)> ReadFileAsync(string root, string relative, CancellationToken token)
    {
        var path = RecoveryPaths.Resolve(root, relative);
        if (!Supported(path)) throw new InvalidDataException("Unsupported document format.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (file.Length > 262144) throw new InvalidDataException("Document exceeds 256 KiB.");
        var bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token);
        RecoveryPaths.RejectLinks(path);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Contains('\0')) throw new InvalidDataException("Binary content is unsupported.");
        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), text);
    }
}
