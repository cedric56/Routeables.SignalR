import { EssentialsError, connectivityService, preferencesService } from './essentials.js';
import type { EssentialsService, EssentialsServices } from './essentials.js';

// The device APIs of a browser, one service per .NET MAUI Essentials contract (Routeables.SignalR.Essentials). Each one only does what its
// methods say; what the browser lacks is reported as `not-supported`, a refused permission as `permission-denied`.

/** What `browserEssentials` needs to know of the application. */
export interface BrowserEssentialsOptions {
  /** Where the paths of the server (uploads, downloads) are resolved: `document.baseURI` by default. */
  baseUrl?: string;
  /** What `IAppInfo` says of the application (the browser does not know its version). */
  app?: { name?: string; packageName?: string; version?: string; build?: string };
  /** Replaces `fetch` (tests, headers of your own). */
  fetch?: typeof fetch;
  /** `geocoding` through a Nominatim instance of yours (the public one forbids heavy use and ignores the browser's User-Agent). */
  nominatim?: string;
}

const g = globalThis as typeof globalThis & { navigator?: Navigator; document?: Document; window?: Window };
const isMobile = () => /Android|iPhone|iPad|iPod/i.test(g.navigator?.userAgent ?? '');

function fail(message: string, code: ConstructorParameters<typeof EssentialsError>[1] = 'not-supported'): never {
  throw new EssentialsError(message, code);
}

function domError(error: unknown): never {
  const name = (error as { name?: string } | null)?.name;
  if (name === 'NotAllowedError' || name === 'SecurityError') fail((error as Error).message || 'Refused.', 'permission-denied');
  if (name === 'AbortError') fail((error as Error).message || 'Cancelled.', 'cancelled');
  if (name === 'NotFoundError' || name === 'NotSupportedError' || name === 'OverconstrainedError') fail((error as Error).message || 'Not supported.');
  throw error;
}

// ---------------------------------------------------------------- files

interface FileLink {
  url: string;
  name: string;
  contentType: string;
}

/** Uploads and downloads the files of the session (`MapRouteablesEssentials` on the server). */
export class SessionFiles {
  constructor(private readonly options: BrowserEssentialsOptions = {}) {}

  private resolve(path: string): string {
    return new URL(path, this.options.baseUrl ?? g.document?.baseURI ?? 'http://localhost/').toString();
  }

  /** Sends a file to an upload slot of the server; returns its id. */
  async upload(path: string, file: Blob, name: string): Promise<string> {
    const send = this.options.fetch ?? fetch;
    const response = await send(this.resolve(path), {
      method: 'POST',
      body: file,
      credentials: 'same-origin',
      headers: { 'Content-Type': file.type || 'application/octet-stream', 'X-File-Name': encodeURIComponent(name) },
    });
    if (response.status === 413) fail(`'${name}' is larger than the server accepts.`, 'invalid-argument');
    if (!response.ok) fail(`The upload of '${name}' failed (${response.status}).`, 'failed');
    const { file: id } = (await response.json()) as { file?: string };
    if (typeof id !== 'string') fail('The server did not name the uploaded file.', 'failed');
    return id;
  }

  /** Uploads every file; returns their ids, in order. */
  async uploadAll(path: string, files: readonly File[]): Promise<string[]> {
    const ids: string[] = [];
    for (const file of files) ids.push(await this.upload(path, file, file.name));
    return ids;
  }

  /** Downloads a file the server gave a link to, with the name and the type the server said (its response is always a download). */
  async download(link: FileLink): Promise<File> {
    if (typeof link?.url !== 'string') fail('No file link.', 'invalid-argument');
    const send = this.options.fetch ?? fetch;
    const response = await send(this.resolve(link.url), { credentials: 'same-origin' });
    if (!response.ok) fail(`The file '${link.name}' could not be downloaded (${response.status}).`, 'failed');
    return new File([await response.blob()], link.name || 'file', { type: link.contentType || 'application/octet-stream' });
  }
}

/** Shows the file dialog; resolves with the files, or null when the user closes it. */
export function chooseFiles(options: { accept?: string | null; multiple?: boolean; capture?: string; title?: string | null }): Promise<File[] | null> {
  return new Promise((resolve, reject) => {
    const doc = g.document;
    if (!doc) return reject(new EssentialsError('No document.', 'not-supported'));
    const input = doc.createElement('input');
    input.type = 'file';
    input.multiple = !!options.multiple;
    if (options.accept) input.accept = options.accept;
    if (options.capture) input.setAttribute('capture', options.capture);
    if (options.title) input.setAttribute('aria-label', options.title);
    input.style.display = 'none';
    let settled = false;
    const finish = (files: File[] | null) => {
      if (settled) return;
      settled = true;
      input.remove();
      resolve(files && files.length > 0 ? files : null);
    };
    input.addEventListener('change', () => finish(Array.from(input.files ?? [])));
    input.addEventListener('cancel', () => finish(null));
    doc.body.appendChild(input);
    input.click();
  });
}

// ---------------------------------------------------------------- application and device

export function appInfoService(app: BrowserEssentialsOptions['app'] = {}): EssentialsService {
  const dark = () => g.window?.matchMedia?.('(prefers-color-scheme: dark)');
  const read = () => ({
    packageName: app.packageName ?? g.window?.location?.host ?? '',
    name: app.name ?? g.document?.title ?? '',
    versionString: app.version ?? '1.0',
    buildString: app.build ?? '1',
    requestedTheme: dark()?.matches ? 'Dark' : 'Light',
    requestedLayoutDirection: g.document?.documentElement?.dir === 'rtl' ? 'RightToLeft' : 'LeftToRight',
    packagingModel: 'Unpackaged',
  });
  return {
    state: read,
    methods: { showSettingsUI: () => fail('A browser has no settings page to show.') },
    events: {
      changed(emit) {
        const media = dark();
        const handler = () => emit(read());
        media?.addEventListener('change', handler);
        return () => media?.removeEventListener('change', handler);
      },
    },
  };
}

