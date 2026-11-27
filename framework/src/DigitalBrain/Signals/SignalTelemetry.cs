using System.Diagnostics;
using DigitalBrain.Kernel;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Signals;

/// <summary>
/// First-class OpenTelemetry tracing and structured logging for DigitalBrain signal publication and reaction delivery.
/// Emits safe metadata tags only (<c>signal.alias</c>, <c>signal.publisher</c>, <c>signal.subscriber</c>,
/// <c>signal.sequence</c>, <c>signal.causation_id</c>) and never tags or logs signal payloads or PII.
/// </summary>
public static partial class SignalTelemetry
{
    /// <summary>The OpenTelemetry <see cref="ActivitySource"/> name for signal spans.</summary>
    public const string SourceName = "DigitalBrain.Signals";

    /// <summary>Span name emitted when a signal is published to a brain journal or ephemeral fanout.</summary>
    public const string PublishSpanName = "signal.publish";

    /// <summary>Span name emitted when a subscriber neuron reacts to a signal.</summary>
    public const string ReactSpanName = "signal.react";

    /// <summary>Tag carrying the signal's <c>[Signal]</c> alias (or CLR type name).</summary>
    public const string AliasTag = "signal.alias";

    /// <summary>Tag carrying the publishing <see cref="NeuronId"/>.</summary>
    public const string PublisherTag = "signal.publisher";

    /// <summary>Tag carrying the subscribing <see cref="NeuronId"/>.</summary>
    public const string SubscriberTag = "signal.subscriber";

    /// <summary>Tag carrying the publisher-scoped signal sequence number.</summary>
    public const string SequenceTag = "signal.sequence";

    /// <summary>Tag carrying the causation address linking a reaction and its resulting signals.</summary>
    public const string CausationIdTag = "signal.causation_id";

    /// <summary>The dedicated <see cref="ActivitySource"/> for DigitalBrain signal telemetry.</summary>
    public static readonly ActivitySource Source = new(SourceName);

    internal static Activity? StartPublish(
        NeuronId publisher,
        string alias,
        long sequence,
        string? causationId,
        ILogger? logger = null)
    {
        var logEnabled = logger is not null && logger.IsEnabled(LogLevel.Debug);
        var activity = Source.StartActivity(PublishSpanName, ActivityKind.Producer);
        if (activity is null && !logEnabled)
        {
            return null;
        }

        var publisherText = publisher.ToString();
        if (logEnabled)
        {
            LogSignalPublished(logger!, alias, publisherText, sequence, causationId ?? string.Empty);
        }

        if (activity is not null)
        {
            activity.SetTag(AliasTag, alias);
            activity.SetTag(PublisherTag, publisherText);
            activity.SetTag(SequenceTag, sequence);
            if (!string.IsNullOrEmpty(causationId))
            {
                activity.SetTag(CausationIdTag, causationId);
            }
        }
        return activity;
    }

    internal static Activity? StartReact(
        NeuronId subscriber,
        JournaledSignal signal,
        ILogger? logger = null)
    {
        var logEnabled = logger is not null && logger.IsEnabled(LogLevel.Debug);
        var activity = Source.StartActivity(ReactSpanName, ActivityKind.Consumer);
        if (activity is null && !logEnabled)
        {
            return null;
        }

        var alias = SignalAliases.Of(signal.Signal);
        var publisherText = signal.Publisher.ToString();
        var subscriberText = subscriber.ToString();
        var causationId = Causation.Address(signal.Publisher, signal.Sequence);

        if (logEnabled)
        {
            LogSignalReacted(logger!, alias, publisherText, subscriberText, signal.Sequence, causationId);
        }

        if (activity is not null)
        {
            activity.SetTag(AliasTag, alias);
            activity.SetTag(PublisherTag, publisherText);
            activity.SetTag(SubscriberTag, subscriberText);
            activity.SetTag(SequenceTag, signal.Sequence);
            activity.SetTag(CausationIdTag, causationId);
        }
        return activity;
    }

    internal static void RecordReactionFailure(
        Activity? activity,
        NeuronId subscriber,
        JournaledSignal signal,
        Exception error,
        ILogger? logger = null)
    {
        var errorType = error.GetType().Name;
        activity?.SetStatus(ActivityStatusCode.Error, errorType);
        if (logger is not null && logger.IsEnabled(LogLevel.Warning))
        {
            var alias = SignalAliases.Of(signal.Signal);
            var publisherText = signal.Publisher.ToString();
            var subscriberText = subscriber.ToString();
            LogSignalReactionFailed(logger, alias, publisherText, subscriberText, signal.Sequence, errorType);
        }
    }

    internal static void LogNudge(ILogger? logger, NeuronId subscriber, long upTo)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Debug))
        {
            var subscriberText = subscriber.ToString();
            LogSubscriberNudged(logger, subscriberText, upTo);
        }
    }

    internal static void LogOffer(ILogger? logger, NeuronId subscriber, NeuronId publisher, string alias)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Debug))
        {
            var publisherText = publisher.ToString();
            var subscriberText = subscriber.ToString();
            LogEphemeralOffered(logger, alias, publisherText, subscriberText);
        }
    }

    [LoggerMessage(
        EventId = 4201,
        EventName = "SignalPublished",
        Level = LogLevel.Debug,
        Message = "Signal {SignalAlias} published by {Publisher} at sequence {Sequence} (causation {CausationId}).")]
    private static partial void LogSignalPublished(
        ILogger logger,
        string signalAlias,
        string publisher,
        long sequence,
        string causationId);

    [LoggerMessage(
        EventId = 4202,
        EventName = "SignalReacted",
        Level = LogLevel.Debug,
        Message = "Signal {SignalAlias} from {Publisher} delivering to {Subscriber} at sequence {Sequence} (causation {CausationId}).")]
    private static partial void LogSignalReacted(
        ILogger logger,
        string signalAlias,
        string publisher,
        string subscriber,
        long sequence,
        string causationId);

    [LoggerMessage(
        EventId = 4203,
        EventName = "SignalReactionFailed",
        Level = LogLevel.Warning,
        Message = "Signal {SignalAlias} from {Publisher} failed on {Subscriber} at sequence {Sequence} ({ErrorType}).")]
    private static partial void LogSignalReactionFailed(
        ILogger logger,
        string signalAlias,
        string publisher,
        string subscriber,
        long sequence,
        string errorType);

    [LoggerMessage(
        EventId = 4204,
        EventName = "SubscriberNudged",
        Level = LogLevel.Debug,
        Message = "Nudged subscriber {Subscriber} up to position {Position}.")]
    private static partial void LogSubscriberNudged(
        ILogger logger,
        string subscriber,
        long position);

    [LoggerMessage(
        EventId = 4205,
        EventName = "EphemeralSignalOffered",
        Level = LogLevel.Debug,
        Message = "Offered ephemeral signal {SignalAlias} from {Publisher} to {Subscriber}.")]
    private static partial void LogEphemeralOffered(
        ILogger logger,
        string signalAlias,
        string publisher,
        string subscriber);
}
