using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Devices.Link;

// Executes relayed chat jobs against the advertised local runtime and streams NDJSON chunks back to cloud.
internal sealed class DeviceChatJobExecutor(
    CloudClient cloud,
    DeviceCapabilityReporter capabilities,
    ILogger logger)
{
    private static readonly ActivitySource Telemetry = new("IntoChat.OS.Modules.Devices");

    public async Task ExecuteAsync(DeviceChatJob job, CancellationToken ct)
    {
        using var activity = StartJobActivity(job);

        if (!capabilities.TryGetAdvertisedModel(job.Model, out var target))
        {
            logger.LogInformation("Refused device job {JobId} for model {Model}: not-ready.", job.JobId, job.Model);
            using var refused = new NdjsonPushContent<DeviceChunk>(
                DeviceWireJson.Default.DeviceChunk,
                async (write, token) =>
                {
                    await write(new DeviceChunk(Delta: null, Done: true, Error: "not-ready"), token).ConfigureAwait(false);
                });
            await cloud.StreamAsync(job.JobId, refused, ct).ConfigureAwait(false);
            return;
        }

        var messages = new List<ChatMessage>(job.Messages.Length);
        foreach (var msg in job.Messages)
        {
            var role = msg.Role.ToLowerInvariant() switch
            {
                "system" => ChatRole.System,
                "assistant" => ChatRole.Assistant,
                _ => ChatRole.User,
            };
            messages.Add(new ChatMessage(role, msg.Text));
        }

        var chatOptions = new ChatOptions
        {
            ModelId = target.Card.Id.Value,
            Temperature = job.Temperature,
            MaxOutputTokens = job.MaxOutputTokens,
            Tools = null,
            ToolMode = ChatToolMode.None,
        };

        int? inputTokens = null;
        int? outputTokens = null;

        using var client = target.Runtime.Chat(target.Card.Id);
        using var content = new NdjsonPushContent<DeviceChunk>(
            DeviceWireJson.Default.DeviceChunk,
            async (write, token) =>
            {
                try
                {
                    await foreach (var update in client.GetStreamingResponseAsync(messages, chatOptions, token).ConfigureAwait(false))
                    {
                        foreach (var usage in update.Contents.OfType<UsageContent>())
                        {
                            if (usage.Details.InputTokenCount is { } input)
                            {
                                inputTokens = (inputTokens ?? 0) + (int)Math.Min(input, int.MaxValue);
                            }
                            if (usage.Details.OutputTokenCount is { } output)
                            {
                                outputTokens = (outputTokens ?? 0) + (int)Math.Min(output, int.MaxValue);
                            }
                        }

                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            await write(new DeviceChunk(update.Text), token).ConfigureAwait(false);
                        }
                    }

                    await write(new DeviceChunk(null, Done: true, Error: null, InputTokens: inputTokens, OutputTokens: outputTokens), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    logger.LogWarning("Device job {JobId} on model {Model} failed with {ErrorType}.", job.JobId, job.Model, error.GetType().Name);
                    activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
                    await write(new DeviceChunk(null, Done: true, Error: error.Message, InputTokens: inputTokens, OutputTokens: outputTokens), token).ConfigureAwait(false);
                }
            });

        await cloud.StreamAsync(job.JobId, content, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Completed device job {JobId} on model {Model} (input_tokens={InputTokens}, output_tokens={OutputTokens}).",
            job.JobId,
            job.Model,
            inputTokens ?? 0,
            outputTokens ?? 0);
    }

    private static Activity? StartJobActivity(DeviceChatJob job)
    {
        Activity? activity;
        if (!string.IsNullOrWhiteSpace(job.TraceParent)
            && ActivityContext.TryParse(job.TraceParent, traceState: null, isRemote: true, out var parentContext))
        {
            activity = Telemetry.StartActivity("devices.worker.job", ActivityKind.Consumer, parentContext);
            if (activity is null)
            {
                activity = new Activity("devices.worker.job");
                activity.SetParentId(parentContext.TraceId, parentContext.SpanId, parentContext.TraceFlags);
                activity.Start();
            }
        }
        else
        {
            activity = Telemetry.StartActivity("devices.worker.job", ActivityKind.Consumer);
        }

        activity?.SetTag("job.id", job.JobId);
        activity?.SetTag("gen_ai.request.model", job.Model);
        return activity;
    }
}
