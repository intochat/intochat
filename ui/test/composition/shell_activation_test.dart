import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  group('ShellActivation.fromWireMap / tryFromWireMap', () {
    test('normalizes legacy settings wire maps once at ingress', () {
      final withTab = ShellActivation.fromWireMap({
        'kind': 'settings',
        'brainId': 'brain-1',
        'tab': 'keys',
      });
      expect(withTab, isA<WorkspaceAppActivation>());
      final settings = withTab as WorkspaceAppActivation;
      expect(settings.brainId, 'brain-1');
      expect(settings.application, 'settings');
      expect(settings.operation, 'open');
      expect(settings.input, 'keys');
      expect(settings.hasNavigationPayload, isFalse);
      expect(settings.toWireMap(), {
        'kind': 'workspace',
        'brainId': 'brain-1',
        'application': 'settings',
        'operation': 'open',
        'input': 'keys',
      });

      final bare = ShellActivation.fromWireMap({'kind': 'settings'});
      expect(bare.toWireMap(), {
        'kind': 'workspace',
        'application': 'settings',
        'operation': 'open',
        'input': '',
      });
    });

    test('normalizes configuration view wire maps into apps detail', () {
      final parsed = ShellActivation.fromWireMap({
        'brainId': 'b',
        'application': 'notes',
        'view': 'configuration',
      });
      expect(parsed, isA<WorkspaceAppActivation>());
      expect(parsed.toWireMap(), {
        'kind': 'workspace',
        'brainId': 'b',
        'application': 'apps',
        'operation': 'open',
        'input': 'installed:notes',
      });
    });

    test('parses all sealed activation variants', () {
      expect(
        ShellActivation.fromWireMap({'kind': 'account'}),
        const AccountActivation(),
      );
      expect(
        ShellActivation.fromWireMap({'kind': 'lobby-ended'}),
        const LobbyEndedActivation(),
      );

      final reconnect = ShellActivation.fromWireMap({
        'kind': 'reconnect',
        'application': 'desktop',
        'operation': 'layout',
        'input': 'classic',
      });
      expect(reconnect, isA<ReconnectActivation>());
      expect((reconnect as ReconnectActivation).application, 'desktop');
      expect(reconnect.operation, 'layout');
      expect(reconnect.input, 'classic');

      final lifecycle = ShellActivation.fromWireMap({
        'kind': 'remove',
        'brainId': 'b-1',
        'name': 'notes',
        'confirmed': true,
      });
      expect(lifecycle, isA<AppLifecycleActivation>());
      final remove = lifecycle as AppLifecycleActivation;
      expect(remove.lifecycleKind, AppLifecycleKind.remove);
      expect(remove.name, 'notes');
      expect(remove.confirmed, isTrue);

      final connections = ShellActivation.fromWireMap({
        'kind': 'connections',
        'brainId': 'b-1',
        'connector': 'gmail',
        'connection': 'work',
      });
      expect(connections, isA<ConnectionsActivation>());
      expect((connections as ConnectionsActivation).connectorId, 'gmail');
      expect(connections.connectionId, 'work');

      final openWindow = ShellActivation.fromWireMap({
        'kind': 'os_uri',
        'uri': 'os://window/open?app=aspire&input=run-1',
        'intent': 'os://window/open?app=aspire&input=run-1&transition=none',
      });
      expect(openWindow, isA<OpenWindowActivation>());
      expect(
        (openWindow as OpenWindowActivation).uri,
        'os://window/open?app=aspire&input=run-1',
      );

      final showNeuron = ShellActivation.fromWireMap({
        'brainId': 'b-1',
        'neuron': 'b-1/apps/ino',
      });
      expect(showNeuron, isA<ShowNeuronActivation>());
      expect((showNeuron as ShowNeuronActivation).neuron, 'b-1/apps/ino');
    });

    test('returns null / throws FormatException for unsupported wire maps', () {
      expect(ShellActivation.tryFromWireMap(const {}), isNull);
      expect(ShellActivation.tryFromWireMap({'kind': 'unknown'}), isNull);
      expect(
        () => ShellActivation.fromWireMap({'kind': 'unknown'}),
        throwsFormatException,
      );
    });

    test('derives hasNavigationPayload by default and preserves toWireMap/fromWireMap round-trip fidelity', () {
      // Navigation-payload activations derive hasNavigationPayload = true
      // from non-empty input, argsJson, or rawTransition.
      const withInput = WorkspaceAppActivation(
        brainId: 'b-1',
        application: 'ino',
        operation: 'send',
        input: 'Summarize today',
      );
      expect(withInput.hasNavigationPayload, isTrue);
      final roundTrippedInput = ShellActivation.fromWireMap(
        withInput.toWireMap(),
      ) as WorkspaceAppActivation;
      expect(roundTrippedInput.hasNavigationPayload, isTrue);
      expect(roundTrippedInput.application, 'ino');
      expect(roundTrippedInput.operation, 'send');
      expect(roundTrippedInput.input, 'Summarize today');
      expect(roundTrippedInput.toWireMap(), withInput.toWireMap());

      const withArgsAndTransition = WorkspaceAppActivation(
        brainId: 'b-1',
        application: 'aspire',
        operation: 'open',
        argsJson: '{"traceId":"t-1"}',
        rawTransition: 'shared-axis-z',
        fromNeuron: 'b-1/apps/engineer',
      );
      expect(withArgsAndTransition.hasNavigationPayload, isTrue);
      final roundTrippedArgs = ShellActivation.fromWireMap(
        withArgsAndTransition.toWireMap(),
      ) as WorkspaceAppActivation;
      expect(roundTrippedArgs.hasNavigationPayload, isTrue);
      expect(roundTrippedArgs.argsJson, '{"traceId":"t-1"}');
      expect(roundTrippedArgs.rawTransition, 'shared-axis-z');
      expect(roundTrippedArgs.fromNeuron, 'b-1/apps/engineer');
      expect(roundTrippedArgs.toWireMap(), withArgsAndTransition.toWireMap());

      // Non-navigation-payload activations (omitted or whitespace-only payload)
      // derive hasNavigationPayload = false and round-trip identically.
      const bareOpen = WorkspaceAppActivation(
        brainId: 'b-1',
        application: 'notes',
        operation: 'open',
      );
      expect(bareOpen.hasNavigationPayload, isFalse);
      final roundTrippedBare = ShellActivation.fromWireMap(
        bareOpen.toWireMap(),
      ) as WorkspaceAppActivation;
      expect(roundTrippedBare.hasNavigationPayload, isFalse);
      expect(roundTrippedBare.application, 'notes');
      expect(roundTrippedBare.operation, 'open');
      expect(roundTrippedBare.toWireMap(), bareOpen.toWireMap());

      const whitespaceInput = WorkspaceAppActivation(
        application: 'ino',
        operation: 'send',
        input: '   ',
      );
      expect(whitespaceInput.hasNavigationPayload, isFalse);
      final roundTrippedWhitespace = ShellActivation.fromWireMap(
        whitespaceInput.toWireMap(),
      ) as WorkspaceAppActivation;
      expect(roundTrippedWhitespace.hasNavigationPayload, isFalse);
      expect(roundTrippedWhitespace.toWireMap(), whitespaceInput.toWireMap());

      // Explicit override (legacy settings tab) stays false even with non-empty input.
      final legacySettings = WorkspaceAppActivation.settings(
        brainId: 'b-1',
        tab: 'keys',
      );
      expect(legacySettings.hasNavigationPayload, isFalse);

      // ShowNeuronActivation derives hasNavigationPayload consistently too.
      const neuronWithInput = ShowNeuronActivation(
        brainId: 'b-1',
        neuron: 'b-1/apps/ino',
        input: 'hello',
      );
      expect(neuronWithInput.hasNavigationPayload, isTrue);
      final roundTrippedNeuron = ShellActivation.fromWireMap(
        neuronWithInput.toWireMap(),
      ) as ShowNeuronActivation;
      expect(roundTrippedNeuron.hasNavigationPayload, isTrue);
      expect(roundTrippedNeuron.toWireMap(), neuronWithInput.toWireMap());

      const bareNeuron = ShowNeuronActivation(
        brainId: 'b-1',
        neuron: 'b-1/apps/ino',
      );
      expect(bareNeuron.hasNavigationPayload, isFalse);
      final roundTrippedBareNeuron = ShellActivation.fromWireMap(
        bareNeuron.toWireMap(),
      ) as ShowNeuronActivation;
      expect(roundTrippedBareNeuron.hasNavigationPayload, isFalse);
      expect(roundTrippedBareNeuron.toWireMap(), bareNeuron.toWireMap());
    });
  });

  group('ViewHostOverrides', () {
    test('withoutWatch returns same instance when watch is already null', () {
      final overrides = ViewHostOverrides(
        knowsApp: (app) => app == 'ino',
        onActivate: (_) {},
      );
      expect(identical(overrides.withoutWatch(), overrides), isTrue);
    });

    test('withoutWatch strips watch while preserving other capabilities', () {
      bool knows(String app) => app == 'ino';
      void onActivate(ShellActivation _) {}
      final overrides = ViewHostOverrides(
        knowsApp: knows,
        onActivate: onActivate,
        watch: (_, _) => const Stream.empty(),
      );
      final stripped = overrides.withoutWatch();
      expect(stripped.watch, isNull);
      expect(stripped.knowsApp, same(knows));
      expect(stripped.onActivate, same(onActivate));
    });
  });
}
