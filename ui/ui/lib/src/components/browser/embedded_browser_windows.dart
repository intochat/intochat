import 'dart:async';
import 'dart:io';
import 'dart:math';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:webview_windows/webview_windows.dart';

import '../../design/app_tile.dart';

// One native environment per process; each window has its own marked CDP page.

Future<int>? _environment;
typedef EnvironmentFactory = Future<int> Function();

@visibleForTesting
EnvironmentFactory environmentFactory = _defaultEnvironmentFactory;

@visibleForTesting
bool? debugBrowserPlatformSupported;

Future<int> _defaultEnvironmentFactory() async {
  final socket = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
  final port = socket.port;
  await socket.close();
  final profile = await Directory.systemTemp.createTemp('intochat-os-browser-');
  try {
    await WebviewController.initializeEnvironment(
      userDataPath: profile.path,
      additionalArguments:
          '--remote-debugging-port=$port '
          '--remote-debugging-address=127.0.0.1 --disable-background-networking '
          '--disable-features=ServiceWorker --force-webrtc-ip-handling-policy=disable_non_proxied_udp',
    );
  } on PlatformException catch (e) {
    if (e.code != 'environment_already_initialized') rethrow;
  }
  return port;
}

Future<bool> _isLoopbackPortOpen(int port) async {
  try {
    final socket = await Socket.connect(
      InternetAddress.loopbackIPv4,
      port,
      timeout: const Duration(milliseconds: 200),
    );
    await socket.close();
    return true;
  } catch (_) {
    return false;
  }
}

Future<String> _resolveDirectUrl(String rawUri) async {
  final trimmed = rawUri.trim();
  if (trimmed.isEmpty) return trimmed;
  final lower = trimmed.toLowerCase();
  final isLocal =
      lower.startsWith('/artcraft/') ||
      lower.contains('localhost') ||
      lower.contains('127.0.0.1');
  if (!isLocal) return trimmed;

  final craftSpec = IcAppIcon.findArtCraftByUrlOrName(lower);
  if (craftSpec != null) {
    final candidatePorts = <int>[
      if (craftSpec.appId == 'filmcraft') 18111,
      if (craftSpec.appId == 'photocraft') 18110,
      if (craftSpec.defaultWebPort != null) craftSpec.defaultWebPort!,
    ];
    for (final port in candidatePorts) {
      if (await _isLoopbackPortOpen(port)) {
        return 'http://localhost:$port/';
      }
    }
    final uiBase = IntoChatOSHostEnv.resolveUiBaseRaw();
    if (uiBase.isNotEmpty) {
      final parsed = Uri.tryParse(uiBase);
      if (parsed != null && parsed.hasAuthority) {
        return '${parsed.origin}/artcraft/${craftSpec.appId}/index.html';
      }
    }
    if (await _isLoopbackPortOpen(5080)) {
      return 'http://localhost:5080/artcraft/${craftSpec.appId}/index.html';
    }
    return 'https://os.test.intochat.io/artcraft/${craftSpec.appId}/index.html';
  }

  return trimmed;
}

class EmbeddedBrowser extends StatefulWidget {
  const EmbeddedBrowser({
    super.key,
    this.uri = '',
    required this.onConnect,
    required this.onDisconnect,
    this.onUnavailable,
  });
  final String uri;
  final Future<void> Function(int, String)? onConnect;
  final Future<void> Function(String)? onDisconnect;
  final Future<void> Function(String)? onUnavailable;

  @override
  State<EmbeddedBrowser> createState() => _EmbeddedBrowserState();
}

class _EmbeddedBrowserState extends State<EmbeddedBrowser> {
  WebviewController? _controller;
  final String _session = List.generate(
    16,
    (_) => Random.secure().nextInt(256).toRadixString(16).padLeft(2, '0'),
  ).join();
  String? _error;
  bool _ready = false;
  bool _connected = false;
  bool _controllerDisposed = false;
  bool get _supported =>
      debugBrowserPlatformSupported ??
      (Platform.isWindows && defaultTargetPlatform == TargetPlatform.windows);

  @override
  void initState() {
    super.initState();
    if (_supported) unawaited(_initialize());
  }

  @override
  void didUpdateWidget(EmbeddedBrowser oldWidget) {
    super.didUpdateWidget(oldWidget);
    final controller = _controller;
    if (_ready &&
        !_controllerDisposed &&
        controller != null &&
        widget.onConnect == null &&
        widget.uri.isNotEmpty &&
        widget.uri != oldWidget.uri) {
      unawaited(
        _resolveDirectUrl(widget.uri).then((resolved) {
          if (mounted && !_controllerDisposed) {
            return controller.loadUrl(resolved);
          }
        }),
      );
    }
  }

  Future<void> _initialize() async {
    try {
      final port = await (_environment ??= environmentFactory());
      if (!mounted) return;
      final controller = WebviewController();
      _controller = controller;
      await controller.initialize();
      if (!mounted) {
        await _disposeController();
        return;
      }
      await controller.setPopupWindowPolicy(WebviewPopupWindowPolicy.deny);
      final initialUrl = widget.onConnect == null && widget.uri.isNotEmpty
          ? await _resolveDirectUrl(widget.uri)
          : 'about:blank#intochat-os-$_session';
      await controller.loadUrl(initialUrl);
      if (!mounted) return;
      setState(() => _ready = true);
      _connected = true;
      await widget.onConnect?.call(port, _session);
      if (!mounted) await widget.onDisconnect?.call(_session);
    } catch (error) {
      _environment = null;
      final message =
          'Browser could not start. Check the WebView2 runtime and restart this window.\n$error';
      if (mounted) setState(() => _error = message);
      await widget.onUnavailable?.call(message);
    }
  }

  @override
  void dispose() {
    if (_connected) unawaited(_disconnect());
    unawaited(_disposeController());
    super.dispose();
  }

  Future<void> _disposeController() async {
    final controller = _controller;
    if (!_controllerDisposed &&
        controller != null &&
        controller.value.isInitialized) {
      _controllerDisposed = true;
      await controller.dispose();
    }
  }

  Future<void> _disconnect() async {
    try {
      await widget.onDisconnect?.call(_session);
    } catch (_) {
      /* The native page is also closed below. */
    }
  }

  @override
  Widget build(BuildContext context) {
    if (!_supported) {
      return const Center(
        child: Text('Browsing requires the Windows desktop app.'),
      );
    }
    if (_error != null) return Center(child: SelectableText(_error!));
    if (!_ready) return const Center(child: CircularProgressIndicator());
    return Webview(
      _controller!,
      permissionRequested: (_, _, _) => WebviewPermissionDecision.deny,
    );
  }
}
