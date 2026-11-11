import 'dart:async';

import 'package:battery_plus/battery_plus.dart';
import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:device_info_plus/device_info_plus.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:geolocator/geolocator.dart';
import 'package:package_info_plus/package_info_plus.dart';
import 'package:routeables_client/routeables_client.dart' as rt;
import 'package:sensors_plus/sensors_plus.dart';
import 'package:share_plus/share_plus.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:url_launcher/url_launcher.dart';

/// The device APIs of this app the server may call (`AddRouteablesEssentials` on the server): the view models use the .NET MAUI
/// contracts (`IPreferences`, `IConnectivity`, `IGeolocation`…), the calls reach this phone, this browser or this desktop. Every service
/// answers in the shapes of `browserEssentials()` of `@routeables/client`: the server reads both the same way.
///
/// What Flutter cannot do without another plugin is not announced (the compass, text to speech, the screen reader…): the server says
/// that it is not supported.
Map<String, rt.EssentialsService> flutterEssentials() => {
      'preferences': _preferences(),
      'connectivity': _connectivity(),
      'appInfo': _appInfo(),
      'deviceInfo': _deviceInfo(),
      'deviceDisplay': _deviceDisplay(),
      'battery': _battery(),
      'vibration': _vibration(),
      'hapticFeedback': _hapticFeedback(),
      'clipboard': _clipboard(),
      'geolocation': _geolocation(),
      'accelerometer': _sensor(_SensorKind.accelerometer),
      'gyroscope': _sensor(_SensorKind.gyroscope),
      'magnetometer': _sensor(_SensorKind.magnetometer),
      'barometer': _sensor(_SensorKind.barometer),
      'phoneDialer': _phoneDialer(),
      'sms': _sms(),
      'email': _email(),
      'browser': _browser(),
      'launcher': _launcher(),
      'map': _mapService(),
      'share': _share(),
    };

Never _fail(String message, [String code = rt.EssentialsErrorCodes.notSupported]) => throw rt.EssentialsError(message, code);

String _text(Object? value, String name) =>
    value is String ? value : _fail('The $name must be a string.', rt.EssentialsErrorCodes.invalidArgument);

Map<String, Object?> _object(Object? value) => rt.asMap(value);

bool get _isMobile => !kIsWeb && (defaultTargetPlatform == TargetPlatform.android || defaultTargetPlatform == TargetPlatform.iOS);

// ---------------------------------------------------------------- storage

const _prefix = 'routeables:prefs:';

String _preferenceKey(String container, String key) => '$_prefix${Uri.encodeComponent(container)}:$key';

/// `IPreferences` in the shared preferences of the app: the keys are `routeables:prefs:<container>:<key>`, as in the browser.
rt.EssentialsService _preferences() {
  Iterable<String> keysOf(SharedPreferences prefs, String? container) {
    final start = container == null ? _prefix : '$_prefix${Uri.encodeComponent(container)}:';
    return prefs.getKeys().where((key) => key.startsWith(start));
  }

  return rt.EssentialsService(
    state: () async {
      final prefs = await SharedPreferences.getInstance();
      final containers = <String, Map<String, String>>{};
      for (final key in keysOf(prefs, null)) {
        final rest = key.substring(_prefix.length);
        final separator = rest.indexOf(':');
        final value = prefs.get(key);
        if (separator < 0 || value == null) continue;
        final String container;
        try {
          container = Uri.decodeComponent(rest.substring(0, separator));
        } catch (_) {
          continue;
        }
        (containers[container] ??= {})[rest.substring(separator + 1)] = '$value';
      }
      return containers;
    },
    methods: {
      'set': (args) async {
        final prefs = await SharedPreferences.getInstance();
        await prefs.setString(_preferenceKey(_text(args.elementAtOrNull(0), 'container'), _text(args.elementAtOrNull(1), 'key')),
            _text(args.elementAtOrNull(2), 'value'));
        return null;
      },
      'remove': (args) async {
        final prefs = await SharedPreferences.getInstance();
        await prefs.remove(_preferenceKey(_text(args.elementAtOrNull(0), 'container'), _text(args.elementAtOrNull(1), 'key')));
        return null;
      },
      'clear': (args) async {
        final prefs = await SharedPreferences.getInstance();
        for (final key in [...keysOf(prefs, _text(args.elementAtOrNull(0), 'container'))]) {
          await prefs.remove(key);
        }
        return null;
      },
    },
  );
}

