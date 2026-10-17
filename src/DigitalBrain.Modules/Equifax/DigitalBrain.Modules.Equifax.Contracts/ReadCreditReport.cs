namespace DigitalBrain.Equifax;

// DocumentNumber is a Peruvian DNI: exactly eight digits.
[GenerateSerializer, Alias("equifax.read-credit-report")]
public sealed record ReadCreditReport([property: Id(0)] string DocumentNumber, [property: Id(1)] string? Purpose = null)
{
    public static bool IsDni(string? value) => value is { Length: 8 } && value.All(char.IsAsciiDigit);
}
