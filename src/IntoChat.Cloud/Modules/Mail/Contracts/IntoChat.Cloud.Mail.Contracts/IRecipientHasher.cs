namespace IntoChat.Cloud.Mail;

public interface IRecipientHasher
{
    string Hash(string purpose, string email);
}