// ---------------------------------------------------------------- device

/// `IConnectivity`: connectivity_plus.
rt.EssentialsService _connectivity() {
  Map<String, Object?> read(List<ConnectivityResult> results) {
    String? profile(ConnectivityResult result) => switch (result) {
          ConnectivityResult.wifi => 'WiFi',
          ConnectivityResult.mobile => 'Cellular',
          ConnectivityResult.ethernet => 'Ethernet',
          ConnectivityResult.bluetooth => 'Bluetooth',
          ConnectivityResult.none => null,
          _ => 'Unknown',
        };
    final profiles = {...results.map(profile).whereType<String>()};
    return {'networkAccess': profiles.isEmpty ? 'None' : 'Internet', 'profiles': [...profiles]};
  }

  return rt.EssentialsService(
    state: () async => read(await Connectivity().checkConnectivity()),
    events: {
      'changed': (emit) {
        final subscription = Connectivity().onConnectivityChanged.listen((results) => emit(read(results)));
        return () => unawaited(subscription.cancel());
      },
    },
  );
}

/// `IAppInfo`: package_info_plus, and the theme of the platform.
rt.EssentialsService _appInfo() => rt.EssentialsService(state: () async {
      final info = await PackageInfo.fromPlatform();
      final dispatcher = WidgetsBinding.instance.platformDispatcher;
      const rtl = {'ar', 'fa', 'he', 'ur', 'ps', 'yi'};
      return {
        'packageName': info.packageName,
        'name': info.appName,
        'versionString': info.version,
        'buildString': info.buildNumber,
        'requestedTheme': dispatcher.platformBrightness == Brightness.dark ? 'Dark' : 'Light',
        'requestedLayoutDirection': rtl.contains(dispatcher.locale.languageCode) ? 'RightToLeft' : 'LeftToRight',
        'packagingModel': 'Unpackaged',
      };
    });

/// `IDeviceInfo`: device_info_plus. Each platform names its fields its own way: the common ones are read.
rt.EssentialsService _deviceInfo() => rt.EssentialsService(state: () async {
      final data = (await DeviceInfoPlugin().deviceInfo).data;
      String field(List<String> names) {
        for (final name in names) {
          final value = data[name];
          if (value != null && '$value'.isNotEmpty) return value is Enum ? value.name : '$value';
        }
        return '';
      }

      final version = data['version'];
      final platform = kIsWeb
          ? 'Browser'
          : switch (defaultTargetPlatform) {
              TargetPlatform.android => 'Android',
              TargetPlatform.iOS => 'iOS',
              TargetPlatform.macOS => 'macOS',
              TargetPlatform.windows => 'WinUI',
              TargetPlatform.linux => 'Linux',
              TargetPlatform.fuchsia => 'Unknown',
            };
      final view = WidgetsBinding.instance.platformDispatcher.views.firstOrNull;
      final shortest = view == null ? 0.0 : view.physicalSize.shortestSide / view.devicePixelRatio;
      return {
        'model': field(['model', 'productName', 'platform', 'prettyName']),
        'manufacturer': field(['manufacturer', 'vendor', 'brand']),
        'name': field(['name', 'computerName', 'browserName', 'device']),
        'versionString': version is Map ? '${version['release'] ?? ''}' : field(['systemVersion', 'osRelease', 'displayVersion', 'versionId', 'appVersion']),
        'platform': platform,
        'idiom': _isMobile ? (shortest >= 600 ? 'Tablet' : 'Phone') : 'Desktop',
        'deviceType': data['isPhysicalDevice'] == false ? 'Virtual' : 'Physical',
      };
    });

