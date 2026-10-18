using System.Globalization;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;

// "add" sums "a+b" (more terms are summed too) into the sum neuron. Input that is not a sum answers
// "Error" instead of throwing, so one bad request never stops the synapse or blocks the next one.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var sum = brain.Get<IText>(appKey + "/sum");
await foreach (var request in brain.On(app, "add", brain.Stopping))
{
    await sum.Set(Sum(request.Input));
}

static string Sum(string input)
{
    const NumberStyles number = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
    var terms = input.Split('+');
    var values = terms.Select(term => decimal.TryParse(term, number, CultureInfo.InvariantCulture, out var value) ? value : (decimal?)null).ToArray();
    if (values.Length < 2 || values.Any(value => value is null)) { return "Error"; }
    try { return values.Sum(value => value!.Value).ToString(CultureInfo.InvariantCulture); }
    catch (OverflowException) { return "Error"; }
}
