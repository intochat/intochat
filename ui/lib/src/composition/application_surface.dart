import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'brain_session_controller.dart';
import 'view_host.dart';

/// Renders the surface an application declares. The host decides how the application is
/// started; the application owns the component tree.
class ApplicationSurface extends StatefulWidget {
  const ApplicationSurface({
    super.key,
    required this.client,
    required this.workspace,
    required this.application,
    required this.start,
    required this.knowsApp,
    this.onActivate,
  });

  final IntoChatOSUiClient client;
  final String workspace, application;
  final Future<Map<String, dynamic>> Function() start;
  final ValueChanged<Map<String, dynamic>>? onActivate;

  /// The apps an OpenWindow intent may open.
  final bool Function(String app) knowsApp;

  @override
  State<ApplicationSurface> createState() => _ApplicationSurfaceState();
}

class _ApplicationSurfaceState extends State<ApplicationSurface> {
  late Future<Map<String, dynamic>> _surface = _start();
  BrainSessionController? _ownedSession;

  Future<Map<String, dynamic>> _start() => widget.start();

  BrainSessionController _ensureOwnedSession() =>
      _ownedSession ??= BrainSessionController(
        brainId: widget.workspace,
        client: widget.client,
        knowsApp: widget.knowsApp,
        onActivate: widget.onActivate,
      );

  @override
  void didUpdateWidget(ApplicationSurface oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.workspace != widget.workspace ||
        oldWidget.application != widget.application ||
        oldWidget.client != widget.client) {
      if (oldWidget.client != widget.client) {
        _ownedSession?.dispose();
        _ownedSession = null;
      } else {
        _ownedSession?.switchBrain(widget.workspace);
      }
      _surface = _start();
    }
  }

  @override
  void dispose() {
    _ownedSession?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => FutureBuilder<Map<String, dynamic>>(
    future: _surface,
    builder: (context, snapshot) {
      if (snapshot.hasError) {
        return Center(
          child: TextButton(
            onPressed: () => setState(() {
              _surface = _start();
            }),
            child: const Text('Retry application'),
          ),
        );
      }
      if (snapshot.connectionState != ConnectionState.done ||
          !snapshot.hasData) {
        return const Center(child: CircularProgressIndicator());
      }
      final surface = snapshot.data!;
      final host = ViewHost(
        key: ValueKey('${widget.workspace}/${widget.application}'),
        kind: surface['kind'] as String,
        name: surface['name'] as String,
        onActivate: widget.onActivate,
        knowsApp: widget.knowsApp,
      );
      final ancestor = BrainSessionScope.readOf(context);
      if (ancestor != null &&
          ancestor.brainId == widget.workspace &&
          identical(ancestor.client, widget.client)) {
        return host;
      }
      return BrainSessionScope(controller: _ensureOwnedSession(), child: host);
    },
  );
}