/// Tells the server when the size of the window changes.
class _MetricsObserver with WidgetsBindingObserver {
  _MetricsObserver(this.changed);

  final void Function() changed;

  @override
  void didChangeMetrics() => changed();
}

/// `IDeviceDisplay`: the view of the app. Keeping the screen on takes a plugin (wakelock_plus): this sample does not.
rt.EssentialsService _deviceDisplay() {
  Map<String, Object?> display() {
    final view = WidgetsBinding.instance.platformDispatcher.views.firstOrNull;
    if (view == null) return {'width': 0, 'height': 0, 'density': 1, 'orientation': 'Unknown', 'rotation': 'Unknown', 'refreshRate': 0};
    final size = view.physicalSize;
    return {
      'width': size.width.round(),
      'height': size.height.round(),
      'density': view.devicePixelRatio,
      'orientation': size.width > size.height ? 'Landscape' : 'Portrait',
      'rotation': 'Unknown',
      'refreshRate': view.display.refreshRate,
    };
  }

  return rt.EssentialsService(
    state: () => {'keepScreenOn': false, 'display': display()},
    methods: {
      'setKeepScreenOn': (args) => args.elementAtOrNull(0) == true ? _fail('This app cannot keep the screen on (it needs wakelock_plus).') : null,
    },
    events: {
      'changed': (emit) {
        final observer = _MetricsObserver(() => emit(display()));
        WidgetsBinding.instance.addObserver(observer);
        return () => WidgetsBinding.instance.removeObserver(observer);
      },
    },
  );
}

/// `IBattery`: battery_plus.
rt.EssentialsService _battery() {
  final battery = Battery();
  Future<Map<String, Object?>> read([BatteryState? known]) async {
    final state = known ?? await battery.batteryState;
    final level = await battery.batteryLevel;
    bool? saver;
    try {
      saver = await battery.isInBatterySaveMode;
    } catch (_) {
      saver = null; // not on every platform
    }
    return {
      'chargeLevel': level / 100,
      'state': switch (state) {
        BatteryState.charging => 'Charging',
        BatteryState.full => 'Full',
        BatteryState.discharging => 'Discharging',
        BatteryState.connectedNotCharging => 'NotCharging',
        _ => 'Unknown',
      },
      'powerSource': switch (state) {
        BatteryState.charging || BatteryState.full || BatteryState.connectedNotCharging => 'AC',
        BatteryState.discharging => 'Battery',
        _ => 'Unknown',
      },
      'energySaverStatus': saver == null ? 'Unknown' : (saver ? 'On' : 'Off'),
    };
  }

  return rt.EssentialsService(
    state: () => read(),
    events: {
      'changed': (emit) {
        final subscription = battery.onBatteryStateChanged.listen((state) async => emit(await read(state)), onError: (Object _) {});
        return () => unawaited(subscription.cancel());
      },
    },
  );
}

/// `IVibration`: the vibration of the platform (one length: Flutter does not take a duration).
rt.EssentialsService _vibration() => rt.EssentialsService(
      state: () => {'isSupported': _isMobile},
      methods: {
        'vibrate': (_) async {
          if (!_isMobile) _fail('This device cannot vibrate.');
          await HapticFeedback.vibrate();
          return null;
        },
        'cancel': (_) => null,
      },
    );

/// `IHapticFeedback`: a click or a long press.
rt.EssentialsService _hapticFeedback() => rt.EssentialsService(
      state: () => {'isSupported': _isMobile},
      methods: {
        'perform': (args) async {
          if (!_isMobile) _fail('This device has no haptic feedback.');
          await (args.elementAtOrNull(0) == 'LongPress' ? HapticFeedback.heavyImpact() : HapticFeedback.selectionClick());
          return null;
        },
      },
    );

