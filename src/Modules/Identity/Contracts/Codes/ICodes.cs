using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Modules.Identity.Contracts.Codes;

public enum CodePurpose
{
    Legacy = 0,
    Friend = 1,
    Device = 2,
}

public sealed record CodeTarget(BrainId Brain, string Reference, DateTimeOffset ExpiresAt);

[NeuronAlias("circles.invite-key"), DefaultNeuronType("circles.invite-key"), Internal]
public interface ICodeKey
{
    const string Type = "circles.invite-key";
    const string Key = "system/main";
    const string Configuration = "IntoChatOS:Circles:InviteHmacKey";

    Task<byte[]> Get();
}

[NeuronAlias("circles.invite-code"), DefaultNeuronType("circles.invite-code"), Internal]
public interface ICodeIndex
{
    const string Type = "circles.invite-code";

    Task Bind(CodePurpose purpose, string brain, string reference, DateTimeOffset expiresAt);
    Task<CodeTarget?> Resolve(CodePurpose purpose);
    Task<CodeTarget?> Lookup(CodePurpose purpose);
    Task Spend();

    static string Key(string codeHash) => $"system/{codeHash}";
}

public interface ICodeBook
{
    Task<string> Bind(Code code, CodePurpose purpose, BrainId brain, string reference, DateTimeOffset expiresAt);
    Task<string> HashOf(Code code);
    Task<CodeTarget?> Find(CodePurpose purpose, string hash);
    Task<CodeTarget?> Lookup(CodePurpose purpose, string hash);
    Task Spend(string hash);
}
