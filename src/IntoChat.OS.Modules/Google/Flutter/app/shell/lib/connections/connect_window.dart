import 'dart:async';

import 'package:flutter/material.dart';

import 'windows_storage_setup.dart';

String connectionStatus(Object? status) => switch (status) {
  'Connected' => 'Connected',
  'Expired' => 'Expired',
  'Failing' => 'Failing',
  'Disconnected' => 'Disconnected',
  _ => 'Unavailable',
};

typedef ConnectionsRequest = Future<Object?> Function(
  String path, {
  Map<String, Object?>? body,
});

class ConnectWindow extends StatefulWidget {
  const ConnectWindow({
    super.key,
    required this.request,
    this.connectorRequest,
    this.windowsRequest,
    this.initialConnectorId,
    this.onOpen,
  });

  final ConnectionsRequest request;
  final ConnectionsRequest? connectorRequest;
  final ConnectionsRequest? windowsRequest;
  final String? initialConnectorId;
  final Future<void> Function(Uri url)? onOpen;

  @override
  State<ConnectWindow> createState() => _ConnectWindowState();
}

class _ConnectWindowState extends State<ConnectWindow> {
  final _connectionId = TextEditingController();
  final _value = TextEditingController();
  List<Map<String, dynamic>> connections = [];
  List<Map<String, dynamic>> connectors = [];
  String? source;
  String? authMethod;

  List<String> get authMethods => [
    for (final row in connectors.where((c) => c['id'] == source))
      ...List<String>.from(row['authMethods'] as List? ?? []),
  ];
  bool get usesOAuth => authMethod?.contains('oauth') ?? false;
  String? error;
  String? notice;
  bool busy = false;
  bool loaded = false;
  int generation = 0;

  @override
  void initState() {
    super.initState();
    unawaited(load());
  }

  @override
  void dispose() {
    _connectionId.dispose();
    _value.dispose();
    super.dispose();
  }

  Future<void> load() async {
    final ticket = ++generation;
    setState(() => busy = true);
    try {
      final result = await widget.request('');
      final offered = await widget.connectorRequest?.call('');
      if (!mounted || ticket != generation) return;
      setState(() {
        connections = [
          for (final item in (result as List? ?? const []))
            Map<String, dynamic>.from(item as Map),
        ];
        connectors = [
          for (final item in offered as List? ?? [])
            Map<String, dynamic>.from(item as Map),
        ];
        if (!connectors.any((c) => c['id'] == source)) {
          source = connectors.any((c) => c['id'] == widget.initialConnectorId)
              ? widget.initialConnectorId
              : connectors.isEmpty
              ? null
              : '${connectors.first['id']}';
        }
        if (!authMethods.contains(authMethod)) {
          authMethod = authMethods.firstOrNull;
        }
        loaded = true;
        error = null;
      });
    } catch (failure) {
      if (mounted && ticket == generation) {
        setState(() => error = 'Connections are unavailable. $failure');
      }
    } finally {
      if (mounted && ticket == generation) setState(() => busy = false);
    }
  }

  Future<void> connect() async {
    if (source == null ||
        authMethod == null ||
        _connectionId.text.trim().isEmpty ||
        _value.text.isEmpty) {
      setState(() => error = 'Provide a connection name and a value.');
      return;
    }
    final ticket = ++generation;
    setState(() {
      busy = true;
      notice = null;
      error = null;
    });
    final secret = _value.text;
    _value.clear();
    try {
      final result = await widget.request(
        '${Uri.encodeComponent(_connectionId.text.trim())}/connect',
        body: {
          'connectorId': source,
          'authMethodId': authMethod,
          'value': secret,
        },
      );
      if (!mounted || ticket != generation) return;
      final record = Map<String, dynamic>.from(result as Map);
      setState(() {
        _connectionId.clear();
        notice =
            'Connected ${record['connectorId']} (${connectionStatus(record['status'])}).';
      });
      await load();
    } catch (failure) {
      if (mounted && ticket == generation) {
        setState(() => error = 'The connection was refused. $failure');
      }
    } finally {
      if (mounted && ticket == generation) setState(() => busy = false);
    }
  }

  Future<void> probe(String id) =>
      act('${Uri.encodeComponent(id)}/probe', id, 'Probed $id.');
  Future<void> disconnect(String id) =>
      act('${Uri.encodeComponent(id)}/disconnect', id, 'Disconnected $id.');

  Future<void> act(String action, String id, String message) async {
    final ticket = ++generation;
    setState(() {
      busy = true;
      notice = null;
      error = null;
    });
    try {
      await widget.request(action);
      if (!mounted || ticket != generation) return;
      setState(() => notice = message);
      await load();
    } catch (failure) {
      if (mounted && ticket == generation) {
        setState(() => error = '$action failed. $failure');
      }
    } finally {
      if (mounted && ticket == generation) setState(() => busy = false);
    }
  }