/// `IClipboard`: the clipboard of Flutter. It tells no change: the server reads it when it asks.
rt.EssentialsService _clipboard() => rt.EssentialsService(
      state: () async {
        try {
          return {'hasText': await Clipboard.hasStrings()};
        } catch (_) {
          return {'hasText': false};
        }
      },
      methods: {
        'getText': (_) async => (await Clipboard.getData(Clipboard.kTextPlain))?.text,
        'setText': (args) async {
          await Clipboard.setData(ClipboardData(text: '${args.elementAtOrNull(0) ?? ''}'));
          return null;
        },
      },
    );

// ---------------------------------------------------------------- location

Map<String, Object?> _location(Position p) => {
      'latitude': p.latitude,
      'longitude': p.longitude,
      'altitude': p.altitude,
      'accuracy': p.accuracy,
      'verticalAccuracy': p.altitudeAccuracy,
      'speed': p.speed,
      'course': p.heading,
      'timestamp': p.timestamp.millisecondsSinceEpoch,
    };

LocationAccuracy _accuracy(Object? desired) => switch (desired) {
      'Lowest' => LocationAccuracy.lowest,
      'Low' => LocationAccuracy.low,
      'High' => LocationAccuracy.high,
      'Best' => LocationAccuracy.best,
      _ => LocationAccuracy.medium,
    };

/// Asks for the permission when it is not given yet; fails as the server understands it.
Future<void> _locationAllowed() async {
  if (!await Geolocator.isLocationServiceEnabled()) _fail('The location is off on this device.', rt.EssentialsErrorCodes.notEnabled);
  var permission = await Geolocator.checkPermission();
  if (permission == LocationPermission.denied) permission = await Geolocator.requestPermission();
  if (permission == LocationPermission.denied || permission == LocationPermission.deniedForever) {
    _fail('The location was refused.', rt.EssentialsErrorCodes.permissionDenied);
  }
}

/// `IGeolocation`: geolocator. The positions of a listening go out as `locationChanged`.
rt.EssentialsService _geolocation() {
  Map<String, Object?>? last;
  StreamSubscription<Position>? listening;
  void Function(Object?)? changed;
  void Function(Object?)? failed;

  Future<void> stopListening() async {
    final subscription = listening;
    listening = null;
    await subscription?.cancel();
  }

  return rt.EssentialsService(
    state: () async {
      try {
        return {'isEnabled': await Geolocator.isLocationServiceEnabled()};
      } catch (_) {
        return {'isEnabled': false};
      }
    },
    methods: {
      'getLastKnown': (_) async {
        if (last != null || kIsWeb) return last;
        try {
          final position = await Geolocator.getLastKnownPosition();
          return position == null ? null : (last = _location(position));
        } catch (_) {
          return null;
        }
      },
      'getLocation': (args) async {
        final request = _object(args.elementAtOrNull(0));
        await _locationAllowed();
        final timeout = request['timeout'];
        final position = await Geolocator.getCurrentPosition(
          locationSettings: LocationSettings(
            accuracy: _accuracy(request['desiredAccuracy']),
            timeLimit: timeout is num && timeout > 0 ? Duration(milliseconds: timeout.round()) : null,
          ),
        );
        return last = _location(position);
      },
      'startListening': (args) async {
        final request = _object(args.elementAtOrNull(0));
        await _locationAllowed();
        await stopListening();
        listening = Geolocator.getPositionStream(locationSettings: LocationSettings(accuracy: _accuracy(request['desiredAccuracy']))).listen(
          (position) => changed?.call(last = _location(position)),
          onError: (Object error) {
            failed?.call({'error': error is PermissionDeniedException ? 'Unauthorized' : 'PositionUnavailable'});
            unawaited(stopListening());
          },
        );
        return true;
      },
      'stopListening': (_) async {
        await stopListening();
        return null;
      },
    },
    events: {
      'locationChanged': (emit) {
        changed = emit;
        return () {
          if (changed == emit) changed = null;
        };
      },
      'listeningFailed': (emit) {
        failed = emit;
        return () {
          if (failed == emit) failed = null;
        };
      },
    },
  );
}

