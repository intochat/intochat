namespace DigitalBrain.Kernel.Platform.Auth;

public sealed record OAuthTokens(string AccessToken, string? RefreshToken);
