namespace DigitalBrain.Framework.Platform.Connections;

[GenerateSerializer, Alias("sealed-secret")]
public sealed record SealedSecret(
    [property: Id(0)] string Name,
    [property: Id(1)] string KeyId,
    [property: Id(2)] string Ciphertext);
