using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Chart;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Collection;
using DigitalBrain.Flutter.FileInput;
using DigitalBrain.Flutter.Form;
using DigitalBrain.Flutter.ImageCanvas;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Select;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Table;
using DigitalBrain.Flutter.Tabs;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.VoiceInput;
using DigitalBrain.Flutter.WebBrowser;
using DigitalBrain.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DigitalBrain.Flutter;

internal static class AppUiEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var apps = BrainRoutes.Group(endpoints, "/apps").AddEndpointFilter(ModuleRouteGuard.Guard);
        apps.MapGet("/node", async (string kind, string name, IDigitalBrain brain, CancellationToken ct) =>
        {
            var scope = BrainScope.CurrentId();
            if (!OwnsUi(scope, name)) { throw new UnauthorizedAccessException("The UI belongs to another brain."); }
            return kind switch
            {
                "text" => Results.Ok(await brain.Get<IText>(name).Read().WaitAsync(ct)),
                "button" => Results.Ok(await brain.Get<IButton>(name).Read().WaitAsync(ct)),
                "textfield" => Results.Ok(await brain.Get<ITextField>(name).Read().WaitAsync(ct)),
                "select" => Results.Ok(await brain.Get<ISelect>(name).Read().WaitAsync(ct)),
                "fileinput" => Results.Ok(await brain.Get<IFileInput>(name).Read().WaitAsync(ct)),
                "card" => Results.Ok(await brain.Get<ICard>(name).Read().WaitAsync(ct)),
                "tabs" => Results.Ok(await brain.Get<ITabs>(name).Read().WaitAsync(ct)),
                "surface" => Results.Ok(await brain.Get<ISurface>(name).Read().WaitAsync(ct)),
                "layout" => Results.Ok(await brain.Get<ILayout>(name).Read().WaitAsync(ct)),
                "collection" => Results.Ok(await brain.Get<ICollectionView>(name).Read().WaitAsync(ct)),
                "imagecanvas" => Results.Ok(await brain.Get<IImageCanvas>(name).Read().WaitAsync(ct)),
                "form" => Results.Ok(await brain.Get<IForm>(name).Read().WaitAsync(ct)),
                "table" => Results.Ok(await brain.Get<ITable>(name).Read().WaitAsync(ct)),
                "chart" => Results.Ok(await brain.Get<IChart>(name).Read().WaitAsync(ct)),
                UIVocabulary.ChatType => Results.Ok(await brain.Get<IChat>(name).Read().WaitAsync(ct)),
                UIVocabulary.ComposerType => Results.Ok(await brain.Get<IComposer>(name).Read().WaitAsync(ct)),
                UIVocabulary.VoiceInputType => Results.Ok(await brain.Get<IVoiceInput>(name).Read().WaitAsync(ct)),
                UIVocabulary.WebBrowserType => Results.Ok(await brain.Get<IWebBrowser>(name).Read().WaitAsync(ct)),
                _ => throw new ArgumentException("Unknown app UI kind.")
            };
        });
        apps.MapPost("/event", async (UiEvent input, IDigitalBrain brain, [FromServices] IGrainFactory grains, CancellationToken ct) =>
        {
            var scope = BrainScope.CurrentId();
            if (!OwnsUi(scope, input.Name)) { throw new UnauthorizedAccessException(); }
            switch (input.Kind)
            {
                case UIVocabulary.ChatType:
                    var chat = brain.Get<IChat>(input.Name);
                    if (input.Read is { } read)
                    {
                        if (input.Action is not null) { throw new ArgumentException("Choose a chat action or a read acknowledgement."); }
                        await chat.MarkRead(input.Context ?? "", input.Revision, read.Version, read.MessageIds).WaitAsync(ct);
                    }
                    else { await chat.Activate(input.Context ?? "", input.Revision, input.Action ?? "").WaitAsync(ct); }
                    break;
                case UIVocabulary.ComposerType:
                    var composer = brain.Get<IComposer>(input.Name);
                    switch (input.Action)
                    {
                        case "edit": await composer.Edit(input.Context ?? "", input.Value ?? "").WaitAsync(ct); break;
                        case "submit": await composer.Submit(input.OperationId ?? Guid.Empty, input.Context ?? "", input.Value ?? "").WaitAsync(ct); break;
                        case "voice-begin": await composer.BeginVoice(input.OperationId ?? Guid.Empty, input.Context ?? "").WaitAsync(ct); break;
                        case "voice-audio": await composer.CaptureVoice(input.OperationId ?? Guid.Empty, DecodeAudio(input.Value), input.MimeType ?? "").WaitAsync(ct); break;
                        case "voice-cancel": await composer.CancelVoice(input.OperationId ?? Guid.Empty).WaitAsync(ct); break;
                        case "voice-error": await composer.CompleteVoice(input.OperationId ?? Guid.Empty, null, input.Value ?? "Recording failed. Try again.").WaitAsync(ct); break;
                        default: throw new ArgumentException("Unknown composer action.");
                    }
                    break;
                case UIVocabulary.WebBrowserType:
                    var browser = grains.GetGrain<IWebBrowserConnector>(input.Name);
                    if (input.Action == "unavailable")
                    {
                        await browser.Unavailable(input.Value ?? "The browser is unavailable.").WaitAsync(ct);
                        break;
                    }
                    BrowserConnect connection;
                    try { connection = JsonSerializer.Deserialize<BrowserConnect>(input.Value ?? "", JsonSerializerOptions.Web) ?? throw new ArgumentException("Browser connection is required."); }
                    catch (JsonException) { throw new ArgumentException("Invalid browser connection."); }
                    if (input.Action == "connect") { await browser.Connect(connection.Port, connection.SessionId).WaitAsync(ct); }
                    else if (input.Action == "disconnect") { await browser.Disconnect(connection.SessionId).WaitAsync(ct); }
                    else { throw new ArgumentException("Unknown browser action."); }
                    break;
                case "button": await brain.Get<IButton>(input.Name).Click().WaitAsync(ct); break;
                case "textfield": await brain.Get<ITextField>(input.Name).Input(input.Value ?? "").WaitAsync(ct); break;
                case "select": await brain.Get<ISelect>(input.Name).Choose(input.Value ?? "").WaitAsync(ct); break;
                case "fileinput": await brain.Get<IFileInput>(input.Name).Capture(input.Field ?? "", input.Value ?? "").WaitAsync(ct); break;
                case "tabs": await brain.Get<ITabs>(input.Name).Select(input.Value ?? "").WaitAsync(ct); break;
                case "collection":
                    var collection = brain.Get<ICollectionView>(input.Name);
                    if (input.Action == "select") { await collection.Select(input.Value ?? "", input.Revision).WaitAsync(ct); }
                    else { await collection.Activate(input.Value ?? "", input.Revision).WaitAsync(ct); }
                    break;
                case "form":
                    var form = brain.Get<IForm>(input.Name);
                    if (input.Action == "submit")
                    {
                        var state = await form.Read().WaitAsync(ct);
                        var values = state.Fields.Select(field => new FormFieldValue(field.Name, field.Value)).ToArray();
                        await form.Submit(new(values, (int)input.Revision)).WaitAsync(ct);
                    }
                    else if (input.Action == "secret")
                    {
                        if (!SecretReferences.IsReference(input.Value))
                        {
                            throw new ArgumentException("A form secret carries the vault reference, never a raw secret value.");
                        }

                        await form.SetSecret(input.Field ?? "", new FormSecret(input.Value!, input.Field ?? "", true)).WaitAsync(ct);
                    }
                    else { await form.SetDraft(input.Field ?? "", input.Value ?? "").WaitAsync(ct); }
                    break;
                case UIVocabulary.VoiceInputType:
                    await brain.Get<IVoiceInput>(input.Name).Capture(DecodeAudio(input.Value), input.MimeType ?? "").WaitAsync(ct);
                    break;
                default: throw new ArgumentException("Unknown UI event.");
            }
            return Results.Ok(new { delivered = true });
        });

        UiHttp.MapGet<ITable, TableState>(endpoints, "tables", neuron => neuron.Read());
        UiHttp.MapPost(endpoints, "tables/{name}/replace", async (string name, TableReplace body, IGrainFactory grains, CancellationToken ct) =>
        {
            await grains.GetGrain<ITable>(name).Replace(body.Title, body.Columns, body.Rows).WaitAsync(ct);
            return Results.Accepted();
        });
    }

    private static bool OwnsUi(string scope, string name) =>
        new[] { "/apps/", "/images/", "/applications/" }.Any(area => name.StartsWith(scope + area, StringComparison.Ordinal));

    private static byte[] DecodeAudio(string? base64)
    {
        try { return Convert.FromBase64String(base64 ?? ""); }
        catch (FormatException) { throw new ArgumentException("Audio must be base64 encoded."); }
    }

    internal sealed record UiEvent(string Kind, string Name, string? Action = null, string? Value = null, long Revision = 0, string? Field = null, string? MimeType = null,
        string? Context = null, Guid? OperationId = null, ChatReadInput? Read = null);
    internal sealed record ChatReadInput(long Version, string[] MessageIds);
    private sealed record BrowserConnect(int Port, string SessionId);
}