  Future<void> startOAuth() async {
    final open = widget.onOpen;
    if (source == null ||
        authMethod == null ||
        open == null ||
        _connectionId.text.trim().isEmpty) {
      setState(() => error = 'Provide a connection name to authorize.');
      return;
    }
    setState(() {
      busy = true;
      error = null;
    });
    try {
      final result = await widget.request(
        '${Uri.encodeComponent(_connectionId.text.trim())}/login',
        body: {'connectorId': source, 'authMethodId': authMethod},
      );
      final url = Uri.parse((result as Map)['url'] as String);
      if (!url.hasAuthority || !['https', 'http'].contains(url.scheme)) {
        throw const FormatException();
      }
      await open(url);
      if (mounted) {
        setState(
          () => notice = 'Authorization opened. Complete sign-in, then refresh connections.',
        );
      }
    } catch (_) {
      if (mounted) {
        setState(
          () => error = 'Provider authorization could not start. Try again.',
        );
      }
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Padding(
        padding: const EdgeInsets.fromLTRB(16, 10, 8, 10),
        child: Row(
          children: [
            const Expanded(
              child: Text(
                'Connections',
                style: TextStyle(fontSize: 20, fontWeight: FontWeight.w600),
              ),
            ),
            IconButton(
              tooltip: 'Refresh connections',
              onPressed: load,
              icon: const Icon(Icons.refresh),
            ),
          ],
        ),
      ),
      if (error != null)
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16),
          child: Text(
            error!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
        ),
      if (notice != null)
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16),
          child: Text(notice!),
        ),
      if (busy) const LinearProgressIndicator(minHeight: 2),
      const Divider(height: 1),
      Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            DropdownButtonFormField<String>(
              key: ValueKey('connect_source_$source'),
              initialValue: source,
              decoration: const InputDecoration(
                labelText: 'Source',
                border: OutlineInputBorder(),
              ),
              items: [
                for (final connector in connectors)
                  DropdownMenuItem(
                    value: '${connector['id']}',
                    child: Text('${connector['displayName']}'),
                  ),
              ],
              onChanged: busy
                  ? null
                  : (value) {
                      if (value != null) {
                        setState(() {
                          source = value;
                          authMethod = authMethods.firstOrNull;
                        });
                      }
                    },
            ),
            const SizedBox(height: 12),
            if (source == 'windows' && widget.windowsRequest != null)
              ConstrainedBox(
                constraints: const BoxConstraints(maxHeight: 320),
                child: SingleChildScrollView(
                  child: WindowsStorageSetup(
                    key: ValueKey(
                      connections
                          .where((row) => row['connectorId'] == 'windows')
                          .map(
                            (row) => '${row['connectionId']}:${row['status']}',
                          )
                          .join(','),
                    ),
                    request: widget.request,
                    windowsRequest: widget.windowsRequest!,
                    onChanged: load,
                  ),
                ),
              ),
            if (source != 'windows' || widget.windowsRequest == null) ...[
              TextField(
                key: const Key('connect_name'),
                controller: _connectionId,
                decoration: const InputDecoration(
                  labelText: 'Connection name',
                  border: OutlineInputBorder(),
                ),
              ),
              const SizedBox(height: 12),
              if (!usesOAuth)
                TextField(
                  key: const Key('connect_value'),
                  controller: _value,
                  obscureText: true,
                  autocorrect: false,
                  enableSuggestions: false,
                  decoration: const InputDecoration(
                    labelText: 'Connection string or token',
                    border: OutlineInputBorder(),
                  ),
                ),
              const SizedBox(height: 12),
              if (connectors.isEmpty && loaded)
                const Text('No connectors are available for this deployment.'),
              if (authMethods.length > 1)
                DropdownButtonFormField<String>(
                  key: ValueKey('auth_method_$source'),
                  initialValue: authMethod,
                  decoration: const InputDecoration(
                    labelText: 'Authentication method',
                  ),
                  items: [
                    for (final method in authMethods)
                      DropdownMenuItem(value: method, child: Text(method)),
                  ],
                  onChanged: busy
                      ? null
                      : (value) => setState(() => authMethod = value),
                ),
              if (!usesOAuth)
                FilledButton.icon(
                  key: const Key('connect_submit'),
                  onPressed: busy || source == null ? null : connect,
                  icon: const Icon(Icons.link),
                  label: const Text('Connect'),
                ),
              if (usesOAuth)
                TextButton.icon(
                  onPressed: busy ? null : startOAuth,
                  icon: const Icon(Icons.open_in_new),
                  label: const Text('Sign in with the provider'),
                ),
            ],
          ],
        ),
      ),
      const Divider(height: 1),
      Expanded(
        child: !loaded
            ? (error != null
                  ? const SizedBox.shrink()
                  : const Center(child: CircularProgressIndicator()))
            : connections.isEmpty
            ? const Center(child: Text('No connections yet.'))
            : ListView(
                children: [
                  for (final connection in connections)
                    ListTile(
                      leading: Icon(
                        connectionStatus(connection['status']) == 'Connected'
                            ? Icons.key_outlined
                            : Icons.error_outline,
                      ),
                      title: Text(
                        '${connection['connectorId']} · ${connection['connectionId']}',
                      ),
                      subtitle: Text(connectionStatus(connection['status'])),
                      trailing: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          IconButton(
                            tooltip: 'Probe connection',
                            onPressed: busy
                                ? null
                                : () => probe('${connection['connectionId']}'),
                            icon: const Icon(Icons.network_check),
                          ),
                          IconButton(
                            tooltip: 'Disconnect',
                            onPressed: busy
                                ? null
                                : () => disconnect(
                                    '${connection['connectionId']}',
                                  ),
                            icon: const Icon(Icons.link_off),
                          ),
                        ],
                      ),
                    ),
                ],
              ),
      ),
    ],
  );
}
