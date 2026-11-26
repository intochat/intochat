part of 'ui_client.dart';

extension IntoChatOSUiClientNotifications on IntoChatOSUiClient {
  Future<OsNotificationList> listNotifications(
    String brainId, {
    int limit = 50,
    bool unreadOnly = false,
  }) async {
    final response = await _request(
      'GET',
      '/brains/${Uri.encodeComponent(brainId)}/notifications?limit=$limit&unreadOnly=$unreadOnly',
      timeout: const Duration(seconds: 15),
    );
    if (response.body.isEmpty) {
      return const OsNotificationList(items: [], unreadCount: 0);
    }
    return OsNotificationList.fromJson(
      Map<String, dynamic>.from(jsonDecode(response.body) as Map),
    );
  }

  Future<void> markNotificationRead(
    String brainId,
    String notificationId,
  ) async {
    await _request(
      'POST',
      '/brains/${Uri.encodeComponent(brainId)}/notifications/${Uri.encodeComponent(notificationId)}/read',
      body: const {},
      timeout: const Duration(seconds: 15),
    );
  }

  Future<void> markAllNotificationsRead(String brainId) async {
    await _request(
      'POST',
      '/brains/${Uri.encodeComponent(brainId)}/notifications/read-all',
      body: const {},
      timeout: const Duration(seconds: 15),
    );
  }

  Future<void> clearNotifications(String brainId) async {
    await _request(
      'POST',
      '/brains/${Uri.encodeComponent(brainId)}/notifications/clear',
      body: const {},
      timeout: const Duration(seconds: 15),
    );
  }
}
