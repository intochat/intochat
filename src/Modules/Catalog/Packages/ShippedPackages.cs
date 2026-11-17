using IntoChat.Sdk;

namespace IntoChat.OS.Modules.Apps;

/// <summary>Built-in shareable <see cref="AppPackage"/> automations shipped in the Marketplace.</summary>
internal static class ShippedPackages
{
    public const string Publisher = "into-chat";

    public static IReadOnlyList<AppPackage> All { get; } =
    [
        CreateEmailNotifierPackage(),
        CreateDownloadsInvoicesPackage(),
    ];

    public static AppPackage? Find(string key)
        => All.FirstOrDefault(pkg => string.Equals(pkg.Key, key, StringComparison.OrdinalIgnoreCase));

    private static AppPackage CreateEmailNotifierPackage()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DigitalBrain.UI;
            using IntoChat.Sdk;

            namespace IntoChat.Shipped;

            internal sealed record MailAlertState
            {
                public List<string> RecentSubjects { get; init; } = [];
                public int TotalNotified { get; init; }
            }

            internal sealed record MailAlertSettings
            {
                public bool IgnoreNoReply { get; init; } = true;
                public string RequiredKeyword { get; init; } = "";
            }

            internal sealed record IncomingEmailPayload(string From, string Subject, string Preview = "");

