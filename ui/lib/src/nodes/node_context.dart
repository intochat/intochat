import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../primitives/toast.dart';
import '../theme/wire_node_values.dart';
import '../widgets/voice/audio_out.dart';
import '../widgets/voice/mic_stream.dart';
import '../widgets/voice/talk_socket.dart';
import '../core/neuron_method_caller.dart';

/// Watches one signal type on a neuron contract, yielding decoded JSON payloads.
typedef NeuronSignalWatcher = Stream<Map<String, dynamic>> Function(
  String contract,
  String key,
  String signal,
);

/// Reads one node of the brain: `GET /apps/node?kind=…&name=…`, or for a
/// `child` (named `{brain}/apps/{app}/children/{path}`) its app's key path
/// `GET /apps/{app}/children/{path}` (see `appNodePath` in child_route.dart).
typedef NeuronLoader = Future<UiNodePayload> Function(String kind, String name);

/// Sends one typed [UiAction] of a view (an [ActAction] on `POST /act`, a
/// [ConfirmationAction], or a [WebBrowserAction]; `IntoChatOSUiClient.uiEvent`
/// picks the route) and returns the response body, if any.
typedef NeuronAction = Future<Object?> Function(UiAction action);

/// Stores a typed secret in the signed-in person's vault
/// (`POST /secrets/{owner}`) and returns its reference (`secret://…`); the
/// value itself never enters a view action.
typedef SecretStore = Future<String> Function(
  String name,
  String label,
  String value,
);

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
  Future<T> dispatch<T>(Future<T> Function(UiAction) send, UiAction action) {
    version++;
    pending++;
    final operation = _tail.then((_) => send(action));
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

/// What happened when [NodeContext.act] posted a command.
enum ActivationOutcome { accepted, refused }

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
    this.childDepth = 0,
    this.childAncestors = const {},
    this.actNeuron,
    this.childPath = '',
    this.transcribe,
    this.talk,
    this.speak,
    this.storeSecret,
    this.createAudioOut,
    this.createMicStream,
    this.childProps = const {},
  });

  /// The view neuron's name, which every activation names.
  final String view;

  /// The generation of the view as read.
  final int generation;

  final NeuronAction? send;
  final NeuronLoader? load;

  /// Forces the owning host to reload its published view and update [generation].
  final Future<void> Function()? reload;

  /// The node's change events (`FlutterViewChanged` for a view).
  final Stream<void> Function(String kind, String name)? watch;

  /// Watches one signal type on a neuron contract, yielding decoded payloads.
  final NeuronSignalWatcher? watchSignal;

  /// Invokes a typed method on a neuron contract over `NeuronChannel` (gRPC).
  final NeuronMethodCaller? callNeuron;

  /// The shell's activation handler: an `os_uri` opens a window or the settings.
  final ValueChanged<Map<String, dynamic>>? onActivate;

  /// The app keys the shell knows; an `OpenWindow` intent for any other is refused.
  final bool Function(String app)? knowsApp;

  /// Hosts a referenced neuron surface (`neuronRef`) in place with the child's
  /// [props]; null for a kind the host cannot show.
  final Widget? Function(String kind, String name, Map<String, dynamic> props)?
  reference;

  final NeuronEventQueue? events;

  /// True on the OS confirmation surface, the only place an approval card resolves.
  final bool confirmation;
  final bool enabled;
  final int depth;

  /// Current nested `NeuronRef` viewport composition depth (capped at 4).
  final int childDepth;

  /// Ancestor `neuronUri` / resolved view names along the active `Child` chain for cycle detection.
  final Set<String> childAncestors;

  /// The top-level neuron an [act] names; null for a view that is its own
  /// top level (derived from [view]). A child's context carries its root's.
  final String? actNeuron;

  /// The key path from [actNeuron] to this view (`""` at the top level,
  /// `a/b` in a child nested in child `a`). The parent owns its children, so
  /// every act goes to the root and the path routes it hop by hop.
  final String childPath;

  /// Transcribes recorded voice drafts into text without submitting them.
  final VoiceTranscriber? transcribe;

  /// Opens a full-duplex voice-mode session for a conversation id.
  final VoiceTalker? talk;

  /// Synthesizes assistant replies into playable audio bytes for read-aloud.
  final VoiceSpeaker? speak;

  /// Stores a form's secret field in the person's vault; null where no person
  /// is signed in (a secret field is then drawn disabled).
  final SecretStore? storeSecret;

  /// Optional factory for [AudioOut] (defaults to [SoLoudAudioOut]).
  final AudioOut Function()? createAudioOut;

  /// Optional factory for [MicStream] (defaults to [RecordMicStream]).
  final MicStream Function()? createMicStream;

  /// The props the parent wrote on this view's child (`neuronRef.props`): a
  /// control draws its label, header, content and enabled from them, and its
  /// value from its own view. Empty outside a child, and cleared by [deeper]:
  /// only the child view's root reads them.
  final Map<String, dynamic> childProps;

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
    childDepth: childDepth,
    childAncestors: childAncestors,
    actNeuron: actNeuron,
    childPath: childPath,
    transcribe: transcribe,
    talk: talk,
    speak: speak,
    storeSecret: storeSecret,
    createAudioOut: createAudioOut,
    createMicStream: createMicStream,
    // Props belong to the child's root control only; nested nodes (an
    // expander's content) never read them.
  );

  /// The brain scope the view lives under: `{scope}/apps/{app}/…` or `{scope}/{alias}`.
  String get scope {
    final apps = view.indexOf('/apps/');
    if (apps > 0) return view.substring(0, apps);
    final last = view.lastIndexOf('/');
    return last > 0 ? view.substring(0, last) : view;
  }

  Future<Object?> _send(UiAction action) {
    final send = this.send;
    if (send == null) return Future.value();
    final queue = events;
    return queue == null ? send(action) : queue.dispatch(send, action);
  }

  /// The top-level neuron this view belongs to: [actNeuron], or the view
  /// name without `/view`.
  String get neuron =>
      actNeuron ??
      (view.endsWith('/view') ? view.substring(0, view.length - 5) : view);

  /// The act target for [address] relative to this view (`.` itself, `key`
  /// or `./key` a child it shows), as a key path from [neuron].
  String actTarget(String address) {
    if (childPath.isEmpty) return address;
    if (const {'.', './'}.contains(address)) return childPath;
    final key = address.startsWith('./') ? address.substring(2) : address;
    return '$childPath/$key';
  }

  /// Acts on a neuron [address] relative to this view via [ActAction], posted
  /// to `POST /brains/{brain}/act`, the one route of a person's action,
  /// whatever kind of screen drew the view (a compiled or agent app's screen, a
  /// sandboxed app's view, a unit); [args] are the command's JSON args as the
  /// node carries them, and [value] is a field's text, which the server adds
  /// to the args as `value`. A refusal (`409` with its reason) or a
  /// handler failure (`422`) is shown by the host, which reads the view
  /// again; the outcome reports it to the caller.
  Future<ActivationOutcome> act(
    String address,
    String signal,
    Object? args, {
    String? value,
    BuildContext? context,
  }) async {
    if (!interactive) return ActivationOutcome.refused;
    try {
      await _send(
        ActAction(
          neuron: neuron,
          target: actTarget(address),
          signal: signal,
          args: args,
          value: value,
        ),
      );
    } on UiRequestException {
      return ActivationOutcome.refused;
    } on StateError {
      return ActivationOutcome.refused;
    }
    return ActivationOutcome.accepted;
  }

  /// Answers a confirmation plan as the person via [ConfirmationAction], which
  /// the client posts to the brain's confirmation route
  /// (`IntoChatOSUiClient.answerConfirmation`), not as a UI event.
  Future<void> resolve(String planId, {required bool approve}) async {
    if (!interactive) return;
    await _send(ConfirmationAction(planId: planId, approve: approve));
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
/// app the shell knows (`os://window/open?app=…`), or open the settings.
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
        // The node's own argument rides along so the shell can read its
        // arrival metadata (args, transition, from) for this same app
        // (`openWindowRouteOf`); the shell never follows it as a URL.
        return {
          'kind': 'os_uri',
          'uri': rebuilt.toString(),
          'host': 'window',
          'intent': intent.arg,
        };
    }
  }
}

/// a child control's accessible name: its props' `automationName`, else its
/// label or header, else its child key.
String controlNameOf(String? automationName, String label, String key) =>
    automationName != null && automationName.isNotEmpty
    ? automationName
    : label.isNotEmpty
    ? label
    : key;
