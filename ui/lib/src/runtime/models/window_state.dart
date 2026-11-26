/// The declared states a window can be in before it has content to draw. Each state renders; none
/// leaves the window blank.
enum WindowStatus {
  loading,
  empty,
  permissionDenied,
  expiredConnection,
  failed,
}

final class WindowState {
  const WindowState(this.status, {this.message});

  final WindowStatus status;
  final String? message;

  static WindowState? fromMetadata(Map<String, dynamic>? metadata) {
    final raw = metadata?['state'];
    if (raw is! String) {
      return null;
    }
    return WindowState(
      WindowStatus.values.firstWhere(
        (status) => status.name == raw,
        orElse: () => WindowStatus.failed,
      ),
      message: metadata?['message'] as String?,
    );
  }
}

final class StarterPrompt {
  const StarterPrompt({
    required this.id,
    required this.label,
    required this.prompt,
    required this.source,
  });

  final String id;
  final String label;
  final String prompt;
  final String source;

  factory StarterPrompt.fromJson(Map<String, dynamic> json) => StarterPrompt(
    id: json['id'] as String? ?? '',
    label: json['label'] as String? ?? '',
    prompt: json['prompt'] as String? ?? '',
    source: json['source'] as String? ?? '',
  );
}

/// The first-run workspace state: the assistant plus starter prompts that match the connected
/// sources. Rendered when the workspace holds no window yet.
final class FirstRunState {
  const FirstRunState({
    required this.assistantId,
    required this.assistantTitle,
    required this.prompts,
  });

  final String assistantId;
  final String assistantTitle;
  final List<StarterPrompt> prompts;

  factory FirstRunState.fromMetadata(Map<String, dynamic>? metadata) {
    final raw = metadata?['prompts'];
    final prompts = raw is List
        ? raw
              .whereType<Map>()
              .map(
                (prompt) =>
                    StarterPrompt.fromJson(Map<String, dynamic>.from(prompt)),
              )
              .toList(growable: false)
        : const <StarterPrompt>[];
    return FirstRunState(
      assistantId: metadata?['assistantId'] as String? ?? 'intocaht',
      assistantTitle: metadata?['assistantTitle'] as String? ?? 'IntoChat',
      prompts: prompts,
    );
  }
}
