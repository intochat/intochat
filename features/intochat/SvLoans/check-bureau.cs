using DigitalBrain.Apps;
using DigitalBrain.Equifax;

// One reaction: a DNI asks Equifax for the credit report; the flags become the result.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
await foreach (var request in brain.On(app, "check-bureau", brain.Stopping))
{
    var dni = request.Input.Trim();
    await Spike.Run(brain, key, "check-bureau", request.RequestId, dni, async () =>
    {
        var equifax = brain.Get<IEquifax>(Spike.Require(brain, "equifax", "equifax"));
        if (!ReadCreditReport.IsDni(dni)) { throw new InvalidOperationException("A DNI is exactly eight digits."); }
        var report = await equifax.ReadCreditReport(new(dni, "sv-loans spike"));
        var flags = report.Flags;
        return ($"infocorpNegative={flags.InfocorpNegative} sbsRating={flags.SbsRating} maxDaysDelay={flags.MaxDaysDelay} " +
            $"sicomUnpaidDebts={flags.SicomUnpaidDebts} hasMortgage={flags.HasMortgage} hasCarLoan={flags.HasCarLoan}", "read");
    });
}
