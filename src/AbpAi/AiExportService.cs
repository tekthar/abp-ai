using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;

namespace AbpAi;

/// <summary>An export of calls as its owner reads it: its period and scope, how many calls (and of them, with their prompts), its size, and until when it's kept.</summary>
public sealed record AiExportInfo(Guid Id, DateTime At, Guid ActorId, DateTime From, DateTime To, Guid? ScopeId, int Calls, int Prompts, bool Truncated, long Bytes,
    DateTime ExpiresAt);

/// <summary>An export's file: its name, its type and its bytes (JSON lines, gzipped).</summary>
public sealed record AiExportFile(string Name, string ContentType, byte[] Content);

/// <summary>
/// Exports of a period's calls for support (AI8): the site's owner only (<see cref="IAiRights.IsSiteOwnerAsync"/>), each
/// recorded in the audit log (<see cref="IAiAudit"/>) before it's kept, and kept <see cref="AiOptions.ExportDays"/> (7)
/// before it's deleted. One line per call, oldest first: when, how it went and why, where (the scope's id and key) and
/// whose, what it went to, its data class, its tokens and cost; and its system prompt, messages and reply, only when
/// the site kept them (a prompt not kept can't be exported).
/// </summary>
public sealed class AiExportService(AiDb aiDb, IAiRights rights, IAiAudit audit, IAiScopes scopes, IOptions<AiOptions> options) : ITransientDependency
{
    public const string ContentType = "application/gzip";

    private const int Page = 1000;

    public async Task<AiExportInfo> CreateAsync(AiActor actor, DateTime from, DateTime to, Guid? scopeId, CancellationToken ct)
    {
        await OwnerAsync(actor, ct);
        AiOptions settings = options.Value;
        DateTime start = DateTime.SpecifyKind(from, DateTimeKind.Utc), end = DateTime.SpecifyKind(to, DateTimeKind.Utc);
        if (end <= start || end - start > TimeSpan.FromDays(settings.MaxExportDays))
        {
            throw new UserFriendlyException($"An export covers a period of up to {settings.MaxExportDays} days.", AiErrorCodes.Invalid);
        }

        AiExport export = new(Guid.NewGuid(), actor.Id, start, end, scopeId, DateTime.UtcNow.AddDays(Math.Max(1, settings.ExportDays)));
        using MemoryStream bytes = new();
        await using (GZipStream zip = new(bytes, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int skip = 0; ; skip += Page)
            {
                List<AiUsageRecord> records = await aiDb.RunAsync(db =>
                {
                    IQueryable<AiUsageRecord> found = db.AiUsage.AsNoTracking().Where(r => r.At >= start && r.At < end);
                    if (scopeId is { } scope)
                    {
                        found = found.Where(r => r.ScopePath.Contains(scope));
                    }

                    return found.OrderBy(r => r.At).ThenBy(r => r.Id).Skip(skip).Take(Page).ToListAsync(ct);
                }, cancellationToken: ct);
                if (records.Count == 0)
                {
                    break;
                }

                List<Guid> ids = [.. records.Select(r => r.Id)];
                Dictionary<Guid, AiPromptRecord> prompts = await aiDb.RunAsync(db => db.AiPrompts.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct),
                    cancellationToken: ct);
                IReadOnlyDictionary<Guid, AiScopeInfo> names = await scopes.GetManyAsync(records.Where(r => r.ScopeId is not null).Select(r => r.ScopeId!.Value).Distinct(), ct);
                foreach (AiUsageRecord record in records)
                {
                    if (export.Calls >= settings.MaxExportCalls || bytes.Length >= settings.MaxExportBytes)
                    {
                        export.Truncated = true;
                        break;
                    }

                    AiPromptRecord? prompt = prompts.GetValueOrDefault(record.Id);
                    string? scope = record.ScopeId is { } id && names.TryGetValue(id, out AiScopeInfo? info) ? info.Key : null;
                    await zip.WriteAsync(Line(record, scope, prompt), ct);
                    export.Calls++;
                    export.Prompts += prompt is null ? 0 : 1;
                }

                if (export.Truncated || records.Count < Page)
                {
                    break;
                }
            }
        }

        export.Content = bytes.ToArray();
        export.Bytes = export.Content.LongLength;

