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
