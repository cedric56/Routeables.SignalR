// Makes the platform folders of the sample (android, ios, web, windows, macos, linux) with `flutter create`, then lets the app reach a
// server in plain HTTP, as the sample server is on this machine: cleartext traffic on Android, the network client entitlement on macOS,
// local networking on iOS. It also declares what the Essentials page uses: the location (Android, iOS, macOS) and the apps that open
// tel:, sms:, mailto: and web addresses (Android 11 hides the others). Run it once, from this folder:
//
//   dart run tool/setup.dart
//
// `flutter create` keeps the files that are there (lib, test, pubspec.yaml, this README): it only adds what is missing.
import 'dart:io';

Future<void> main() async {
  final create = await Process.start(
    'flutter',
    ['create', '--platforms=android,ios,web,windows,macos,linux', '--project-name', 'xanimals_flutter', '.'],
    runInShell: true,
    mode: ProcessStartMode.inheritStdio,
  );
  if (await create.exitCode != 0) {
    stderr.writeln('flutter create failed.');
    exit(1);
  }

  // Android 9 and later refuse plain HTTP unless the application says it uses it.
  patch('android/app/src/main/AndroidManifest.xml', (text) {
    var result = text;
    if (!result.contains('android.permission.INTERNET')) {
      result = result.replaceFirst('<application', '<uses-permission android:name="android.permission.INTERNET" />\n    <application');
    }
    if (!result.contains('usesCleartextTraffic')) {
      result = result.replaceFirst('<application', '<application\n        android:usesCleartextTraffic="true"');
    }
    // The Essentials page: the location, and the apps a link opens (url_launcher asks Android which can).
    for (final permission in ['ACCESS_FINE_LOCATION', 'ACCESS_COARSE_LOCATION']) {
      if (!result.contains('android.permission.$permission')) {
        result = result.replaceFirst('<application', '<uses-permission android:name="android.permission.$permission" />\n    <application');
      }
    }
    if (!result.contains('android:scheme="tel"')) {
      final intents = [
        for (final scheme in ['tel', 'sms', 'mailto', 'https', 'geo'])
          '        <intent>\n            <action android:name="android.intent.action.${scheme == 'https' || scheme == 'geo' ? 'VIEW' : scheme == 'tel' ? 'DIAL' : 'SENDTO'}" />\n'
              '            <data android:scheme="$scheme" />\n        </intent>',
      ].join('\n');
      result = result.contains('<queries>')
          ? result.replaceFirst('<queries>', '<queries>\n$intents')
          : result.replaceFirst('</manifest>', '    <queries>\n$intents\n    </queries>\n</manifest>');
    }
    return result;
  });

  // A macOS app is sandboxed: it may open connections only with the network client entitlement.
  for (final file in ['macos/Runner/DebugProfile.entitlements', 'macos/Runner/Release.entitlements']) {
    patch(file, (text) => text.contains('com.apple.security.network.client')
        ? text
        : text.replaceFirst('<dict>', '<dict>\n\t<key>com.apple.security.network.client</key>\n\t<true/>'));
    // The location of the Essentials page.
    patch(file, (text) => text.contains('com.apple.security.personal-information.location')
        ? text
        : text.replaceFirst('<dict>', '<dict>\n\t<key>com.apple.security.personal-information.location</key>\n\t<true/>'));
  }
  for (final file in ['ios/Runner/Info.plist', 'macos/Runner/Info.plist']) {
    patch(file, (text) => text.contains('NSLocationWhenInUseUsageDescription')
        ? text
        : text.replaceFirst('<dict>',
            '<dict>\n\t<key>NSLocationWhenInUseUsageDescription</key>\n\t<string>The Essentials page reads the location for the server.</string>'
            '\n\t<key>NSLocationUsageDescription</key>\n\t<string>The Essentials page reads the location for the server.</string>'));
  }

  // iOS lets an app reach this machine in plain HTTP when it allows local networking (App Transport Security).
  patch('ios/Runner/Info.plist', (text) => text.contains('NSAppTransportSecurity')
      ? text
      : text.replaceFirst('<dict>', '<dict>\n\t<key>NSAppTransportSecurity</key>\n\t<dict>\n\t\t<key>NSAllowsLocalNetworking</key>\n\t\t<true/>\n\t</dict>'));

  stdout.writeln('Ready: flutter run -d chrome --web-port 5179 (or windows, macos, linux, an emulator, a device).');
}

void patch(String path, String Function(String text) change) {
  final file = File(path);
  if (!file.existsSync()) return;
  final before = file.readAsStringSync();
  final after = change(before);
  if (after != before) {
    file.writeAsStringSync(after);
    stdout.writeln('Patched $path');
  }
}
