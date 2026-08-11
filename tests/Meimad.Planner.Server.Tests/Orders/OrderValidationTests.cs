using Meimad.Planner.Server.Domain.Orders;

namespace Meimad.Planner.Server.Tests.Orders;

public sealed class OrderValidationTests
{
    [Fact]
    public void Valid_values_are_normalized_and_active_status_is_demand()
    {
        var values = OrderValidator.ValidateAndNormalize(new OrderValues(
            " case-1 ",
            " WO-1042 ",
            50,
            "2026-08-20",
            "active",
            " shop note "));

        Assert.Equal("case-1", values.CaseId);
        Assert.Equal("WO-1042", values.OrderNumber);
        Assert.Equal(new DateOnly(2026, 8, 20), values.WorkFinishDate);
        Assert.Equal(OrderStatus.Active, values.Status);
        Assert.True(values.Status.IsActiveDemand());
        Assert.Equal("shop note", values.Notes);
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("complete", false)]
    [InlineData("cancelled", false)]
    public void Status_tokens_define_active_demand(string token, bool isActiveDemand)
    {
        Assert.True(OrderStatuses.TryParseContractToken(token, out var status));
        Assert.Equal(isActiveDemand, status.IsActiveDemand());
        Assert.Equal(token, status.ToContractToken());
    }

    [Fact]
    public void Invalid_quantity_date_status_and_required_fields_are_reported_together()
    {
        var exception = Assert.Throws<OrderValidationException>(() =>
            OrderValidator.ValidateAndNormalize(new OrderValues(
                null,
                " ",
                0,
                "20/08/2026",
                "ACTIVE",
                null)));

        Assert.Contains(exception.Issues, issue => issue.Field == "caseId");
        Assert.Contains(exception.Issues, issue => issue.Field == "orderNumber");
        Assert.Contains(exception.Issues, issue => issue.Field == "quantity");
        Assert.Contains(exception.Issues, issue => issue.Field == "workFinishDate");
        Assert.Contains(exception.Issues, issue => issue.Field == "status");
    }
}
