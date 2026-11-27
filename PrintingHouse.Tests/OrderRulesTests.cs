using PrintingHouse.Core;
using Xunit;

namespace PrintingHouse.Tests;

public sealed class OrderRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(OrderStatus.Accepted, OrderStatus.Layout)]
    [InlineData(OrderStatus.Layout, OrderStatus.Approved)]
    [InlineData(OrderStatus.Approved, OrderStatus.Printing)]
    [InlineData(OrderStatus.Printing, OrderStatus.Ready)]
    [InlineData(OrderStatus.Ready, OrderStatus.Issued)]
    public void AdvancesThroughEachStage(OrderStatus from, OrderStatus to)
    {
        var order = Order(from);
        OrderRules.Transition(order, to, null, Now);
        Assert.Equal(to, order.Status);
        Assert.Equal(2, order.Version);
        Assert.Equal(to == OrderStatus.Issued ? Now : (DateTime?)null, order.CompletedAt);
    }

    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.Layout)]
    [InlineData(OrderStatus.Approved)]
    [InlineData(OrderStatus.Printing)]
    [InlineData(OrderStatus.Ready)]
    public void CancelsOpenOrdersWithReason(OrderStatus from)
    {
        var order = Order(from);
        Assert.Throws<RuleException>(() => OrderRules.Transition(order, OrderStatus.Cancelled, " ", Now));
        OrderRules.Transition(order, OrderStatus.Cancelled, " Тираж больше не нужен ", Now);
        Assert.Equal("Тираж больше не нужен", order.CancellationReason);
    }

    [Theory]
    [InlineData(OrderStatus.Accepted, OrderStatus.Ready)]
    [InlineData(OrderStatus.Printing, OrderStatus.Layout)]
    [InlineData(OrderStatus.Issued, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Accepted)]
    public void RejectsInvalidTransitions(OrderStatus from, OrderStatus to)
    {
        var order = Order(from);
        Assert.Throws<RuleException>(() => OrderRules.Transition(order, to, null, Now));
        Assert.Equal(from, order.Status);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void ApprovalRequiresLayoutReference()
    {
        var order = Order(OrderStatus.Layout);
        order.LayoutReference = null;
        Assert.Throws<RuleException>(() => OrderRules.Transition(order, OrderStatus.Approved, null, Now));
    }

    [Fact]
    public void PrintingRequiresAssignedPrinter()
    {
        var order = Order(OrderStatus.Approved);
        order.PrinterId = null;
        Assert.Throws<RuleException>(() => OrderRules.Transition(order, OrderStatus.Printing, null, Now));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void IssueRequiresAmountAndDueDate(bool amount, bool date)
    {
        var order = Order(OrderStatus.Ready);
        order.Amount = amount ? 20_000 : null;
        order.DueDate = date ? new DateOnly(2026, 11, 28) : null;
        Assert.Throws<RuleException>(() => OrderRules.Transition(order, OrderStatus.Issued, null, Now));
    }

    [Theory]
    [InlineData(OrderStatus.Accepted, false)]
    [InlineData(OrderStatus.Approved, false)]
    [InlineData(OrderStatus.Printing, true)]
    [InlineData(OrderStatus.Ready, false)]
    public void PrinterCanOnlyMarkPrintingReady(OrderStatus from, bool available)
    {
        var targets = OrderRules.Available(from, Roles.Printer);
        if (available)
            Assert.Equal(OrderStatus.Ready, Assert.Single(targets));
        else
            Assert.Empty(targets);
    }

    [Fact]
    public void StatisticsSummarizeActiveAndIssuedOrders()
    {
        var result = OrderRules.Statistics(
        [
            Row(OrderStatus.Issued, 20400, Now.AddDays(-4), Now),
            Row(OrderStatus.Printing, 16000, Now, null),
            Row(OrderStatus.Cancelled, null, Now, null)
        ]);
        Assert.Equal(3, result.Total);
        Assert.Equal(1, result.Open);
        Assert.Equal(1, result.Issued);
        Assert.Equal(1, result.Cancelled);
        Assert.Equal(36400, result.TotalAmount);
        Assert.Equal(20400, result.IssuedAmount);
        Assert.Equal(4, result.AverageDays);
    }

    private static PrintOrder Order(OrderStatus status) => new()
    {
        Status = status, Version = 1, PrinterId = 3, Quantity = 500,
        LayoutReference = "catalog.pdf", Amount = 20_000, DueDate = new DateOnly(2026, 11, 28)
    };

    internal static OrderInfo Row(OrderStatus status, decimal? amount, DateTime created, DateTime? completed) =>
        new(1, 1, "Музей", "+79990001234", "Каталог \"Город\" <тираж>", "А5 · 4+4", "", 3, "Илья",
            status, amount, new DateOnly(2026, 11, 28), null, created, completed, 1, 500, "catalog.pdf");
}
