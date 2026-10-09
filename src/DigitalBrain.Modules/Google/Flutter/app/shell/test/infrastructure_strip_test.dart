import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/infrastructure_strip.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('health is green when healthy or connected, amber while unknown, red when down', () {
    expect(infrastructureHealth('Running', 'Healthy'), InfrastructureHealth.green);
    expect(infrastructureHealth('Connected', null), InfrastructureHealth.green);
    expect(infrastructureHealth('Starting', null), InfrastructureHealth.amber);
    expect(infrastructureHealth('Running', null), InfrastructureHealth.amber);
    expect(infrastructureHealth('Unknown', null), InfrastructureHealth.amber);
    expect(infrastructureHealth('Running', 'Unhealthy'), InfrastructureHealth.red);
    expect(infrastructureHealth('Exited', null), InfrastructureHealth.red);
    expect(infrastructureHealth('FailedToStart', null), InfrastructureHealth.red);
    expect(infrastructureHealth('Disconnected', null), InfrastructureHealth.red);
    expect(infrastructureHealth('Failing', null), InfrastructureHealth.red);
  });

  testWidgets('the strip shows a chip per resource and connection, refreshes, and a tap names the module', (
    tester,
  ) async {
    var state = 'Running';
    final tapped = <String>[];
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: InfrastructureStrip(
            refreshEvery: const Duration(milliseconds: 100),
            load: () async => [
              InfrastructureRow(
                module: 'postgres',
                resource: 'Modules-postgres-server',
                state: state,
                health: state == 'Running' ? 'Healthy' : null,
              ),
            ],
            loadConnections: () async => [
              {'connectorId': 'gmail', 'connectionId': 'me', 'status': 'Connected'},
            ],
            onTap: tapped.add,
          ),
        ),
      ),
    );
    await tester.pump();

    expect(find.byKey(const ValueKey('infra-postgres-Modules-postgres-server')), findsOneWidget);
    expect(find.byKey(const ValueKey('infra-gmail-connection me')), findsOneWidget);
    CircleAvatar dot() => tester.widget(
      find.byKey(const ValueKey('infra-dot-postgres-Modules-postgres-server')),
    );
    expect(dot().backgroundColor, Colors.green);

    state = 'Exited';
    await tester.pump(const Duration(milliseconds: 150));
    await tester.pump();
    expect(dot().backgroundColor, Colors.red);

    await tester.tap(find.byKey(const ValueKey('infra-postgres-Modules-postgres-server')));
    expect(tapped, ['postgres']);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a failed load says so', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: InfrastructureStrip(
            load: () async => throw StateError('kernel unreachable'),
          ),
        ),
      ),
    );
    await tester.pump();
    expect(find.byKey(const ValueKey('infrastructure-error')), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });
}