        // In the audit log before it's kept: when the log can't take it, nothing is exported.
        await audit.RecordAsync(new AiAuditEntry(AiAuditActions.CallsExported, actor, export.Id, new Dictionary<string, string?>
        {
            ["from"] = start.ToString("O", CultureInfo.InvariantCulture),
            ["to"] = end.ToString("O", CultureInfo.InvariantCulture),
            ["scopeId"] = scopeId?.ToString(),
            ["calls"] = export.Calls.ToString(CultureInfo.InvariantCulture),
            ["prompts"] = export.Prompts.ToString(CultureInfo.InvariantCulture),
            ["truncated"] = export.Truncated ? "true" : "false",
        }), ct);
        await aiDb.RunAsync(async db =>
        {
            db.AiExports.Add(export);
            await db.SaveChangesAsync(ct);
        }, requiresNew: true, cancellationToken: ct);
        return Info(export);
    }

    /// <summary>The exports still kept, newest first.</summary>
    public async Task<List<AiExportInfo>> ListAsync(AiActor actor, CancellationToken ct)
    {
        await OwnerAsync(actor, ct);
        DateTime now = DateTime.UtcNow;
        return await aiDb.RunAsync(db => db.AiExports.AsNoTracking().Where(e => e.ExpiresAt > now).OrderByDescending(e => e.At)
            .Select(e => new AiExportInfo(e.Id, e.At, e.ActorId, e.From, e.To, e.ScopeId, e.Calls, e.Prompts, e.Truncated, e.Bytes, e.ExpiresAt)).ToListAsync(ct),
            cancellationToken: ct);
    }

    /// <summary>An export's file, recorded in the audit log as downloaded; one past its days is gone.</summary>
    public async Task<AiExportFile> DownloadAsync(AiActor actor, Guid id, CancellationToken ct)
    {
        await OwnerAsync(actor, ct);
        DateTime now = DateTime.UtcNow;
        AiExport export = await aiDb.RunAsync(db => db.AiExports.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && e.ExpiresAt > now, ct), cancellationToken: ct)
            ?? throw new EntityNotFoundException(typeof(AiExport), id);
        await audit.RecordAsync(new AiAuditEntry(AiAuditActions.CallsDownloaded, actor, export.Id, new Dictionary<string, string?>
        {
            ["calls"] = export.Calls.ToString(CultureInfo.InvariantCulture),
        }), ct);
        return new AiExportFile($"ai-calls-{export.From:yyyyMMdd'T'HHmm}-{export.To:yyyyMMdd'T'HHmm}.jsonl.gz", ContentType, export.Content);
    }

    /// <summary>Deletes an export before its days are up.</summary>
    public async Task DeleteAsync(AiActor actor, Guid id, CancellationToken ct)
    {
        await OwnerAsync(actor, ct);
        await aiDb.RunAsync(db => db.AiExports.Where(e => e.Id == id).ExecuteDeleteAsync(ct), cancellationToken: ct);
    }

    /// <summary>One call as a line of JSON, ending in a newline.</summary>
    internal static byte[] Line(AiUsageRecord r, string? scope, AiPromptRecord? prompt)
    {
        using MemoryStream line = new();
        using (Utf8JsonWriter json = new(line))
        {
            json.WriteStartObject();
            json.WriteString("id", r.Id);
            json.WriteString("at", r.At);
            json.WriteString("state", r.State);
            json.WriteString("code", r.Code);
            json.WriteString("error", r.Error);
            json.WriteString("scope_id", r.ScopeId?.ToString());
            json.WriteString("scope", scope);
            json.WriteString("account_id", r.AccountId?.ToString());
            json.WriteString("on_behalf_of", r.OnBehalfOf?.ToString());
            json.WriteString("purpose", r.Purpose);
            json.WriteString("about_kind", r.AboutKind);
            json.WriteString("about_id", r.AboutId);
            json.WriteString("alias", r.Alias);
            json.WriteString("connection", r.Connection);
            json.WriteString("provider", r.Provider);
            json.WriteString("model", r.Model);
            json.WriteString("data_class", r.DataClass);
            json.WriteBoolean("streamed", r.Streamed);
            json.WriteNumber("input_tokens", r.InputTokens);
            json.WriteNumber("cache_write_tokens", r.CacheWriteTokens);
            json.WriteNumber("cache_read_tokens", r.CacheReadTokens);
            json.WriteNumber("output_tokens", r.OutputTokens);
            json.WriteNumber("cost", r.Cost);
            if (r.DurationMs is { } ms)
            {
                json.WriteNumber("duration_ms", ms);
            }
            else
            {
                json.WriteNull("duration_ms");
            }

            json.WriteBoolean("prompt_kept", prompt is not null);
            if (prompt is not null)
            {
                json.WriteString("system", prompt.System);
                json.WritePropertyName("messages");
                json.WriteRawValue(prompt.Messages);
                json.WritePropertyName("reply");
                if (prompt.Reply is { Length: > 0 } reply)
                {
                    json.WriteRawValue(reply);
                }
                else
                {
                    json.WriteNullValue();
                }
            }

            json.WriteEndObject();
        }

        line.WriteByte((byte)'\n');
        return line.ToArray();
    }

    private async Task OwnerAsync(AiActor actor, CancellationToken ct)
    {
        if (!await rights.IsSiteOwnerAsync(actor, ct))
        {
            throw new UserFriendlyException("Only the site's owner exports its AI calls.", AiErrorCodes.Forbidden);
        }
    }

    private static AiExportInfo Info(AiExport e) => new(e.Id, e.At, e.ActorId, e.From, e.To, e.ScopeId, e.Calls, e.Prompts, e.Truncated, e.Bytes, e.ExpiresAt);
}
