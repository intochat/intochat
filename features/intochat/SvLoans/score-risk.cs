using DigitalBrain.Apps;
using DigitalBrain.RiskSeal;

// One reaction: an applicant's email, phone, IP and DNI are scored by RiskSeal.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
await foreach (var request in brain.On(app, "score-risk", brain.Stopping))
{
    await Spike.Run(brain, key, "score-risk", request.RequestId, request.Input, async () =>
    {
        var riskSeal = brain.Get<IRiskSeal>(Spike.Require(brain, "riskseal", "riskseal"));
        var input = Spike.ParseObject(request.Input);
        var email = Spike.Text(input, "email");
        var phone = Spike.Text(input, "phone");
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone)) { throw new InvalidOperationException("Give an email or a phone to score."); }
        var score = await riskSeal.Score(new(email, phone, Spike.Text(input, "ip"), Spike.Text(input, "dni")));
        var signals = string.Join(", ", score.Signals.Select(signal => $"{signal.Name}={signal.Value}"));
        return ($"score={score.Score} level={score.RiskLevel} request={score.RequestId} signals: {signals}", score.RiskLevel);
    });
}
