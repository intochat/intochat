using IntoChat.Cloud.Mail.Templates;

namespace IntoChat.Cloud.Mail.Templates.Tests.Unit;

public sealed class ProbeFacts
{
    [Fact]
    public void MailTemplateId_AllValues_AreDefined()
    {
        Assert.Equal(8, Enum.GetValues<MailTemplateId>().Length);
    }
}