// ---------------------------------------------------------------- sensors

enum _SensorKind { accelerometer, gyroscope, magnetometer, barometer }

const _gravity = 9.80665;

// A reading crosses the network: never more than 20 per second, whatever the speed asked.
const _intervals = {'Default': 200, 'UI': 66, 'Game': 50, 'Fastest': 50};

/// `IAccelerometer` (in g, as .NET MAUI), `IGyroscope`, `IMagnetometer`, `IBarometer`: sensors_plus. The readings go out as `reading`.
rt.EssentialsService _sensor(_SensorKind kind) {
  void Function(Object?)? emit;
  StreamSubscription<Object?>? subscription;
  // sensors_plus reads the motion sensors of a phone, and of a browser but its barometer.
  final supported = _isMobile || (kIsWeb && kind != _SensorKind.barometer);

  return rt.EssentialsService(
    state: () => {'isSupported': supported},
    methods: {
      'start': (args) async {
        if (!supported) _fail('This device has no ${kind.name} that Flutter reads.');
        await subscription?.cancel();
        final interval = Duration(milliseconds: _intervals['${_object(args.elementAtOrNull(0))['speed']}'] ?? 200);
        final Stream<Object?> readings = switch (kind) {
          _SensorKind.accelerometer =>
            accelerometerEventStream(samplingPeriod: interval).map((e) => {'x': e.x / _gravity, 'y': e.y / _gravity, 'z': e.z / _gravity}),
          _SensorKind.gyroscope => gyroscopeEventStream(samplingPeriod: interval).map((e) => {'x': e.x, 'y': e.y, 'z': e.z}),
          _SensorKind.magnetometer => magnetometerEventStream(samplingPeriod: interval).map((e) => {'x': e.x, 'y': e.y, 'z': e.z}),
          _SensorKind.barometer => barometerEventStream(samplingPeriod: interval).map((e) => {'pressureInHectopascals': e.pressure}),
        };
        var lastSent = DateTime.fromMillisecondsSinceEpoch(0);
        subscription = readings.listen((reading) {
          final now = DateTime.now();
          if (now.difference(lastSent) < interval) return;
          lastSent = now;
          emit?.call(reading);
        }, onError: (Object _) => unawaited(subscription?.cancel()));
        return null;
      },
      'stop': (_) async {
        await subscription?.cancel();
        subscription = null;
        return null;
      },
    },
    events: {
      'reading': (e) {
        emit = e;
        return () {
          if (emit == e) emit = null;
        };
      },
    },
  );
}

// ---------------------------------------------------------------- communication

Future<bool> _open(Uri uri) async {
  if (!await launchUrl(uri, mode: LaunchMode.externalApplication)) _fail('Nothing on this device opens $uri.', rt.EssentialsErrorCodes.failed);
  return true;
}

String _addresses(Object? value) => [
      for (final address in value is List ? value : const []) Uri.encodeComponent('$address'.trim()),
    ].where((address) => address.isNotEmpty).join(',');

