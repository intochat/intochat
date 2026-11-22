import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../primitives/toast.dart';
import '../theme/wire_node_values.dart';
import '../voice_mode/audio_out.dart';
import '../voice_mode/mic_stream.dart';
import '../voice_mode/talk_socket.dart';
import '../widgets/neuron/neuron_widget.dart' show NeuronMethodCaller;

/// Watches one signal type on a neuron contract, yielding decoded JSON payloads.
typedef NeuronSignalWatcher = Stream<Map<String, dynamic>> Function(
  String contract,
  String key,
  String signal,
);

/// Reads one node of the brain: `GET /apps/node?kind=â€¦&name=â€¦`, or for a
/// `slot` (named `{brain}/apps/{app}/slots/{path}`) its app's key path
/// `GET /apps/{app}/slots/{path}` (see `appNodePath` in slot_route.dart).
typedef NeuronLoader = Future<Map<String, dynamic>> Function(
  String kind,
  String name,
);

/// Posts one UI event (`POST /apps/event`) and returns the response body, if any.
typedef NeuronAction = Future<Object?> Function(Map<String, dynamic> event);

/// Transcribes recorded audio bytes and returns the recognized text.
typedef VoiceTranscriber = Future<String> Function(
  Uint8List audio,
  String fileName,
);

/// Opens a full-duplex voice-mode WebSocket session for [conversation].
typedef VoiceTalker = Future<TalkSocket> Function(String conversation);

/// Synthesizes [text] into playable audio bytes (WAV) for read-aloud.
typedef VoiceSpeaker = Future<Uint8List> Function(String text);

/// One ordered event channel for a rendered view. An activation is sent only
/// after the field edits before it, regardless of network latency or focus.
class NeuronEventQueue extends ChangeNotifier {
  Future<void> _tail = Future.value();
  int version = 0, pending = 0;
  bool _disposed = false;
  Future<void> flush() => _tail;
  Future<T> dispatch<T>(
    Future<T> Function(Map<String, dynamic>) send,
    Map<String, dynamic> event,
  ) {
    version++;
    pending++;
    final operation = _tail.then((_) => send(event));
    _tail = operation.then<void>((_) {}).catchError((Object _) {});
    return operation.whenComplete(() {
      pending--;
      version++;
      if (!_disposed && pending == 0) notifyListeners();
    });
  }

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }
}

/// What happened when [NodeContext.activateReporting] posted a view activation.
enum ActivationOutcome { accepted, stale, refused }

/// What the renderer needs from its host to draw one published view.
@immutable
class NodeContext {
  const NodeContext({
    required this.view,
    this.generation = 0,
    this.send,
    this.load,
    this.reload,
    this.watch,
    this.watchSignal,
    this.callNeuron,
    this.onActivate,
    this.knowsApp,
    this.reference,
    this.events,
    this.confirmation = false,
    this.enabled = true,
    this.depth = 0,
    this.slotDepth = 0,
    this.slotAncestors = const {},
    this.actNeuron,
    this.slotPath = '',
    this.buttonHints = const {},
    this.nodeKeys = const {},
    this.transcribe,
    this.talk,
    this.speak,
    this.createAudioOut,
    this.createMicStream,
    this.slotProps = const {},
    this.actsViaAct = false,
  });

  /// The view neuron's name, which every activation names.
  final String view;

  /// The generation of the view as read; the server refuses an activation of a stale one.
  final int generation;

  final NeuronAction? send;
  final NeuronLoader? load;

  /// Forces the owning host to reload its published view and update [generation].
  final Future<void> Function()? reload;

  /// The node's change events (`FieldChanged` for a field).
  final Stream<void> Function(String kind, String name)? watch;

  /// Watches one signal type on a neuron contract, yielding decoded payloads.
  final NeuronSignalWatcher? watchSignal;

