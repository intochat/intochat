using DigitalBrain.Compute;
using DigitalBrain.Compute.Billing;
using DigitalBrain.Compute.Metering;

namespace DigitalBrain.Modules.Compute.Tests;

public sealed class BillingFacts
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecurringMetersAccrueWithoutAUserIntentAndAppearOnTheStatement()
    {
        var meter = RecurringMeters.Accrue("ws-1", "storage.gb_month", 2m, "gb_month", new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("recurring:ws-1:storage.gb_month:2026-09", meter.IntentId);

        var statement = StatementBuilder.Build("account", "2026-09", [], [meter], new PriceBook(), Now);

        var line = Assert.Single(statement.Lines);
        Assert.True(line.Recurring);
        Assert.Equal("storage.gb_month", line.MeterId);
    }

    [Fact]
    public void AStatementRollsSettledReservationsAndRecurringMetersIntoLines()
    {
        var settled = new AllowanceReservation
        {
            ReservationId = "intent-1:neuron:chat", AllowanceId = "budget",
            ReservedCompute = 5m, SettledCompute = 3m, Settled = true,
            OccurredAt = Now, IntentId = "intent-1", BrainId = "brain-1",
            AppId = "app-1", Operation = "chat",
        };
        var meter = RecurringMeters.Accrue("brain-1", "storage.gb_month", 2m, "gb_month", Now);
        var statement = StatementBuilder.Build("account", "2026-09", [settled], [meter], new PriceBook(), Now);

        Assert.Equal(2, statement.Lines.Count);
        var call = statement.Lines.Single(line => !line.Recurring);
        Assert.Equal(3m, call.Compute);
        Assert.Equal("chat", call.Operation);
        Assert.Equal("app-1", call.AppId);
    }

    [Fact]
    public void AStatementMapsToADeterministicInvoice()
    {
        var meter = RecurringMeters.Accrue("ws-1", "storage.gb_month", 2m, "gb_month", new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var statement = StatementBuilder.Build("account", "2026-09", [], [meter], new PriceBook(), Now);

        var invoice = BillingService.InvoiceFor(statement);

        Assert.Equal("inv:account:2026-09", invoice.InvoiceId);
        Assert.Equal(statement.Lines, invoice.Lines);
        Assert.Equal(statement.TotalUsd, invoice.AmountUsd);
    }

    [Fact]
    public async Task TwoBrainsAccruingTheSameMeterAndPeriodAreBothRecorded()
    {
        var first = RecurringMeters.Accrue("brain-1", "storage.gb_month", 1m, "gb_month", Now);
        var second = RecurringMeters.Accrue("brain-2", "storage.gb_month", 2m, "gb_month", Now);
        var store = new InMemoryMeterStore();

        Assert.True(await store.AppendAsync(first, TestContext.Current.CancellationToken));
        Assert.True(await store.AppendAsync(second, TestContext.Current.CancellationToken));
        Assert.NotEqual(first.IntentId, second.IntentId);
    }

    [Fact]
    public void AStatementOmitsUnsettledZeroSettledAndOutOfPeriodReservations()
    {
        static AllowanceReservation Reservation(string id, bool settled, decimal compute, DateTimeOffset at) => new()
        {
            ReservationId = id, AllowanceId = "budget", ReservedCompute = 5m, SettledCompute = compute,
            Settled = settled, OccurredAt = at, IntentId = id, BrainId = "brain-1", AppId = "app-1", Operation = "chat",
        };
        var statement = StatementBuilder.Build("account", "2026-09",
        [
            Reservation("unsettled", false, 3m, Now),
            Reservation("zero", true, 0m, Now),
            Reservation("august", true, 3m, new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero)),
            Reservation("october", true, 3m, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
        ], [], new PriceBook(), Now);

        Assert.Empty(statement.Lines);
        Assert.Equal(0m, statement.TotalCompute);
    }
}
