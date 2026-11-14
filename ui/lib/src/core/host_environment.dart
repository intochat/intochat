import 'package:http/http.dart' as http;

import 'process_environment_stub.dart'
    if (dart.library.io) 'process_environment_io.dart'
    as process_env;
import 'runtime_surface_io.dart'
    if (dart.library.js_interop) 'runtime_surface_web.dart'
    as surface;
import 'served_config_io.dart'
    if (dart.library.js_interop) 'served_config_web.dart'
    as served;

abstract final class IntoChatOSHostEnv {
  static const uiBaseVariable = 'INTOCHATOS_UI_BASE';
  static const shellVariable = 'INTOCHATOS_SHELL';
  static const defaultShellName = 'desk';
  static const chatVariable = 'INTOCHATOS_CHAT';
  static const defaultChatName = 'main';
  static const presenceTokenVariable = 'INTOCHAT_PRESENCE_TOKEN';
  static const presenceHeaderName = 'X-IntoChat-Presence';
  static const hostProcessVariables = {
    uiBaseVariable,
    shellVariable,
    chatVariable,
    presenceTokenVariable,
  };

  /// Optional `--presence-token` CLI argument captured in [main].
  static String? cliPresenceToken;

  /// Parses `--presence-token=<token>` or `--presence-token <token>` from CLI [args].
  static void setCliArguments(List<String> args) {
    cliPresenceToken = parsePresenceTokenArg(args);
  }

  static String? parsePresenceTokenArg(List<String> args) {
    for (var i = 0; i < args.length; i++) {
      final arg = args[i];
      if (arg.startsWith('--presence-token=')) {
        final value = arg.substring('--presence-token='.length).trim();
        if (value.isNotEmpty) return value;
      } else if (arg == '--presence-token' && i + 1 < args.length) {
        final value = args[i + 1].trim();
        if (value.isNotEmpty) return value;
      }
    }
    return null;
  }

  /// Resolves the desktop presence proof token from CLI args, `--dart-define`,
  /// or `Platform.environment['INTOCHAT_PRESENCE_TOKEN']`.
  static String? resolvePresenceToken({
    String fromDefine = const String.fromEnvironment(presenceTokenVariable),
    String fromAltDefine = const String.fromEnvironment('presence-token'),
    Map<String, String>? processEnvironment,
  }) {
    final cli = cliPresenceToken;
    if (cli != null && cli.isNotEmpty) {
      return cli;
    }
    if (fromDefine.isNotEmpty) {
      return fromDefine;
    }
    if (fromAltDefine.isNotEmpty) {
      return fromAltDefine;
    }
    final process = processEnvironment ?? process_env.readProcessEnvironment();
    final raw = process[presenceTokenVariable]?.trim() ?? '';
    return raw.isEmpty ? null : raw;
  }

  /// Extracts `#presence=<token>` from `window.location.hash` on web and
  /// strips it from the browser address bar immediately.
  static String? consumeWebPresenceToken() => surface.consumeWebPresenceToken();

  /// The values config.json served next to a web bundle, loaded once at boot by
  /// [loadServedConfig]. Keys are the host variable names above. Sits between
  /// baked defines and the remaining fallbacks in every resolver.
  static Map<String, String> servedConfig = const {};

  static Future<void> loadServedConfig({http.Client? client}) async {
    // A bundle with the define baked (the deployed shell) never fetches.
    if (const String.fromEnvironment(uiBaseVariable).isNotEmpty) {
      return;
    }
    servedConfig = await served.fetchServedConfig(client) ?? const {};
  }

  static String resolveUiBaseRaw({
    String fromDefine = const String.fromEnvironment(uiBaseVariable),
    Map<String, String>? processEnvironment,
  }) {
    if (fromDefine.isNotEmpty) {
      return fromDefine;
    }
    final fromPage = rememberedUiBase(
      query: surface.queryUiBase(),
      stored: surface.storedUiBase(),
      remember: surface.storeUiBase,
    );
    if (fromPage != null) {
      return fromPage;
    }
    final servedValue = servedConfig[uiBaseVariable] ?? '';
    if (servedValue.isNotEmpty) {
      return servedValue;
    }
    final process = processEnvironment ?? process_env.readProcessEnvironment();
    return process[uiBaseVariable] ?? '';
  }

  static Uri requireUiBaseUri({
    String fromDefine = const String.fromEnvironment(uiBaseVariable),
    Map<String, String>? processEnvironment,
  }) {
    final raw = resolveUiBaseRaw(
      fromDefine: fromDefine,
      processEnvironment: processEnvironment,
    );
    if (raw.isNotEmpty) {
      return browserKernelUri(Uri.parse(raw), surface.sameOriginUiBase());
    }

    // Deployed shell served from intochat-os-ui (same origin) has no process env.
    final sameOrigin = surface.sameOriginUiBase();
    if (sameOrigin != null) {
      return sameOrigin;
    }

    throw StateError(
      '$uiBaseVariable is required (AppHost WithFlutterHost injects it).',
    );
  }

  /// The kernel URL a browser page was pointed at. A `uiBase` query wins and is
  /// remembered, because an installed PWA starts from its manifest start_url,
  /// which carries no query: the next start reconnects to the same OS, local or
  /// hosted. Without a query the remembered value applies.
  static String? rememberedUiBase({
    required String? query,
    required String? stored,
    required void Function(String value) remember,
  }) {
    if (query != null && query.isNotEmpty) {
      if (query != stored) {
        remember(query);
      }
      return query;
    }
    return stored == null || stored.isEmpty ? null : stored;
  }

  static String resolveShell({
    String fromDefine = const String.fromEnvironment(shellVariable),
    Map<String, String>? processEnvironment,
  }) {
    if (fromDefine.isNotEmpty) {
      return fromDefine;
    }
    final servedValue = servedConfig[shellVariable] ?? '';
    if (servedValue.isNotEmpty) {
      return servedValue;
    }
    final process = processEnvironment ?? process_env.readProcessEnvironment();
    final raw = process[shellVariable] ?? '';
    if (raw.isEmpty) {
      return defaultShellName;
    }
    return raw;
  }

  static String resolveChat({
    String fromDefine = const String.fromEnvironment(chatVariable),
    Map<String, String>? processEnvironment,
  }) {
    if (fromDefine.isNotEmpty) {
      return fromDefine;
    }
    final servedValue = servedConfig[chatVariable] ?? '';
    if (servedValue.isNotEmpty) {
      return servedValue;
    }
    final process = processEnvironment ?? process_env.readProcessEnvironment();
    final raw = process[chatVariable] ?? '';
    if (raw.isEmpty) {
      return defaultChatName;
    }
    return raw;
  }
}

/// Keeps local shell and kernel cookies same-site without rewriting remote hosts.
Uri browserKernelUri(Uri kernel, Uri? page) {
  const loopback = {'localhost', '127.0.0.1', '::1', '[::1]'};
  if (page != null &&
      loopback.contains(kernel.host) &&
      loopback.contains(page.host)) {
    return kernel.replace(host: page.host);
  }
  return kernel;
}
