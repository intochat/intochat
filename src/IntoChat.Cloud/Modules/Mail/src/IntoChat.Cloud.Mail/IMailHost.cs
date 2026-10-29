namespace IntoChat.Cloud.Mail;

// The host stamps the mail module as the capability caller while it reads its own secrets. The cloud kernel binds it.
public interface IMailHost
{
    IDisposable StampModule();
}