/// `IPhoneDialer`: a `tel:` link.
rt.EssentialsService _phoneDialer() => rt.EssentialsService(
      state: () => {'isSupported': _isMobile},
      methods: {'open': (args) => _open(Uri.parse('tel:${'${args.elementAtOrNull(0) ?? ''}'.replaceAll(RegExp(r'[^\d+*#,;]'), '')}'))},
    );

/// `ISms`: an `sms:` link.
rt.EssentialsService _sms() => rt.EssentialsService(
      state: () => {'isComposeSupported': _isMobile},
      methods: {
        'compose': (args) {
          final message = _object(args.elementAtOrNull(0));
          final recipients = [
            for (final recipient in message['recipients'] is List ? message['recipients']! as List : const []) '$recipient'.replaceAll(RegExp(r'[^\d+*#]'), ''),
          ].where((recipient) => recipient.isNotEmpty).join(',');
          final body = message['body'];
          return _open(Uri.parse('sms:$recipients${body is String && body.isNotEmpty ? '?body=${Uri.encodeComponent(body)}' : ''}'));
        },
      },
    );

/// `IEmail`: a `mailto:` link (no attachment, no HTML).
rt.EssentialsService _email() => rt.EssentialsService(
      state: () => {'isComposeSupported': true},
      methods: {
        'compose': (args) {
          final message = _object(args.elementAtOrNull(0));
          if (message['attachments'] is List && (message['attachments']! as List).isNotEmpty) _fail('A mailto: link cannot carry attachments.');
          if (message['bodyFormat'] == 'Html') _fail('A mailto: link cannot carry an HTML body.');
          final query = [
            if (message['subject'] case final String subject when subject.isNotEmpty) 'subject=${Uri.encodeComponent(subject)}',
            if (message['body'] case final String body when body.isNotEmpty) 'body=${Uri.encodeComponent(body)}',
            if (_addresses(message['cc']) case final cc when cc.isNotEmpty) 'cc=$cc',
            if (_addresses(message['bcc']) case final bcc when bcc.isNotEmpty) 'bcc=$bcc',
          ];
          return _open(Uri.parse('mailto:${_addresses(message['to'])}${query.isEmpty ? '' : '?${query.join('&')}'}'));
        },
      },
    );

/// `IBrowser`: the browser of the device.
rt.EssentialsService _browser() => rt.EssentialsService(methods: {
      'open': (args) => _open(Uri.parse('${_object(args.elementAtOrNull(0))['uri'] ?? ''}')),
    });

/// `ILauncher`: what the device opens for an address. The files of the server are not opened here.
rt.EssentialsService _launcher() => rt.EssentialsService(methods: {
      'canOpen': (args) => canLaunchUrl(Uri.parse('${args.elementAtOrNull(0) ?? ''}')),
      'open': (args) => launchUrl(Uri.parse('${args.elementAtOrNull(0) ?? ''}'), mode: LaunchMode.externalApplication),
    });

/// `IMap`: OpenStreetMap, as in the browser.
rt.EssentialsService _mapService() => rt.EssentialsService(methods: {
      'open': (args) {
        final request = _object(args.elementAtOrNull(0));
        final lat = request['latitude'], lon = request['longitude'];
        if (lat is! num || lon is! num) _fail('No coordinates.', rt.EssentialsErrorCodes.invalidArgument);
        final engine = switch (request['navigationMode']) {
          'Bicycling' => 'fossgis_osrm_bike',
          'Walking' => 'fossgis_osrm_foot',
          'Driving' || 'Transit' || 'Default' => 'fossgis_osrm_car',
          _ => null,
        };
        return _open(Uri.parse(engine == null
            ? 'https://www.openstreetmap.org/?mlat=$lat&mlon=$lon#map=15/$lat/$lon'
            : 'https://www.openstreetmap.org/directions?engine=$engine&route=%3B$lat%2C$lon#map=15/$lat/$lon'));
      },
    });

/// `IShare`: the share sheet of the platform (share_plus).
rt.EssentialsService _share() => rt.EssentialsService(methods: {
      'shareText': (args) async {
        final request = _object(args.elementAtOrNull(0));
        String? value(String name) => switch (request[name]) { final String text when text.isNotEmpty => text, _ => null };
        final uri = value('uri'), text = value('text'), title = value('title'), subject = value('subject');
        if (text == null && uri == null) _fail('Nothing to share.', rt.EssentialsErrorCodes.invalidArgument);
        // A text and an address go together as a text: the share sheet takes one or the other.
        await SharePlus.instance.share(text == null
            ? ShareParams(uri: Uri.parse(uri!), title: title, subject: subject)
            : ShareParams(text: uri == null ? text : '$text\n$uri', title: title, subject: subject));
        return null;
      },
    });
