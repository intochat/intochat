import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/kit_toast.dart';
import '../theme/wire_node_values.dart';

/// Reads one node of the brain: `GET /apps/node?kind=…&name=…`.
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
    this.onActivate,
    this.knowsApp,
    this.reference,
    this.events,
    this.confirmation = false,
    this.enabled = true,
    this.depth = 0,
    this.slotDepth = 0,
    this.slotAncestors = const {},
    this.buttonHints = const {},
    this.nodeKeys = const {},
    this.transcribe,
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

  /// The shell's activation handler: an `os_uri` opens a window or the settings.
  final ValueChanged<Map<String, dynamic>>? onActivate;

  /// The app keys the shell knows; an `OpenWindow` intent for any other is refused.
  final bool Function(String app)? knowsApp;

  /// Hosts a referenced neuron surface (`neuronRef`) in place; null for a kind the host cannot show.
  final Widget? Function(String kind, String name)? reference;

  final NeuronEventQueue? events;

  /// True on the OS confirmation surface, the only place an approval card resolves.
  final bool confirmation;
  final bool enabled;
  final int depth;

  /// Current nested `NeuronRef` (`Slot`) viewport composition depth (capped at 4).
  final int slotDepth;

  /// Ancestor `neuronUri` / resolved view names along the active `Slot` chain for cycle detection.
  final Set<String> slotAncestors;

  /// Optional per-button tooltip / `.Guard(rule)` disabled reasons extracted from view metadata.
  final Map<String, String> buttonHints;

  /// Optional activation `key` strings extracted from raw view JSON for nodes
  /// such as `chart` or `dataTable` (indexed by `'chart:<title>'` / `'dataTable:<title>'`).
  final Map<String, String> nodeKeys;

  /// Transcribes recorded voice drafts into text without submitting them.
  final VoiceTranscriber? transcribe;

  bool get interactive => enabled && send != null;

  NodeContext deeper() => NodeContext(
    view: view,
    generation: generation,
    send: send,
    load: load,
    reload: reload,
    watch: watch,
    onActivate: onActivate,
    knowsApp: knowsApp,
    reference: reference,
    events: events,
    confirmation: confirmation,
    enabled: enabled,
    depth: depth + 1,
    slotDepth: slotDepth,
    slotAncestors: slotAncestors,
    buttonHints: buttonHints,
    nodeKeys: nodeKeys,
    transcribe: transcribe,
  );

  /// The brain scope the view lives under: `{scope}/apps/{app}/…` or `{scope}/{alias}`.
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
    Map<String, Object?>? event,
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
        'event': ?event,
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

  /// Emits a control event: activates [key] with the typed payload under
  /// `event`. A control without [emit] sends nothing.
  Future<void> emitEvent(
    String key,
    Emit? emit,
    Map<String, Object?> event, {
    BuildContext? context,
  }) async {
    if (emit == null) return;
    await activateReporting(key, event: event, context: context);
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
        return {'kind': 'os_uri', 'uri': rebuilt.toString(), 'host': 'window'};
    }
  }
}
