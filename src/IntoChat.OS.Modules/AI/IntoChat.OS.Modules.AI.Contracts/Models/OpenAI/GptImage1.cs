using IntoChat.OS.AI.Media;

namespace IntoChat.OS.AI.Models.OpenAI;

public sealed class GptImage1 : ImageModel<IGptImage1>
{
    public override string Id => "gpt-image-1";

    public override AiProvider Provider => AiProvider.OpenAI;
}

public interface IGptImage1 : IImageModel;