  /// Invokes a typed method on a neuron contract over `NeuronChannel` (gRPC).
  final NeuronMethodCaller? callNeuron;

  /// The shell's activation handler: an `os_uri` opens a window or the settings.
  final ValueChanged<Map<String, dynamic>>? onActivate;

  /// The app keys the shell knows; an `OpenWindow` intent for any other is refused.
  final bool Function(String app)? knowsApp;

  /// Hosts a referenced neuron surface (`neuronRef`) in place with the slot's
  /// [props]; null for a kind the host cannot show.
  final Widget? Function(String kind, String name, Map<String, dynamic> props)?
  reference;

  final NeuronEventQueue? events;

  /// True on the OS confirmation surface, the only place an approval card resolves.
  final bool confirmation;
  final bool enabled;
  final int depth;

  /// Current nested `NeuronRef` (`Slot`) viewport composition depth (capped at 4).
  final int slotDepth;

  /// Ancestor `neuronUri` / resolved view names along the active `Slot` chain for cycle detection.
  final Set<String> slotAncestors;

  /// The top-level neuron an [act] names; null for a view that is its own
  /// top level (derived from [view]). A slot's context carries its root's.
  final String? actNeuron;

  /// The key path from [actNeuron] to this view (`""` at the top level,
  /// `a/b` in a slot nested in slot `a`). The parent owns its children, so
  /// every act goes to the root and the path routes it hop by hop.
  final String slotPath;

  /// Optional per-button tooltip / `.Guard(rule)` disabled reasons extracted from view metadata.
  final Map<String, String> buttonHints;

  /// Optional activation `key` strings extracted from raw view JSON for nodes
  /// such as `chart` or `dataTable` (indexed by `'chart:<title>'` / `'dataTable:<title>'`).
  final Map<String, String> nodeKeys;

  /// Transcribes recorded voice drafts into text without submitting them.
  final VoiceTranscriber? transcribe;

  /// Opens a full-duplex voice-mode session for a conversation id.
  final VoiceTalker? talk;

  /// Synthesizes assistant replies into playable audio bytes for read-aloud.
  final VoiceSpeaker? speak;

  /// Optional factory for [AudioOut] (defaults to [SoLoudAudioOut]).
  final AudioOut Function()? createAudioOut;

  /// Optional factory for [MicStream] (defaults to [RecordMicStream]).
  final MicStream Function()? createMicStream;

  /// The props the parent wrote on this view's slot (`neuronRef.props`): a
  /// control draws its label, header, content and enabled from them, and its
  /// value from its own view. Empty outside a slot, and cleared by [deeper]:
  /// only the slot view's root reads them.
  final Map<String, dynamic> slotProps;

  /// True when the server marked this view's commands for the act route
  /// (`acts: "act"` on a screen's view): every command, a top-level button's
  /// included, goes to `POST /brains/{brain}/act`; otherwise a top-level
  /// button activates by key.
  final bool actsViaAct;

  bool get interactive => enabled && send != null;

  NodeContext deeper() => NodeContext(
    view: view,
    generation: generation,
    send: send,
    load: load,
    reload: reload,
    watch: watch,
    watchSignal: watchSignal,
    callNeuron: callNeuron,
    onActivate: onActivate,
    knowsApp: knowsApp,
    reference: reference,
    events: events,
    confirmation: confirmation,
    enabled: enabled,
    depth: depth + 1,
    slotDepth: slotDepth,
    slotAncestors: slotAncestors,
    actNeuron: actNeuron,
    slotPath: slotPath,
    buttonHints: buttonHints,
    nodeKeys: nodeKeys,
    transcribe: transcribe,
    talk: talk,
    speak: speak,
    createAudioOut: createAudioOut,
    createMicStream: createMicStream,
    actsViaAct: actsViaAct,
    // Props belong to the slot's root control only; nested nodes (an
    // expander's content) never read them.
  );

