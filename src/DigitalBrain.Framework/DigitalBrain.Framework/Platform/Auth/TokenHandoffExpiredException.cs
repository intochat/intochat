namespace DigitalBrain.Framework.Platform.Auth;

public sealed class TokenHandoffExpiredException() : InvalidOperationException("The login expired. Sign in again.");