export function deviceInfoService(): EssentialsService {
  return {
    state() {
      const nav = g.navigator as (Navigator & { userAgentData?: { platform?: string; mobile?: boolean; brands?: { brand: string; version: string }[] } }) | undefined;
      const ua = nav?.userAgent ?? '';
      const brand = nav?.userAgentData?.brands?.find(b => !/Not.?A.?Brand|Chromium/i.test(b.brand));
      const tablet = /iPad|Tablet/i.test(ua) || (/Android/i.test(ua) && !/Mobile/i.test(ua));
      return {
        model: nav?.userAgentData?.platform ?? nav?.platform ?? '',
        manufacturer: nav?.vendor ?? '',
        name: brand?.brand ?? (/Firefox\//.test(ua) ? 'Firefox' : /Safari\//.test(ua) && !/Chrome\//.test(ua) ? 'Safari' : 'Browser'),
        versionString: brand?.version ?? ua.match(/(?:Firefox|Version|Chrome)\/([\d.]+)/)?.[1] ?? '',
        platform: 'Browser',
        idiom: tablet ? 'Tablet' : (nav?.userAgentData?.mobile ?? isMobile()) ? 'Phone' : 'Desktop',
        deviceType: 'Physical',
      };
    },
  };
}

export function deviceDisplayService(): EssentialsService {
  let wakeLock: { release(): Promise<void> } | null = null;
  let wanted = false;
  const display = () => {
    const w = g.window;
    const density = w?.devicePixelRatio || 1;
    const type = (g as { screen?: Screen }).screen?.orientation?.type ?? '';
    const angle = (g as { screen?: Screen }).screen?.orientation?.angle ?? 0;
    return {
      width: Math.round((w?.innerWidth ?? 0) * density),
      height: Math.round((w?.innerHeight ?? 0) * density),
      density,
      orientation: type.startsWith('portrait') ? 'Portrait' : type.startsWith('landscape') ? 'Landscape' : 'Unknown',
      rotation: ({ 0: 'Rotation0', 90: 'Rotation90', 180: 'Rotation180', 270: 'Rotation270' } as Record<number, string>)[angle] ?? 'Unknown',
      refreshRate: 0,
    };
  };
  const acquire = async () => {
    const nav = g.navigator as Navigator & { wakeLock?: { request(type: 'screen'): Promise<{ release(): Promise<void>; addEventListener(t: string, l: () => void): void }> } };
    if (!nav?.wakeLock) fail('This browser cannot keep the screen on.');
    const lock = await nav.wakeLock.request('screen');
    lock.addEventListener('release', () => { if (wakeLock === lock) wakeLock = null; });
    wakeLock = lock;
  };
  // The browser releases the lock when the page is hidden: taken again when it comes back.
  g.document?.addEventListener('visibilitychange', () => {
    if (wanted && wakeLock === null && g.document?.visibilityState === 'visible') void acquire().catch(() => {});
  });
  return {
    state: () => ({ keepScreenOn: wakeLock !== null, display: display() }),
    methods: {
      async setKeepScreenOn(on: unknown) {
        wanted = on === true;
        if (wanted) {
          if (wakeLock === null) await acquire();
        } else {
          const lock = wakeLock;
          wakeLock = null;
          await lock?.release();
        }
      },
    },
    events: {
      changed(emit) {
        const handler = () => emit(display());
        g.window?.addEventListener('resize', handler);
        (g as { screen?: Screen }).screen?.orientation?.addEventListener('change', handler);
        return () => {
          g.window?.removeEventListener('resize', handler);
          (g as { screen?: Screen }).screen?.orientation?.removeEventListener('change', handler);
        };
      },
    },
  };
}

interface BatteryLike extends EventTarget {
  level: number;
  charging: boolean;
}

export function batteryService(): EssentialsService {
  const manager = (): Promise<BatteryLike> | null => {
    const nav = g.navigator as Navigator & { getBattery?: () => Promise<BatteryLike> };
    return typeof nav?.getBattery === 'function' ? nav.getBattery() : null;
  };
  const read = (b: BatteryLike) => ({
    chargeLevel: b.level,
    state: b.charging ? (b.level >= 1 ? 'Full' : 'Charging') : 'Discharging',
    powerSource: b.charging ? 'AC' : 'Battery',
    energySaverStatus: 'Unknown',
  });
  return {
    async state() {
      const b = await manager();
      return b ? read(b) : { chargeLevel: 1, state: 'Unknown', powerSource: 'Unknown', energySaverStatus: 'Unknown' };
    },
    events: {
      changed(emit) {
        let stopped = false;
        let battery: BatteryLike | null = null;
        const handler = () => { if (battery) emit(read(battery)); };
        void manager()?.then(b => {
          if (stopped) return;
          battery = b;
          b.addEventListener('levelchange', handler);
          b.addEventListener('chargingchange', handler);
        });
        return () => {
          stopped = true;
          battery?.removeEventListener('levelchange', handler);
          battery?.removeEventListener('chargingchange', handler);
        };
      },
    },
  };
}

const canVibrate = () => typeof g.navigator?.vibrate === 'function';

export function vibrationService(): EssentialsService {
  return {
    state: () => ({ isSupported: canVibrate() }),
    methods: {
      vibrate(ms: unknown) {
        if (!canVibrate()) fail('This browser cannot vibrate.');
        g.navigator!.vibrate(Math.max(1, Math.min(5000, Number(ms) || 500)));
      },
      cancel() {
        if (canVibrate()) g.navigator!.vibrate(0);
      },
    },
  };
}

export function hapticFeedbackService(): EssentialsService {
  return {
    state: () => ({ isSupported: canVibrate() }),
    methods: {
      perform(type: unknown) {
        if (!canVibrate()) fail('This browser has no haptic feedback.');
        g.navigator!.vibrate(type === 'LongPress' ? 25 : 10);
      },
    },
  };
}

export function flashlightService(): EssentialsService {
  let stream: MediaStream | null = null;
  const supported = () => !!g.navigator?.mediaDevices?.getSupportedConstraints?.() && 'torch' in (g.navigator!.mediaDevices.getSupportedConstraints() as object);
  const torch = (track: MediaStreamTrack, on: boolean) => track.applyConstraints({ advanced: [{ torch: on } as MediaTrackConstraintSet] });
  return {
    methods: {
      isSupported: () => supported(),
      async turnOn() {
        if (!supported()) fail('This browser cannot drive the flashlight.');
        try {
          stream ??= await g.navigator!.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' } } });
          await torch(stream.getVideoTracks()[0]!, true);
        } catch (error) {
          stream?.getTracks().forEach(t => t.stop());
          stream = null;
          domError(error);
        }
      },
      async turnOff() {
        const current = stream;
        stream = null;
        if (!current) return;
        try { await torch(current.getVideoTracks()[0]!, false); } catch { /* stopping the camera turns it off anyway */ }
        current.getTracks().forEach(t => t.stop());
      },
    },
  };
}

export function semanticScreenReaderService(): EssentialsService {
  let region: HTMLElement | null = null;
  return {
    methods: {
      announce(text: unknown) {
        const doc = g.document;
        if (!doc) fail('No document.');
        if (!region) {
          region = doc.createElement('div');
          region.setAttribute('aria-live', 'polite');
          region.setAttribute('role', 'status');
          region.style.cssText = 'position:absolute;width:1px;height:1px;margin:-1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap;border:0;';
          doc.body.appendChild(region);
        }
        // Cleared first, so that the same text said twice is said twice.
        region.textContent = '';
        const target = region;
        setTimeout(() => { target.textContent = String(text ?? ''); }, 50);
      },
    },
  };
}

// ---------------------------------------------------------------- communication

/** The address of an SMS (`sms:` with the recipients and the body, as iOS and Android read it). */
export function smsUrl(message: { body?: unknown; recipients?: unknown }): string {
  const recipients = (Array.isArray(message.recipients) ? message.recipients : []).map(r => String(r).replace(/[^\d+*#]/g, '')).filter(r => r.length > 0);
  const body = typeof message.body === 'string' && message.body.length > 0 ? `?body=${encodeURIComponent(message.body)}` : '';
  return `sms:${recipients.join(',')}${body}`;
}

/** The `mailto:` address of an email. */
export function mailtoUrl(message: { subject?: unknown; body?: unknown; to?: unknown; cc?: unknown; bcc?: unknown }): string {
  const list = (value: unknown) => (Array.isArray(value) ? value : []).map(v => encodeURIComponent(String(v).trim())).filter(v => v.length > 0).join(',');
  const query: string[] = [];
  if (typeof message.subject === 'string' && message.subject.length > 0) query.push(`subject=${encodeURIComponent(message.subject)}`);
  if (typeof message.body === 'string' && message.body.length > 0) query.push(`body=${encodeURIComponent(message.body)}`);
  if (list(message.cc)) query.push(`cc=${list(message.cc)}`);
  if (list(message.bcc)) query.push(`bcc=${list(message.bcc)}`);
  return `mailto:${list(message.to)}${query.length ? '?' + query.join('&') : ''}`;
}

/** The OpenStreetMap page of a place, or of the route to it. */
export function mapUrl(request: { latitude?: unknown; longitude?: unknown; navigationMode?: unknown; placemark?: Record<string, unknown> | null }): string {
  const lat = Number(request.latitude);
  const lon = Number(request.longitude);
  const engine = ({ Bicycling: 'fossgis_osrm_bike', Walking: 'fossgis_osrm_foot', Driving: 'fossgis_osrm_car', Transit: 'fossgis_osrm_car', Default: 'fossgis_osrm_car' } as Record<string, string>)[String(request.navigationMode)];
  const place = request.placemark;
  if (place && (!Number.isFinite(lat) || !Number.isFinite(lon) || (lat === 0 && lon === 0))) {
    const query = ['subThoroughfare', 'thoroughfare', 'locality', 'postalCode', 'adminArea', 'countryName']
      .map(k => place[k]).filter(v => typeof v === 'string' && v.length > 0).join(', ');
    return `https://www.openstreetmap.org/search?query=${encodeURIComponent(query)}`;
  }
  if (!Number.isFinite(lat) || !Number.isFinite(lon)) fail('No coordinates.', 'invalid-argument');
  const at = `${lat}/${lon}`;
  return engine
    ? `https://www.openstreetmap.org/directions?engine=${engine}&route=%3B${lat}%2C${lon}#map=15/${at}`
    : `https://www.openstreetmap.org/?mlat=${lat}&mlon=${lon}#map=15/${at}`;
}

const WEB = ['http:', 'https:'];
const PROTOCOL_HANDLERS = ['mailto:', 'tel:', 'sms:'];

function schemeOf(url: string): string | null {
  try {
    return new URL(url).protocol;
  } catch {
    return null;
  }
}

/** Opens a web page in a new tab; false when the popup blocker stopped it. The opened page cannot reach this one. */
export function openInNewTab(url: string): boolean {
  if (!WEB.includes(schemeOf(url) ?? '')) fail('Only http and https pages are opened.', 'invalid-argument');
  const w = g.window?.open('about:blank', '_blank');
  if (!w) return false;
  w.opener = null;
  w.location.href = url;
  return true;
}

function navigate(url: string): boolean {
  if (!PROTOCOL_HANDLERS.includes(schemeOf(url) ?? '')) fail('Not a protocol handler.', 'invalid-argument');
  g.window?.location.assign(url);
  return true;
}

// A blob: URL has the origin of the app: these types run script there, so they are downloaded, never opened.
const ACTIVE = /^(text\/html|application\/xhtml\+xml|image\/svg\+xml|text\/xml|application\/xml|text\/javascript|application\/javascript|application\/x-javascript)\b/i;

function show(file: File): boolean {
  const doc = g.document;
  if (!doc) fail('No document.');
  const active = ACTIVE.test(file.type);
  const url = URL.createObjectURL(active ? new Blob([file], { type: 'application/octet-stream' }) : file);
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
  if (active) {
    const a = doc.createElement('a');
    a.href = url;
    a.download = file.name;
    a.rel = 'noopener';
    doc.body.appendChild(a);
    a.click();
    a.remove();
    return true;
  }
  const w = g.window?.open(url, '_blank');
  if (!w) return false;
  w.opener = null;
  return true;
}

export function phoneDialerService(): EssentialsService {
  return {
    state: () => ({ isSupported: isMobile() }),
    methods: { open: (number: unknown) => navigate(`tel:${String(number ?? '').replace(/[^\d+*#,;]/g, '')}`) },
  };
}

export function smsService(): EssentialsService {
  return {
    state: () => ({ isComposeSupported: isMobile() }),
    methods: { compose: (message: unknown) => navigate(smsUrl((message ?? {}) as Record<string, unknown>)) },
  };
}

export function emailService(): EssentialsService {
  return {
    state: () => ({ isComposeSupported: true }),
    methods: {
      compose(message: unknown) {
        const m = (message ?? {}) as Record<string, unknown>;
        if (Array.isArray(m['attachments']) && m['attachments'].length > 0) fail('A mailto: link cannot carry attachments.');
        if (m['bodyFormat'] === 'Html') fail('A mailto: link cannot carry an HTML body.');
        return navigate(mailtoUrl(m));
      },
    },
  };
}

export function contactsService(): EssentialsService {
  return {
    methods: {
      async select(multiple: unknown) {
        const nav = g.navigator as Navigator & { contacts?: { select(p: string[], o: { multiple: boolean }): Promise<{ name?: string[]; email?: string[]; tel?: string[] }[]> } };
        if (!nav?.contacts || !('ContactsManager' in (g as object))) fail('The Contact Picker API is not available (Chrome on Android only).');
        try {
          const picked = await nav.contacts.select(['name', 'email', 'tel'], { multiple: multiple === true });
          return picked.map(c => ({ givenName: c.name?.[0] ?? '', displayName: c.name?.[0] ?? '', phones: c.tel ?? [], emails: c.email ?? [] }));
        } catch (error) {
          domError(error);
        }
      },
    },
  };
}

export function browserService(): EssentialsService {
  return { methods: { open: (request: unknown) => openInNewTab(String((request as { uri?: unknown } | null)?.uri ?? '')) } };
}

export function launcherService(files: SessionFiles): EssentialsService {
  const allowed = (uri: unknown) => [...WEB, ...PROTOCOL_HANDLERS].includes(schemeOf(String(uri)) ?? '');
  return {
    methods: {
      canOpen: (uri: unknown) => allowed(uri),
      open(uri: unknown) {
        const url = String(uri);
        if (!allowed(url)) return false;
        return WEB.includes(schemeOf(url)!) ? openInNewTab(url) : navigate(url);
      },
      async openFile(request: unknown) {
        return show(await files.download((request as { file: FileLink }).file));
      },
    },
  };
}

export function mapService(): EssentialsService {
  return { methods: { open: (request: unknown) => openInNewTab(mapUrl((request ?? {}) as Record<string, unknown>)) } };
}

export function shareService(files: SessionFiles): EssentialsService {
  const share = async (data: ShareData) => {
    const nav = g.navigator;
    if (typeof nav?.share !== 'function') fail('The Web Share API is not available in this browser.');
    if (typeof nav.canShare === 'function' && !nav.canShare(data)) fail('This browser cannot share that.');
    try {
      await nav.share(data);
    } catch (error) {
      // The user closed the sheet: MAUI ends the request without an error.
      if ((error as { name?: string } | null)?.name === 'AbortError') return;
      domError(error);
    }
  };
  return {
    methods: {
      shareText(request: unknown) {
        const r = (request ?? {}) as Record<string, string | null | undefined>;
        const data: ShareData = {};
        if (r['title']) data.title = r['title'];
        if (r['text'] || r['subject']) data.text = [r['subject'], r['text']].filter(Boolean).join('\n');
        if (r['uri']) data.url = r['uri'];
        return share(data);
      },
      async shareFiles(request: unknown) {
        const r = (request ?? {}) as { title?: string | null; files?: FileLink[] };
        const shared = await Promise.all((r.files ?? []).map(f => files.download(f)));
        return share(r.title ? { title: r.title, files: shared } : { files: shared });
      },
    },
  };
}

export function clipboardService(): EssentialsService {
  const clipboard = () => g.navigator?.clipboard ?? fail('No clipboard in this context (it needs HTTPS).');
  return {
    state: () => ({ hasText: false }),
    methods: {
      async getText() {
        try { return await clipboard().readText(); } catch (error) { domError(error); }
      },
      async setText(text: unknown) {
        try { await clipboard().writeText(String(text ?? '')); } catch (error) { domError(error); }
      },
    },
    events: {
      changed(emit) {
        const handler = () => emit({ hasText: true });
        g.document?.addEventListener('copy', handler);
        g.document?.addEventListener('cut', handler);
        return () => {
          g.document?.removeEventListener('copy', handler);
          g.document?.removeEventListener('cut', handler);
        };
      },
    },
  };
}

// ---------------------------------------------------------------- location

function toLocation(position: GeolocationPosition) {
  const c = position.coords;
  return {
    latitude: c.latitude, longitude: c.longitude, altitude: c.altitude, accuracy: c.accuracy, verticalAccuracy: c.altitudeAccuracy,
    speed: c.speed, course: c.heading, timestamp: position.timestamp,
  };
}

function geolocationError(error: GeolocationPositionError): EssentialsError {
  switch (error.code) {
    case 1: return new EssentialsError(error.message || 'Location refused.', 'permission-denied');
    case 2: return new EssentialsError(error.message || 'Position unavailable.', 'not-enabled');
    default: return new EssentialsError(error.message || 'Timed out.', 'timeout');
  }
}

export function geolocationService(): EssentialsService {
  let last: ReturnType<typeof toLocation> | null = null;
  let watch: number | null = null;
  let changed: ((payload: unknown) => void) | null = null;
  let failed: ((payload: unknown) => void) | null = null;
  const api = () => {
    if (!g.navigator?.geolocation) fail('This browser has no geolocation.');
    if (g.isSecureContext === false) fail('Geolocation needs HTTPS.');
    return g.navigator.geolocation;
  };
  const accurate = (accuracy: unknown) => accuracy === 'High' || accuracy === 'Best';
  return {
    async state() {
      if (!g.navigator?.geolocation || g.isSecureContext === false) return { isEnabled: false };
      try {
        const status = await g.navigator.permissions?.query({ name: 'geolocation' });
        return { isEnabled: status?.state !== 'denied' };
      } catch {
        return { isEnabled: true };
      }
    },
    methods: {
      getLastKnown: () => last,
      getLocation(request: unknown) {
        const r = (request ?? {}) as { desiredAccuracy?: string; timeout?: number | null };
        return new Promise((resolve, reject) => {
          api().getCurrentPosition(
            position => resolve((last = toLocation(position))),
            error => reject(geolocationError(error)),
            { enableHighAccuracy: accurate(r.desiredAccuracy), timeout: r.timeout && r.timeout > 0 ? r.timeout : Infinity, maximumAge: 0 },
          );
        });
      },
      startListening(request: unknown) {
        const r = (request ?? {}) as { desiredAccuracy?: string };
        if (watch !== null) api().clearWatch(watch);
        watch = api().watchPosition(
          position => changed?.((last = toLocation(position))),
          error => {
            failed?.({ error: error.code === 1 ? 'Unauthorized' : 'PositionUnavailable' });
            if (watch !== null) api().clearWatch(watch);
            watch = null;
          },
          { enableHighAccuracy: accurate(r.desiredAccuracy) },
        );
        return true;
      },
      stopListening() {
        if (watch !== null) g.navigator?.geolocation?.clearWatch(watch);
        watch = null;
      },
    },
    events: {
      locationChanged(emit) { changed = emit; return () => { if (changed === emit) changed = null; }; },
      listeningFailed(emit) { failed = emit; return () => { if (failed === emit) failed = null; }; },
    },
  };
}

/** `IGeocoding` through Nominatim (`/search` and `/reverse`): give it your instance, or a proxy of your server. */
export function geocodingService(baseUrl: string, send: typeof fetch = fetch): EssentialsService {
  const base = baseUrl.endsWith('/') ? baseUrl : baseUrl + '/';
  const get = async (path: string) => {
    const response = await send(base + path, { headers: { Accept: 'application/json' } });
    if (!response.ok) fail(`The geocoding service answered ${response.status}.`, response.status === 429 ? 'timeout' : 'failed');
    return response.json();
  };
  return {
    methods: {
      async getPlacemarks(latitude: unknown, longitude: unknown) {
        const data = await get(`reverse?format=json&addressdetails=1&lat=${Number(latitude)}&lon=${Number(longitude)}`) as { lat?: string; lon?: string; name?: string; address?: Record<string, string> };
        if (data?.lat === undefined) return [];
        const a = data.address ?? {};
        return [{
          location: { latitude: Number(data.lat), longitude: Number(data.lon) },
          countryCode: (a['country_code'] ?? '').toUpperCase(), countryName: a['country'] ?? '', featureName: a['road'] ?? data.name ?? '',
          postalCode: a['postcode'] ?? '', locality: a['city'] ?? a['town'] ?? a['village'] ?? '', subLocality: a['suburb'] ?? '',
          thoroughfare: a['road'] ?? '', subThoroughfare: a['house_number'] ?? '', adminArea: a['state'] ?? '', subAdminArea: a['county'] ?? '',
        }];
      },
      async getLocations(address: unknown) {
        const data = await get(`search?format=json&q=${encodeURIComponent(String(address ?? ''))}`) as { lat: string; lon: string }[];
        return (Array.isArray(data) ? data : []).map(d => ({ latitude: Number(d.lat), longitude: Number(d.lon) }));
      },
    },
  };
}

// ---------------------------------------------------------------- sensors

const DEG = Math.PI / 180;
const GRAVITY = 9.80665;
// A reading crosses the network: never more than 20 per second, whatever the speed asked.
const INTERVALS: Record<string, number> = { Default: 200, UI: 66, Game: 50, Fastest: 50 };

type SensorKind = 'accelerometer' | 'gyroscope' | 'compass' | 'orientationSensor' | 'magnetometer' | 'barometer';

function sensorSupported(kind: SensorKind): boolean {
  switch (kind) {
    case 'accelerometer':
    case 'gyroscope':
      return 'DeviceMotionEvent' in g;
    case 'compass':
    case 'orientationSensor':
      return 'DeviceOrientationEvent' in g;
    case 'magnetometer':
      return 'Magnetometer' in g;
    default:
      return false;   // no barometer in browsers
  }
}

function quaternion(e: DeviceOrientationEvent) {
  const x = (e.beta ?? 0) * DEG / 2, y = (e.gamma ?? 0) * DEG / 2, z = (e.alpha ?? 0) * DEG / 2;
  const cX = Math.cos(x), cY = Math.cos(y), cZ = Math.cos(z), sX = Math.sin(x), sY = Math.sin(y), sZ = Math.sin(z);
  return { x: sX * cY * cZ - cX * sY * sZ, y: cX * sY * cZ + sX * cY * sZ, z: cX * cY * sZ + sX * sY * cZ, w: cX * cY * cZ - sX * sY * sZ };
}

export function sensorService(kind: SensorKind): EssentialsService {
  let emit: ((payload: unknown) => void) | null = null;
  let stop: (() => void) | null = null;
  return {
    state: () => ({ isSupported: sensorSupported(kind) }),
    methods: {
      async start(request: unknown) {
        if (!sensorSupported(kind)) fail(`This browser has no ${kind}.`);
        stop?.();
        const interval = INTERVALS[String((request as { speed?: unknown } | null)?.speed)] ?? 200;
        let lastSent = 0;
        const send = (reading: unknown) => {
          const now = Date.now();
          if (now - lastSent < interval) return;
          lastSent = now;
          emit?.(reading);
        };
        const motion = kind === 'accelerometer' || kind === 'gyroscope';
        const ctor = (motion ? g.DeviceMotionEvent : g.DeviceOrientationEvent) as unknown as { requestPermission?: () => Promise<string> } | undefined;
        // iOS asks the user: it only works from a gesture, which a call of the server is not. Ask once from a button, then start.
        if (typeof ctor?.requestPermission === 'function' && (await ctor.requestPermission().catch(() => 'denied')) !== 'granted')
          fail('The motion sensors were refused (iOS asks from a user gesture).', 'permission-denied');

        if (kind === 'magnetometer') {
          const Sensor = (g as unknown as { Magnetometer: new (o: { frequency: number }) => EventTarget & { x?: number; y?: number; z?: number; start(): void; stop(): void } }).Magnetometer;
          const sensor = new Sensor({ frequency: 1000 / interval });
          const handler = () => send({ x: sensor.x ?? 0, y: sensor.y ?? 0, z: sensor.z ?? 0 });
          sensor.addEventListener('reading', handler);
          sensor.start();
          stop = () => { sensor.removeEventListener('reading', handler); sensor.stop(); };
          return;
        }
        const eventName = motion ? 'devicemotion' : kind === 'compass' && 'ondeviceorientationabsolute' in g ? 'deviceorientationabsolute' : 'deviceorientation';
        const handler = (event: Event) => {
          if (kind === 'accelerometer') {
            const a = (event as DeviceMotionEvent).accelerationIncludingGravity;
            if (a) send({ x: (a.x ?? 0) / GRAVITY, y: (a.y ?? 0) / GRAVITY, z: (a.z ?? 0) / GRAVITY });
          } else if (kind === 'gyroscope') {
            const r = (event as DeviceMotionEvent).rotationRate;
            if (r) send({ x: (r.beta ?? 0) * DEG, y: (r.gamma ?? 0) * DEG, z: (r.alpha ?? 0) * DEG });
          } else if (kind === 'orientationSensor') {
            send(quaternion(event as DeviceOrientationEvent));
          } else {
            const e = event as DeviceOrientationEvent & { webkitCompassHeading?: number };
            const heading = typeof e.webkitCompassHeading === 'number' ? e.webkitCompassHeading : e.absolute && e.alpha !== null ? (360 - e.alpha) % 360 : null;
            if (heading !== null) send({ heading });
          }
        };
        g.window?.addEventListener(eventName, handler);
        stop = () => g.window?.removeEventListener(eventName, handler);
      },
      stop() {
        stop?.();
        stop = null;
      },
    },
    events: { reading(e) { emit = e; return () => { if (emit === e) emit = null; }; } },
  };
}

// ---------------------------------------------------------------- media

export function textToSpeechService(): EssentialsService {
  const synth = () => g.window?.speechSynthesis ?? fail('This browser has no speech synthesis.');
  const voices = () => new Promise<SpeechSynthesisVoice[]>(resolve => {
    const list = synth().getVoices();
    if (list.length > 0) return resolve(list);
    // Chrome loads the voices after the first call.
    synth().addEventListener('voiceschanged', () => resolve(synth().getVoices()), { once: true });
    setTimeout(() => resolve(synth().getVoices()), 1000);
  });
  return {
    methods: {
      async getLocales() {
        return (await voices()).map(v => {
          const [language, country] = v.lang.split(/[-_]/);
          return { language: language ?? '', country: country ?? '', name: v.name, id: v.voiceURI };
        });
      },
      async speak(request: unknown) {
        const r = (request ?? {}) as { text?: string; locale?: { id?: string; language?: string; country?: string } | null; pitch?: number | null; volume?: number | null; rate?: number | null };
        const utterance = new SpeechSynthesisUtterance(String(r.text ?? ''));
        if (r.locale) {
          const voice = (await voices()).find(v => v.voiceURI === r.locale!.id);
          if (voice) utterance.voice = voice;
          utterance.lang = voice?.lang ?? [r.locale.language, r.locale.country].filter(Boolean).join('-');
        }
        if (typeof r.pitch === 'number') utterance.pitch = r.pitch;
        if (typeof r.volume === 'number') utterance.volume = r.volume;
        if (typeof r.rate === 'number') utterance.rate = r.rate;
        await new Promise<void>((resolve, reject) => {
          utterance.onend = () => resolve();
          utterance.onerror = e => (e.error === 'canceled' || e.error === 'interrupted' ? resolve() : reject(new EssentialsError(e.error, 'failed')));
          synth().speak(utterance);
        });
      },
      cancel: () => synth().cancel(),
    },
  };
}

export function filePickerService(files: SessionFiles): EssentialsService {
  return {
    methods: {
      async pick(request: unknown) {
        const r = (request ?? {}) as { title?: string | null; accept?: string | null; multiple?: boolean; upload: string };
        const chosen = await chooseFiles({ accept: r.accept, multiple: r.multiple, title: r.title });
        return chosen === null ? null : files.uploadAll(r.upload, chosen);
      },
    },
  };
}

export function mediaPickerService(files: SessionFiles): EssentialsService {
  const accept = (kind: unknown) => (kind === 'video' ? 'video/*' : 'image/*');
  return {
    state: () => ({ isCaptureSupported: isMobile() || !!g.navigator?.mediaDevices?.getUserMedia }),
    methods: {
      async pick(request: unknown) {
        const r = (request ?? {}) as { title?: string | null; kind?: string; multiple?: boolean; limit?: number; upload: string };
        const chosen = await chooseFiles({ accept: accept(r.kind), multiple: r.multiple, title: r.title });
        return chosen === null ? null : files.uploadAll(r.upload, chosen.slice(0, Math.max(1, r.limit ?? chosen.length)));
      },
      async capture(request: unknown) {
        // The capture attribute opens the camera on a phone; a desktop browser shows the file dialog instead.
        const r = (request ?? {}) as { title?: string | null; kind?: string; upload: string };
        const chosen = await chooseFiles({ accept: accept(r.kind), capture: 'environment', title: r.title });
        return chosen === null ? null : files.uploadAll(r.upload, chosen.slice(0, 1));
      },
    },
  };
}

export function screenshotService(files: SessionFiles): EssentialsService {
  const supported = () => typeof g.navigator?.mediaDevices?.getDisplayMedia === 'function';
  return {
    state: () => ({ isCaptureSupported: supported() }),
    methods: {
      async capture(request: unknown) {
        if (!supported()) fail('This browser cannot capture its screen.');
        let stream: MediaStream;
        try {
          stream = await g.navigator!.mediaDevices.getDisplayMedia({ video: true });
        } catch (error) {
          domError(error);
        }
        try {
          const video = g.document!.createElement('video');
          video.srcObject = stream;
          video.muted = true;
          await video.play();
          const canvas = g.document!.createElement('canvas');
          canvas.width = video.videoWidth;
          canvas.height = video.videoHeight;
          canvas.getContext('2d')!.drawImage(video, 0, 0);
          const png = await new Promise<Blob>((resolve, reject) => canvas.toBlob(b => (b ? resolve(b) : reject(new EssentialsError('No image.', 'failed'))), 'image/png'));
          const file = await files.upload((request as { upload: string }).upload, png, 'screenshot.png');
          return { file, width: canvas.width, height: canvas.height };
        } finally {
          stream.getTracks().forEach(t => t.stop());
        }
      },
    },
  };
}

// ---------------------------------------------------------------- secure storage

/** Where `secureStorageService` keeps its AES key (IndexedDB by default): replaceable in tests. */
export interface SecureKeyStore {
  /** The key, created once: two tabs that create it at the same time end up with the same one. */
  getOrCreate(create: () => Promise<CryptoKey>): Promise<CryptoKey>;
}

/** The key in IndexedDB, created in a readwrite transaction that checks again for an existing one (transactions are serialized across tabs). */
export function indexedDbKeyStore(database = 'routeables-securestorage'): SecureKeyStore {
  let cached: Promise<CryptoKey> | null = null;
  const open = () => new Promise<IDBDatabase>((resolve, reject) => {
    const request = g.indexedDB?.open(database, 1);
    if (!request) return reject(new EssentialsError('No IndexedDB in this context.', 'not-supported'));
    request.onupgradeneeded = () => request.result.createObjectStore('keys');
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
  return {
    getOrCreate(create) {
      cached ??= (async () => {
        const db = await open();
        const candidate = await create();
        return new Promise<CryptoKey>((resolve, reject) => {
          const tx = db.transaction('keys', 'readwrite');
          const store = tx.objectStore('keys');
          let key = candidate;
          const read = store.get('aes');
          read.onsuccess = () => {
            if (read.result) key = read.result as CryptoKey;
            else store.add(candidate, 'aes');
          };
          tx.oncomplete = () => resolve(key);
          tx.onerror = tx.onabort = () => reject(tx.error);
        });
      })();
      cached.catch(() => { cached = null; });
      return cached;
    },
  };
}

/**
 * `ISecureStorage` (service `secureStorage`): AES-GCM in a Storage (localStorage by default) with a key that cannot be exported. It protects
 * the values at rest; any script running on the origin can still use the key.
 */
export function secureStorageService(
  storage: Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'> | undefined = globalThis.localStorage,
  keys: SecureKeyStore = indexedDbKeyStore(),
  prefix = 'routeables:secure:',
): EssentialsService {
  const subtle = () => globalThis.crypto?.subtle ?? fail('No WebCrypto in this context (it needs HTTPS).');
  const key = () => keys.getOrCreate(() => subtle().generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']) as Promise<CryptoKey>);
  const store = () => storage ?? fail('No storage is available.');
  const names = () => {
    const found: string[] = [];
    for (let i = 0; i < (storage?.length ?? 0); i++) {
      const k = storage!.key(i);
      if (k?.startsWith(prefix)) found.push(k.slice(prefix.length));
    }
    return found;
  };
  const text = (value: unknown) => (typeof value === 'string' && value.length > 0 ? value : fail('The key must be a string.', 'invalid-argument'));
  return {
    state: () => ({ keys: names() }),
    methods: {
      async get(name: unknown) {
        const raw = store().getItem(prefix + text(name));
        if (raw === null) return null;
        const aes = await key();
        try {
          const { iv, data } = JSON.parse(raw) as { iv: number[]; data: number[] };
          return new TextDecoder().decode(await subtle().decrypt({ name: 'AES-GCM', iv: new Uint8Array(iv) }, aes, new Uint8Array(data)));
        } catch {
          // Encrypted with a key that is gone (site data partly cleared), or corrupted: as on the platforms, it reads as missing.
          store().removeItem(prefix + String(name));
          return null;
        }
      },
      async set(name: unknown, value: unknown) {
        const aes = await key();
        const iv = globalThis.crypto.getRandomValues(new Uint8Array(12));
        const data = await subtle().encrypt({ name: 'AES-GCM', iv }, aes, new TextEncoder().encode(String(value ?? '')));
        store().setItem(prefix + text(name), JSON.stringify({ iv: [...iv], data: [...new Uint8Array(data)] }));
      },
      remove(name: unknown) {
        store().removeItem(prefix + text(name));
      },
      removeAll() {
        for (const name of names()) store().removeItem(prefix + name);
      },
    },
  };
}

// ---------------------------------------------------------------- authentication

/**
 * `IWebAuthenticator` (service `webAuthenticator`): opens the sign-in page in a popup and waits for it to reach the callback URL, a page of
 * the same origin as this app (the server checks the address too). Ends as cancelled when the user closes the popup.
 */
export function webAuthenticatorService(timeoutMs = 5 * 60_000): EssentialsService {
  return {
    methods: {
      authenticate(request: unknown) {
        const r = (request ?? {}) as { url?: string; callbackUrl?: string };
        const url = String(r.url ?? '');
        if (!WEB.includes(schemeOf(url) ?? '')) fail('The sign-in page must be http(s).', 'invalid-argument');
        const callback = new URL(String(r.callbackUrl ?? ''), g.document?.baseURI);
        if (g.window && callback.origin !== g.window.location.origin) fail('The callback URL must be on the origin of this app.', 'invalid-argument');
        return new Promise<string>((resolve, reject) => {
          const popup = g.window?.open('about:blank', 'routeables-authentication', 'width=520,height=700');
          if (!popup) return reject(new EssentialsError('The sign-in popup was blocked.', 'permission-denied'));
          const started = Date.now();
          const timer = setInterval(() => {
            if (popup.closed) return end(new EssentialsError('The sign-in window was closed.', 'cancelled'));
            if (Date.now() - started > timeoutMs) return end(new EssentialsError('The sign-in took too long.', 'timeout'));
            let href: string;
            try {
              href = popup.location.href;   // readable once the popup is back on this origin
            } catch {
              return;
            }
            if (href.startsWith(callback.origin + callback.pathname)) end(null, href);
          }, 250);
          const end = (error: EssentialsError | null, href?: string) => {
            clearInterval(timer);
            try { popup.close(); } catch { /* already closed */ }
            if (error) reject(error);
            else resolve(href!);
          };
          popup.location.href = url;
        });
      },
    },
  };
}

// ---------------------------------------------------------------- all of them

/**
 * Every device API a browser offers, for the view models of a Routeables.SignalR.Essentials server. A service the browser lacks entirely
 * (app actions, barometer) is left out or says `isSupported: false`; geocoding only comes with `nominatim`.
 */
export function browserEssentials(options: BrowserEssentialsOptions = {}): EssentialsServices {
  const files = new SessionFiles(options);
  const services: Record<string, EssentialsService> = {
    appInfo: appInfoService(options.app),
    deviceInfo: deviceInfoService(),
    deviceDisplay: deviceDisplayService(),
    battery: batteryService(),
    connectivity: connectivityService(),
    vibration: vibrationService(),
    hapticFeedback: hapticFeedbackService(),
    flashlight: flashlightService(),
    semanticScreenReader: semanticScreenReaderService(),
    phoneDialer: phoneDialerService(),
    sms: smsService(),
    email: emailService(),
    contacts: contactsService(),
    browser: browserService(),
    launcher: launcherService(files),
    map: mapService(),
    share: shareService(files),
    clipboard: clipboardService(),
    geolocation: geolocationService(),
    accelerometer: sensorService('accelerometer'),
    barometer: sensorService('barometer'),
    compass: sensorService('compass'),
    gyroscope: sensorService('gyroscope'),
    magnetometer: sensorService('magnetometer'),
    orientationSensor: sensorService('orientationSensor'),
    textToSpeech: textToSpeechService(),
    filePicker: filePickerService(files),
    mediaPicker: mediaPickerService(files),
    screenshot: screenshotService(files),
    webAuthenticator: webAuthenticatorService(),
  };
  try {
    if (globalThis.localStorage !== undefined) {
      services['preferences'] = preferencesService(globalThis.localStorage);
      services['secureStorage'] = secureStorageService(globalThis.localStorage);
    }
  } catch {
    // localStorage throws in some privacy modes: no preferences then.
  }
  if (options.nominatim) services['geocoding'] = geocodingService(options.nominatim, options.fetch);
  return services;
}