  /// The brain scope the view lives under: `{scope}/apps/{app}/â€¦` or `{scope}/{alias}`.
  String get scope {
    final apps = view.indexOf('/apps/');
    if (apps > 0) return view.substring(0, apps);
    final last = view.lastIndexOf('/');
    return last > 0 ? view.substring(0, last) : view;
  }

  /// The full field neuron name for a field node's `name` (`apps/{app}/{field}`).
  String fieldName(String name) =>
      name.startsWith('$scope/') ? name : '$scope/$name';

  Future<Object?> _send(Map<String, dynamic> event) {
    final send = this.send;
    if (send == null) return Future.value();
    final queue = events;
    return queue == null ? send(event) : queue.dispatch(send, event);
  }

  /// Activates the keyed node of this view: `{kind:"view", name, generation, key, value?}`.
  /// A returned client-local intent is performed through [IntentGate].
  Future<void> activate(
    String key, {
    String? value,
    BuildContext? context,
  }) async {
    await activateReporting(key, value: value, context: context);
  }

  /// Activates the keyed node of this view and reports whether the server
  /// accepted it, refused it as stale (`409` with `status: "stale"`), or refused it.
  Future<ActivationOutcome> activateReporting(
    String key, {
    String? value,
    BuildContext? context,
  }) async {
    if (!interactive) return ActivationOutcome.refused;
    final Object? response;
    try {
      response = await _send({
        'kind': 'view',
        'name': view,
        'generation': generation,
        'key': key,
        'value': ?value,
      });
    } on UiRequestException catch (error) {
      if (isStaleConflictError(error.statusCode, error.status ?? '')) {
        return ActivationOutcome.stale;
      }
      return ActivationOutcome.refused;
    } catch (_) {
      // The host has shown the refusal and reads the view again.
      return ActivationOutcome.refused;
    }
    if (response is Map && response['intent'] is Map) {
      final intent = Map<String, dynamic>.from(response['intent'] as Map);
      // An intent kind this client does not know is refused, never guessed.
      final kind = intentKindOf(intent['intentKind'] ?? intent['kind']);
      if (kind != null) {
        await perform(
          IntentNode(intentKind: kind, arg: intent['arg'] as String?),
          context: context != null && context.mounted ? context : null,
        );
      }
    }
    return ActivationOutcome.accepted;
  }

  /// The top-level neuron this view belongs to: [actNeuron], or the view
  /// name without `/view`.
  String get neuron =>
      actNeuron ??
      (view.endsWith('/view') ? view.substring(0, view.length - 5) : view);

  /// The act target for [address] relative to this view (`.` itself, `key`
  /// or `./key` a slot it shows), as a key path from [neuron].
  String actTarget(String address) {
    if (slotPath.isEmpty) return address;
    if (const {'.', './'}.contains(address)) return slotPath;
    final key = address.startsWith('./') ? address.substring(2) : address;
    return '$slotPath/$key';
  }

  /// Whether [address] names this view's own neuron on a top-level view
  /// (not a slot), where activating a node's key reaches it for any app kind.
  bool isTopLevelSelf(String address) =>
      slotPath.isEmpty && const {'.', './'}.contains(address);

  /// Acts on a neuron [address] relative to this view:
  /// `{kind:"act", neuron, target, signal, args}`, posted to
  /// `POST /brains/{brain}/act`. A refusal (`409` with its reason) or a
  /// handler failure (`422`) is shown by the host, which reads the view
  /// again; the outcome reports it to the caller.
  Future<ActivationOutcome> act(
    String address,
    String signal,
    Map<String, Object?> args, {
    BuildContext? context,
  }) async {
    if (!interactive) return ActivationOutcome.refused;
    try {
      await _send({
        'kind': 'act',
        'neuron': neuron,
        'target': actTarget(address),
        'signal': signal,
        'args': args,
      });
    } on UiRequestException catch (error) {
      return isStaleConflictError(error.statusCode, error.status ?? '')
          ? ActivationOutcome.stale
          : ActivationOutcome.refused;
    } on StateError {
      return ActivationOutcome.refused;
    }
    return ActivationOutcome.accepted;
  }