            [App("email-notifier",
                Title = "Email Notifier",
                Summary = "Tracks incoming email signals and raises OS notifications for matching messages.",
                Version = "1.0.0",
                Icon = "mail",
                Category = "Communication",
                Author = "into-chat")]
            internal sealed class EmailNotifierApp : BrainApp<MailAlertState, MailAlertSettings>
            {
                [OnSignal("mail.received")]
                public async Task OnMailReceived(IncomingEmailPayload email)
                {
                    if (Settings.IgnoreNoReply && email.From.Contains("noreply", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(Settings.RequiredKeyword)
                        && !email.Subject.Contains(Settings.RequiredKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    await Brain.Notifications.SendAsync(new NotificationRequest(
                        Title: $"New email: {email.Subject}",
                        Body: $"From {email.From}: {email.Preview}",
                        Priority: NotificationPriority.High,
                        Category: "email",
                        DedupeKey: $"mail:{email.From}:{email.Subject}"));

                    var updated = new List<string>(State.RecentSubjects) { $"{email.From}: {email.Subject}" };
                    if (updated.Count > 25)
                    {
                        updated.RemoveAt(0);
                    }

                    State = State with
                    {
                        RecentSubjects = updated,
                        TotalNotified = State.TotalNotified + 1,
                    };
                }

                [OnCommand("test-alert", Description = "Send a test email notification")]
                public Task TestAlert()
                    => OnMailReceived(new IncomingEmailPayload("alex@example.com", "Q4 Invoice Approved", "Your quarterly invoice was approved."));

                public override View Render()
                    => new("Email Notifier", new Column(
                    [
                        new Text($"Notified messages: {State.TotalNotified}"),
                        new Button("test-alert", "Send Test Alert", Emit: new Emit("app.email-notifier", "test-alert")),
                    ]));
            }
            """;

        AppSourceFile[] files = [new AppSourceFile("EmailNotifierApp.cs", code)];
        var needs = new AppPackageNeeds(
            [],
            ["mail.received"],
            [],
            [new AppCommandSpec("test-alert", "Send a test email notification")]);
        return new AppPackage(
            "email-notifier",
            "1.0.0",
            "Email Notifier",
            "Tracks incoming email signals and raises OS notifications for matching messages.",
            "mail",
            "Communication",
            Publisher,
            files,
            needs,
            "{}",
            AppPackage.ComputeDigest("email-notifier", "1.0.0", files));
    }

    private static AppPackage CreateDownloadsInvoicesPackage()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.RegularExpressions;
            using System.Threading.Tasks;
            using DigitalBrain.UI;
            using IntoChat.Sdk;

            namespace IntoChat.Shipped;

            internal sealed record InvoiceAppState
            {
                public int ProcessedCount { get; init; }
                public List<string> RecentFiles { get; init; } = [];
            }

            internal sealed record InvoiceAppSettings
            {
                public string ProcessedSubfolder { get; init; } = "Processed";
            }

            [App("downloads-invoices",
                Title = "Downloads Invoices",
                Summary = "Watches your Windows Downloads folder, logs invoices to a durable table, archives them, and notifies you.",
                Version = "1.0.0",
                Icon = "receipt_long",
                Category = "Finance",
                Author = "into-chat")]
            [AppSlot("downloads", typeof(IFolder),
                Required = true,
                Access = FolderAccess.ReadWrite,
                DefaultPath = "%USERPROFILE%/Downloads",
                Description = "Host Downloads folder mediated by the Windows connector")]
            internal sealed class DownloadsInvoicesApp : BrainApp<InvoiceAppState, InvoiceAppSettings>
            {
                [OnSignal("windows.file-changed")]
                public async Task OnFileChanged(FileChanged change)
                {
                    if (!string.Equals(change.Slot, "downloads", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(change.Kind, "Deleted", StringComparison.OrdinalIgnoreCase)
                        || change.RelativePath.StartsWith(Settings.ProcessedSubfolder + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (!change.RelativePath.Contains("invoice", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var folder = Brain.Folder("downloads");
                    if (!await folder.ExistsAsync(change.RelativePath))
                    {
                        return;
                    }

                    var content = await folder.ReadTextAsync(change.RelativePath);
                    var amountMatch = Regex.Match(content, @"\$\s*([0-9]+(?:\.[0-9]{2})?)");
                    if (!amountMatch.Success)
                    {
                        amountMatch = Regex.Match(content, @"\b([0-9]+\.[0-9]{2})\b");
                    }
                    if (!amountMatch.Success)
                    {
                        amountMatch = Regex.Match(content, @"\b([0-9]+)\b");
                    }
                    var amount = amountMatch.Success ? amountMatch.Groups[1].Value : "0.00";
                    var recordJson = JsonSerializer.Serialize(new
                    {
                        file = change.RelativePath,
                        amount,
                        detectedUtc = DateTimeOffset.UtcNow,
                    });

                    var table = Brain.Tables.Table("invoices");
                    await table.EnsureAsync(
                    [
                        new TableColumnSpec("file", "text"),
                        new TableColumnSpec("amount", "text"),
                    ]);
                    await table.UpsertAsync(change.RelativePath, recordJson);

                    var fileName = change.RelativePath.Contains('/')
                        ? change.RelativePath[(change.RelativePath.LastIndexOf('/') + 1)..]
                        : change.RelativePath;
                    await folder.MoveAsync(change.RelativePath, $"{Settings.ProcessedSubfolder}/{fileName}");

                    await Brain.Notifications.SendAsync(new NotificationRequest(
                        Title: $"Invoice processed: {fileName}",
                        Body: $"Amount {amount} archived to {Settings.ProcessedSubfolder}/{fileName}",
                        Priority: NotificationPriority.Normal,
                        Category: "invoices",
                        DedupeKey: $"invoice:{change.RelativePath}"));

                    var recent = new List<string>(State.RecentFiles) { $"{fileName} ({amount})" };
                    if (recent.Count > 25)
                    {
                        recent.RemoveAt(0);
                    }

                    State = State with
                    {
                        ProcessedCount = State.ProcessedCount + 1,
                        RecentFiles = recent,
                    };
                }

                [OnCommand("scan-now", Description = "Scan the bound Downloads folder for pending invoices")]
                public async Task ScanNow()
                {
                    var entries = await Brain.Folder("downloads").ListAsync();
                    foreach (var entry in entries)
                    {
                        if (!entry.IsDirectory && entry.Name.Contains("invoice", StringComparison.OrdinalIgnoreCase))
                        {
                            await OnFileChanged(new FileChanged("downloads", "Created", entry.RelativePath, entry.SizeBytes, entry.ModifiedUtc));
                        }
                    }
                }

                public override View Render()
                    => new("Downloads Invoices", new Column(
                    [
                        new Text($"Invoices processed: {State.ProcessedCount}"),
                        new Button("scan-now", "Scan Downloads Now", Emit: new Emit("app.downloads-invoices", "scan-now")),
                    ]));
            }
            """;

        AppSourceFile[] files = [new AppSourceFile("DownloadsInvoicesApp.cs", code)];
        var needs = new AppPackageNeeds(
            [new AppSlotSpec("downloads", typeof(IFolder).FullName!, true, "Host Downloads folder mediated by the Windows connector", FolderAccess.ReadWrite, "%USERPROFILE%/Downloads")],
            ["windows.file-changed"],
            [],
            [new AppCommandSpec("scan-now", "Scan the bound Downloads folder for pending invoices")]);
        return new AppPackage(
            "downloads-invoices",
            "1.0.0",
            "Downloads Invoices",
            "Watches your Windows Downloads folder, logs invoices to a durable table, archives them, and notifies you.",
            "receipt_long",
            "Finance",
            Publisher,
            files,
            needs,
            "{}",
            AppPackage.ComputeDigest("downloads-invoices", "1.0.0", files));
    }
}
