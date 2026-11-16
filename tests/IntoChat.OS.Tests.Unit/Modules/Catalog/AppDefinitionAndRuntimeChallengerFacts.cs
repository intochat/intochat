using System.Reflection;
using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class AppDefinitionAndRuntimeChallengerFacts
{
    [Theory]
    [InlineData(typeof(FilmCraftApp), "app.filmcraft")]
    [InlineData(typeof(PhotoCraftApp), "app.photocraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.ArtCraftApp), "app.artcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.VectorCraftApp), "app.vectorcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.LightCraftApp), "app.lightcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.EffectCraftApp), "app.effectcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.DesignCraftApp), "app.designcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.PdfCraftApp), "app.pdfcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.WordCraftApp), "app.wordcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.SoundCraftApp), "app.soundcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.DeckCraftApp), "app.deckcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.CadCraftApp), "app.cadcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.GridCraftApp), "app.gridcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.FigCraftApp), "app.figcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.TextureCraftApp), "app.texturecraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.MaterialCraftApp), "app.materialcraft")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.SolidCraftApp), "app.solidcraft")]
    [InlineData(typeof(CreatorApp), "app.creator")]
    [InlineData(typeof(DesktopApp), "app.desktop")]
    [InlineData(typeof(GalleryApp), "app.gallery")]
    [InlineData(typeof(InoApp), "app.ino")]
    [InlineData(typeof(FriendsApp), "app.friends")]
    public void ShellAppScreenNeuronsMatchNeuronAliasContract(Type screenType, string expectedNeuronType)
    {
        Assert.True(typeof(IScreen).IsAssignableFrom(screenType), $"{screenType.Name} must implement IScreen.");
        var attr = screenType.GetCustomAttribute<NeuronAttribute>(inherit: false);
        Assert.NotNull(attr);
        Assert.Equal(expectedNeuronType, attr!.Type);
    }
}
