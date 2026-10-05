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
        Assert.Equal("recurring:storage.gb_month:2026-09", meter.IntentId);

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
}
