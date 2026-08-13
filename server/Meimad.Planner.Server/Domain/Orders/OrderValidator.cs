using System.Globalization;

namespace Meimad.Planner.Server.Domain.Orders;

internal static class OrderValidator
{
    private const int IdentifierMaximum = 200;
    private const int NotesMaximum = 8000;

    internal static ValidatedOrderValues ValidateAndNormalize(OrderValues values)
    {
        var issues = new List<OrderValidationIssue>();
        var caseId = RequiredText(values.CaseId, "caseId", IdentifierMaximum, issues);
        var orderNumber = RequiredText(
            values.OrderNumber,
            "orderNumber",
            IdentifierMaximum,
            issues);

        if (values.Quantity <= 0)
        {
            issues.Add(new OrderValidationIssue(
                "quantity",
                "positive_required",
                "quantity must be greater than zero."));
        }

        DateOnly workFinishDate = default;
        if (!DateOnly.TryParseExact(
                values.WorkFinishDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out workFinishDate))
        {
            issues.Add(new OrderValidationIssue(
                "workFinishDate",
                "invalid_date",
                "workFinishDate must use the YYYY-MM-DD calendar-date format."));
        }

        if (!OrderStatuses.TryParseContractToken(values.Status?.Trim(), out var status))
        {
            issues.Add(new OrderValidationIssue(
                "status",
                "invalid_status",
                "status must be active, in_production, complete, or cancelled."));
        }

        var notes = OptionalText(values.Notes, "notes", NotesMaximum, issues);

        if (issues.Count > 0)
        {
            throw new OrderValidationException(issues);
        }

        return new ValidatedOrderValues(
            caseId!,
            orderNumber!,
            values.Quantity,
            workFinishDate,
            status,
            notes);
    }

    private static string? RequiredText(
        string? value,
        string field,
        int maximumLength,
        ICollection<OrderValidationIssue> issues)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            issues.Add(new OrderValidationIssue(field, "required", $"{field} is required."));
            return null;
        }

        ValidateLength(normalized, field, maximumLength, issues);
        return normalized;
    }

    private static string? OptionalText(
        string? value,
        string field,
        int maximumLength,
        ICollection<OrderValidationIssue> issues)
    {
        var normalized = Normalize(value);
        if (normalized is not null)
        {
            ValidateLength(normalized, field, maximumLength, issues);
        }

        return normalized;
    }

    private static void ValidateLength(
        string value,
        string field,
        int maximumLength,
        ICollection<OrderValidationIssue> issues)
    {
        if (value.Length > maximumLength)
        {
            issues.Add(new OrderValidationIssue(
                field,
                "too_long",
                $"{field} must contain at most {maximumLength} characters."));
        }
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

internal sealed record OrderValues(
    string? CaseId,
    string? OrderNumber,
    int Quantity,
    string? WorkFinishDate,
    string? Status,
    string? Notes);

internal sealed record ValidatedOrderValues(
    string CaseId,
    string OrderNumber,
    int Quantity,
    DateOnly WorkFinishDate,
    OrderStatus Status,
    string? Notes);

internal sealed record OrderValidationIssue(string Field, string Code, string Message);

internal sealed class OrderValidationException : Exception
{
    internal OrderValidationException(IReadOnlyList<OrderValidationIssue> issues)
        : base("Order validation failed.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<OrderValidationIssue> Issues { get; }
}
