using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.Orders;
using Meimad.Planner.Server.Domain.Orders;

namespace Meimad.Planner.Server.Api.Orders;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateOrderRequest(
    string? CaseId,
    string? OrderNumber,
    int Quantity,
    string? WorkFinishDate,
    string? Status,
    string? Notes,
    decimal? Price = null)
{
    internal CreateOrderCommand ToCommand() => new(
        CaseId,
        OrderNumber,
        Quantity,
        WorkFinishDate,
        Status,
        Notes,
        Price);
}

internal sealed class PatchOrderRequest
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Fields { get; init; } =
        new(StringComparer.Ordinal);

    internal UpdateOrderCommand ToCommand()
    {
        var reader = new PatchFieldReader(Fields);
        var command = new UpdateOrderCommand(
            reader.ReadString("orderNumber"),
            reader.ReadNullableInt32("quantity"),
            reader.ReadString("workFinishDate"),
            reader.ReadString("status"),
            reader.ReadString("notes"),
            reader.ReadNullableDecimal("price"));
        reader.ThrowIfInvalid();
        return command;
    }

    private sealed class PatchFieldReader
    {
        private static readonly HashSet<string> AllowedFields =
        [
            "orderNumber",
            "quantity",
            "workFinishDate",
            "status",
            "notes",
            "price"
        ];

        private readonly IReadOnlyDictionary<string, JsonElement> fields;
        private readonly List<OrderRequestIssue> issues = [];

        internal PatchFieldReader(IReadOnlyDictionary<string, JsonElement> fields)
        {
            this.fields = fields;
            foreach (var field in fields.Keys)
            {
                if (!AllowedFields.Contains(field))
                {
                    issues.Add(new OrderRequestIssue(
                        field,
                        "unknown_field",
                        $"Field '{field}' is not supported."));
                }
            }

            if (fields.Count == 0)
            {
                issues.Add(new OrderRequestIssue(
                    string.Empty,
                    "empty_patch",
                    "At least one Order field must be supplied."));
            }
        }

        internal OrderField<string?> ReadString(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return OrderField<string?>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return OrderField<string?>.Specified(null);
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                return OrderField<string?>.Specified(element.GetString());
            }

            AddTypeIssue(name, "string or null");
            return OrderField<string?>.Unspecified;
        }

        internal OrderField<int?> ReadNullableInt32(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return OrderField<int?>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return OrderField<int?>.Specified(null);
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            {
                return OrderField<int?>.Specified(value);
            }

            AddTypeIssue(name, "32-bit integer or null");
            return OrderField<int?>.Unspecified;
        }

        internal OrderField<decimal?> ReadNullableDecimal(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return OrderField<decimal?>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return OrderField<decimal?>.Specified(null);
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value))
            {
                return OrderField<decimal?>.Specified(value);
            }

            AddTypeIssue(name, "decimal number or null");
            return OrderField<decimal?>.Unspecified;
        }

        internal void ThrowIfInvalid()
        {
            if (issues.Count > 0)
            {
                throw new OrderRequestException(issues);
            }
        }

        private void AddTypeIssue(string name, string expected)
        {
            issues.Add(new OrderRequestIssue(
                name,
                "invalid_type",
                $"Field '{name}' must be a {expected}."));
        }
    }
}

internal sealed record OrderResponse(
    string OrderId,
    string CaseId,
    string OrderNumber,
    int Quantity,
    string WorkFinishDate,
    string Status,
    string? Notes,
    decimal? Price,
    bool IsKitaronManaged,
    bool IsHistorical,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static OrderResponse FromDomain(PlannerOrder order) => new(
        order.OrderId,
        order.CaseId,
        order.OrderNumber,
        order.Quantity,
        order.WorkFinishDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        order.IsKitaronManaged && order.KitaronStatus is not null
            ? order.KitaronStatus
            : order.Status.ToContractToken(),
        order.Notes,
        order.Price,
        order.IsKitaronManaged,
        order.IsHistorical,
        order.Version,
        order.CreatedAt,
        order.UpdatedAt);
}

internal sealed record OrderListResponse(IReadOnlyList<OrderResponse> Items, string? NextCursor);

internal sealed record OrderRequestIssue(string Field, string Code, string Message);

internal sealed class OrderRequestException : Exception
{
    internal OrderRequestException(IReadOnlyList<OrderRequestIssue> issues)
        : base("Order request is invalid.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<OrderRequestIssue> Issues { get; }
}
