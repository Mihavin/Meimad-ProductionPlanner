using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.Concurrency;

namespace Meimad.Planner.Server.Api.Accounts;

/// <summary>
/// Explains every refused change that was based on stale data in one shape, so the Windows client can
/// tell the user what changed, who changed it and when, and what to do (owner decision 2026-09-27).
/// A conflict thrown as <see cref="EditConflictException"/> becomes 409 <c>edit_conflict</c>; an
/// endpoint's own version or state refusal (412 <c>resource_version_stale</c>, 409 <c>*_changed</c>,
/// ...) keeps its code and gains the same <c>conflict</c> object. Who and when come from the
/// exception or, for the endpoint refusals, from the <see cref="ChangeJournal"/> of saved changes.
/// </summary>
internal sealed class ConflictResponseMiddleware(RequestDelegate next)
{
    private static readonly Dictionary<string, string> ResourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cases"] = "Case",
        ["operations"] = "Case Operation",
        ["components"] = "Case Component",
        ["model-files"] = "Case model file",
        ["orders"] = "Order",
        ["batches"] = "Work Order",
        ["batch-operations"] = "Work Order operation",
        ["machines"] = "Machine",
        ["machine-types"] = "Machine type",
        ["machine-assignments"] = "Planning Board backlog item",
        ["resources"] = "Employee",
        ["working-calendars"] = "working calendar",
        ["tool-catalog"] = "tool library entry",
        ["tool-preparations"] = "Tool Room item",
        ["qc-queue"] = "QC queue item",
        ["manufacturing-programs"] = "Manufacturing Program",
        ["production-runs"] = "Production Run",
        ["production-packages"] = "Production Package",
        ["postprocessors"] = "postprocessor",
        ["users"] = "user account",
        ["user-types"] = "user type"
    };

    public async Task InvokeAsync(HttpContext context, ChangeJournal journal, IAccountRepository accounts)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? string.Empty;
        var isChange = (HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
                || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method))
            && path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/v1/auth/", StringComparison.OrdinalIgnoreCase);
        if (!isChange)
        {
            await RunAsync(context, accounts);
            return;
        }

        // A change's answer is small JSON; hold it to add the conflict explanation before it leaves.
        var body = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await RunAsync(context, accounts);
        }
        finally
        {
            context.Response.Body = body;
        }

        var status = context.Response.StatusCode;
        if (status is >= 200 and < 300 && PlanningHttpSupport.CurrentUser(context) is { } user)
        {
            journal.Record(path, user.UserName);
        }
        else if (status is StatusCodes.Status409Conflict or StatusCodes.Status412PreconditionFailed
                 && await ExplainAsync(buffer, path, journal, accounts, context.RequestAborted) is { } explained)
        {
            buffer.SetLength(0);
            buffer.Write(explained);
            context.Response.ContentLength = explained.Length;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(body, context.RequestAborted);
    }

    private async Task RunAsync(HttpContext context, IAccountRepository accounts)
    {
        try
        {
            await next(context);
        }
        catch (EditConflictException conflict) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await Results.Json(
                new
                {
                    error = new
                    {
                        code = "edit_conflict",
                        message = conflict.Message,
                        correlationId = context.TraceIdentifier,
                        details = Array.Empty<object>(),
                        conflict = new
                        {
                            resource = conflict.Resource,
                            changedBy = await NameAsync(conflict.ChangedBy, accounts, context.RequestAborted),
                            changedAt = conflict.ChangedAt,
                            advice = conflict.Advice
                        }
                    }
                },
                statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context);
        }
        catch (AccountException exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await PlanningHttpSupport.Error(exception.Status, exception.Code, exception.Message, context).ExecuteAsync(context);
        }
    }

    private static async Task<byte[]?> ExplainAsync(
        MemoryStream buffer, string path, ChangeJournal journal, IAccountRepository accounts, CancellationToken token)
    {
        JsonNode? document;
        try
        {
            document = JsonNode.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        if (document?["error"] is not JsonObject error
            || error["conflict"] is not null
            || error["code"]?.GetValueKind() != JsonValueKind.String
            || !IsStaleData(error["code"]!.GetValue<string>()))
        {
            return null;
        }

        var resource = ResourceName(path);
        var change = journal.LatestRelatedTo(path);
        error["conflict"] = new JsonObject
        {
            ["resource"] = resource,
            ["changedBy"] = await NameAsync(change?.UserName, accounts, token),
            ["changedAt"] = change?.ChangedAt,
            ["advice"] = $"Someone saved a change to this {resource} after you opened it, so your change was not saved. " +
                         $"Refresh to load the current {resource}, check what changed, then make your change again."
        };
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }

    /// <summary>A refusal because the data the request was based on is no longer current.</summary>
    internal static bool IsStaleData(string code) =>
        code is "resource_version_stale" or "precondition_failed" or "version_conflict" or "stale"
        || code.EndsWith("_stale", StringComparison.Ordinal)
        || code.EndsWith("_changed", StringComparison.Ordinal)
        || code.EndsWith("_version_conflict", StringComparison.Ordinal);

    private static string ResourceName(string path)
    {
        // /api/v1/<collection>/<id>/<collection>/<id>...: the deepest collection the user knows by name.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = segments.Length - 1; index >= 2; index--)
        {
            if (ResourceNames.TryGetValue(segments[index], out var name)) return name;
        }
        return "item";
    }

    private static async Task<string?> NameAsync(string? userName, IAccountRepository accounts, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        var displayName = await accounts.FindDisplayNameAsync(userName, token);
        return displayName is null || string.Equals(displayName, userName, StringComparison.OrdinalIgnoreCase)
            ? userName
            : $"{displayName} ({userName})";
    }
}
