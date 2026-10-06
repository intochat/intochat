import 'package:flutter/material.dart';

typedef OpenUrl = Future<void> Function(Uri url);

final class ConnectorsMenu extends StatelessWidget {
  const ConnectorsMenu({
    super.key,
    required this.connectors,
    required this.onSelect,
  });
  final List<Map<String, dynamic>> connectors;
  final void Function(String connectorId) onSelect;
  @override
  Widget build(BuildContext context) => Column(
    mainAxisSize: MainAxisSize.min,
    children: [
      for (final connector in connectors)
        ListTile(
          key: Key('connector_${connector['id']}'),
          leading: const Icon(Icons.link),
          title: Text('${connector['displayName']}'),
          onTap: () => onSelect('${connector['id']}'),
        ),
    ],
  );
}
