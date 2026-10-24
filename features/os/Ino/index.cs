using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;

// Any chat index change (a first message, a back-fill, Settings' Unarchive) marks Ino's view dirty for view.cs.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await brain.React<ChatIndexChanged>(InoChats.Index(brain), _ => Ino.Dirty(brain), brain.Stopping);
