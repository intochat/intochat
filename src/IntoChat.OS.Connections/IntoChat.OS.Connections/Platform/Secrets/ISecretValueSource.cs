namespace IntoChat.OS.Framework.Platform.Secrets;

// Framework-internal: the secrets store hands the redactor the current plaintext values of the
// calling brain's vault. Not a grain call surface, so the public ISecrets contract stays as it is.
internal interface ISecretValueSource
{
    ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken);
}