  /// Resolves a confirmation plan: `{kind:"confirmation", name, planId, approve}`
  /// (AppUiEndpoints.UiEvent).
  Future<void> resolve(String planId, {required bool approve}) async {
    if (!interactive) return;
    await _send({
      'kind': 'confirmation',
      'name': confirmation ? view : '$scope/confirmations',
      'planId': planId,
      'approve': approve,
    });
  }

  /// Posts a field's typed value to its field neuron.
  Future<void> input(String field, String value) async {
    if (!interactive) return;
    await _send({'kind': 'field', 'name': fieldName(field), 'value': value});
  }

  /// Performs a client-local intent if the gate allows it; returns whether it ran.
  Future<bool> perform(IntentNode intent, {BuildContext? context}) async {
    final action = IntentGate.check(intent, knowsApp: knowsApp);
    if (action == null) return false;
    if (intent.intentKind == IntentKind.copy) {
      final text = intent.arg ?? '';
      try {
        await Clipboard.setData(ClipboardData(text: text));
        if (context != null && context.mounted) {
          showKitToast(
            context,
            Uri.tryParse(text)?.hasScheme == true ? 'Link copied.' : 'Copied.',
          );
        }
        return true;
      } catch (_) {
        if (context != null && context.mounted) {
          showKitToast(context, 'Could not copy.', tone: UiTone.error);
        }
        return false;
      }
    }
    onActivate?.call(action);
    return onActivate != null;
  }

  /// The intent kind a response names; null for one this client does not know.
  static IntentKind? intentKindOf(Object? value) => parseIntentKind(value);
}

/// The client's gate on server-named intents: copy text, open a window of an
/// app the shell knows (`os://window/open?app=â€¦`), or open the settings.
/// Never an external URL.
abstract final class IntentGate {
  /// The activation the shell performs, or null when the intent is refused.
  static Map<String, dynamic>? check(
    IntentNode intent, {
    bool Function(String app)? knowsApp,
  }) {
    switch (intent.intentKind) {
      case IntentKind.copy:
        return {'kind': 'copy', 'text': intent.arg ?? ''};
      case IntentKind.openSettings:
        final page = intent.arg ?? '';
        if (page.isNotEmpty && !isValidIntentAppKey(page)) return null;
        return {
          'kind': 'os_uri',
          'uri': Uri(
            scheme: 'os',
            host: 'window',
            path: '/open',
            queryParameters: {
              'app': 'settings',
              'section': ?(page.isEmpty ? null : page),
            },
          ).toString(),
          'host': 'window',
        };
      case IntentKind.openWindow:
        final uri = Uri.tryParse(intent.arg ?? '');
        if (!isAllowedOpenWindowUri(uri)) {
          return null;
        }
        final app = uri!.queryParameters['app'] ?? '';
        // Without the shell's known-app set nothing opens.
        if (!isValidIntentAppKey(app) || !(knowsApp?.call(app) ?? false)) {
          return null;
        }
        // Rebuilt from the allowed keys only: the app, and an opaque input the shell hands to the app's open
        // operation as text (never a URL it follows).
        final input = uri.queryParameters['input'];
        final rebuilt = Uri(
          scheme: 'os',
          host: 'window',
          path: '/open',
          queryParameters: {'app': app, 'input': ?input},
        );
        return {'kind': 'os_uri', 'uri': rebuilt.toString(), 'host': 'window'};
    }
  }
}

/// A slotted control's accessible name: its props' `automationName`, else its
/// label or header, else its slot key.
String controlNameOf(String? automationName, String label, String key) =>
    automationName != null && automationName.isNotEmpty
    ? automationName
    : label.isNotEmpty
    ? label
    : key;
