// --- src/runtime/models/rejected.dart ---
final class Rejected {
  static const alias = 'rule.rejected';

  const Rejected({
    required this.operation,
    required this.rules,
    required this.messages,
  });

  factory Rejected.fromJson(Map<String, dynamic> json) => Rejected(
    operation: json['operation'] as String,
    rules: List<String>.unmodifiable(json['rules'] as List),
    messages: List<String>.unmodifiable(json['messages'] as List),
  );

  final String operation;
  final List<String> rules;
  final List<String> messages;

  Map<String, dynamic> toJson() => {
    'operation': operation,
    'rules': rules,
    'messages': messages,
  };
}

// --- src/runtime/models/window_state.dart ---
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
