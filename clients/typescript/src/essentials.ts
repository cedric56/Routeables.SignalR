import type { EssentialsCallMessage, EssentialsSubscriptionMessage, ServerMethod } from './types.js';

/** The version of the Essentials messages this client speaks (`EssentialsReady`). */
export const ESSENTIALS_PROTOCOL = 1;

/**
 * One device API this client offers to the server (the `IPreferences`, `IConnectivity`… of the view models). Only what is listed here can be
 * reached: the server names a method or an event, it never runs arbitrary code.
 */
export interface EssentialsService {
  /** The state sent with `EssentialsReady`, read again after each reconnection: what the server answers synchronous calls with. */
  state?(): unknown | Promise<unknown>;
  /** The methods the server may call, by name. What they return (or resolve) is the result; what they throw is the error. */
  readonly methods?: Readonly<Record<string, (...args: never[]) => unknown>>;
  /** The events the server may ask for, by name: called with `emit` when the server starts one, returns the way to stop it. */
  readonly events?: Readonly<Record<string, (emit: (payload: unknown) => void) => () => void>>;
}

/** The device APIs of this client, by service name (`preferences`, `connectivity`). */
export type EssentialsServices = Readonly<Record<string, EssentialsService>>;

/** The kinds of failure the server understands (`EssentialsErrorCodes`). */
export type EssentialsErrorCode =
  | 'not-supported' | 'permission-denied' | 'not-enabled' | 'timeout' | 'invalid-argument' | 'failed' | 'cancelled';

/** Thrown by a method to give the server a precise failure. */
export class EssentialsError extends Error {
  constructor(message: string, readonly code: EssentialsErrorCode) {
    super(message);
    this.name = 'EssentialsError';
  }
}

const CODES: readonly string[] = ['not-supported', 'permission-denied', 'not-enabled', 'timeout', 'invalid-argument', 'failed', 'cancelled'];

/** The code of a failure: the one of an `EssentialsError`, the meaning of a DOMException (a refused permission…), `failed` otherwise. */
export function essentialsErrorCode(error: unknown): EssentialsErrorCode {
  const code = (error as { code?: unknown } | null)?.code;
  if (typeof code === 'string' && CODES.includes(code)) return code as EssentialsErrorCode;
  switch ((error as { name?: unknown } | null)?.name) {
    case 'NotAllowedError':
    case 'SecurityError':
      return 'permission-denied';
    case 'NotSupportedError':
      return 'not-supported';
    case 'TimeoutError':
      return 'timeout';
    case 'AbortError':
      return 'cancelled';
    default:
      return 'failed';
  }
}

const own = (object: object | undefined, key: string): boolean => object !== undefined && Object.prototype.hasOwnProperty.call(object, key);

/**
 * Runs the Essentials messages of the server against `services`: what `RouteablesClient` does with its `essentials` option. Separate so that
 * another transport, or a test, can drive it.
 */
export class EssentialsBridge {
  private readonly listeners = new Map<string, () => void>();

  constructor(
    private readonly services: EssentialsServices,
    private readonly call: (method: ServerMethod, ...args: unknown[]) => Promise<unknown>,
  ) {}

  /** The services and their state, as `EssentialsReady` sends them. The states are read together (some are asynchronous: the battery). */
  async announcement(): Promise<{ version: number; capabilities: string[]; state: Record<string, unknown> }> {
    const capabilities = Object.keys(this.services).filter(name => own(this.services, name));
    const state: Record<string, unknown> = {};
    await Promise.all(capabilities.map(async name => {
      const service = this.services[name]!;
      if (typeof service.state !== 'function') return;
      try {
        const value = await service.state();
        if (value !== undefined) state[name] = value;
      } catch {
        // A service that cannot read its state is announced without one: its synchronous members answer with defaults.
      }
    }));
    return { version: ESSENTIALS_PROTOCOL, capabilities, state };
  }

  /**
   * Tells the server what this client supports. Call it after each `Hello`: the events that ran are stopped first, the server asks again for
   * those it still wants (it may have stopped some while the connection was gone).
   */
  async ready(): Promise<{ success: boolean; error?: string | null; code?: string | null }> {
    this.stopAll();
    return (await this.call('EssentialsReady', await this.announcement())) as { success: boolean };
  }

  /** A call of the server: runs the method and answers. */
  async invoke(message: EssentialsCallMessage): Promise<void> {
    let answer: Record<string, unknown>;
    try {
      const method = this.methodOf(message.service, message.method);
      if (method === undefined)
        throw new EssentialsError(`This client does not implement ${message.service}.${message.method}.`, 'not-supported');
      const args = Array.isArray(message.args) ? message.args : [];
      const result = await (method as (...args: unknown[]) => unknown)(...args);
      answer = { id: message.id, success: true, result: result === undefined ? null : result };
    } catch (error) {
      answer = { id: message.id, success: false, error: error instanceof Error ? error.message : String(error), code: essentialsErrorCode(error) };
    }
    try {
      await this.call('EssentialsResult', answer);
    } catch {
      // The connection dropped: a replayable call comes again once the session is resumed, the others end on the server.
    }
  }

  /** The server starts or stops an event. */
  subscribe(message: EssentialsSubscriptionMessage): void {
    const key = `${message.service}\u0000${message.event}`;
    if (!message.active) {
      this.stop(key);
      return;
    }
    if (this.listeners.has(key)) return;
    const service = own(this.services, message.service) ? this.services[message.service] : undefined;
    const start = service?.events !== undefined && own(service.events, message.event) ? service.events[message.event] : undefined;
    if (typeof start !== 'function') return;
    const stop = start(payload => {
      this.call('EssentialsEvent', { service: message.service, event: message.event, payload }).catch(() => {
        // Not connected: the server reads the state again when the session is resumed.
      });
    });
    this.listeners.set(key, stop);
  }

  /** Stops every event (the client is done with the connection). */
  stopAll(): void {
    for (const key of [...this.listeners.keys()]) this.stop(key);
  }

  private stop(key: string): void {
    const stop = this.listeners.get(key);
    this.listeners.delete(key);
    try {
      stop?.();
    } catch {
      // Stopping a browser listener does not fail the client.
    }
  }

  private methodOf(serviceName: string, methodName: string): ((...args: never[]) => unknown) | undefined {
    if (typeof serviceName !== 'string' || typeof methodName !== 'string' || !own(this.services, serviceName)) return undefined;
    const methods = this.services[serviceName]!.methods;
    if (!own(methods, methodName)) return undefined;
    const method = methods![methodName];
    return typeof method === 'function' ? method : undefined;
  }
}

// ---------------------------------------------------------------- browser services

/** The part of the Storage API the preferences use (localStorage, or a stand-in in tests). */
export interface PreferenceStorage {
  readonly length: number;
  key(index: number): string | null;
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

/**
 * `IPreferences` (service `preferences`) in a Storage, `localStorage` by default. The keys are `<prefix><container>:<key>`, the container being
 * the shared name ("" without one), escaped.
 */
export function preferencesService(storage: PreferenceStorage | undefined = globalThis.localStorage, prefix = 'routeables:prefs:'): EssentialsService {
  const storageKey = (container: string, key: string) => `${prefix}${encodeURIComponent(container)}:${key}`;
  const text = (value: unknown, name: string): string => {
    if (typeof value !== 'string') throw new EssentialsError(`The ${name} must be a string.`, 'invalid-argument');
    return value;
  };
  const keysOf = (container: string | null): string[] => {
    const found: string[] = [];
    if (storage === undefined) return found;
    const start = container === null ? prefix : `${prefix}${encodeURIComponent(container)}:`;
    for (let i = 0; i < storage.length; i++) {
      const key = storage.key(i);
      if (key !== null && key.startsWith(start)) found.push(key);
    }
    return found;
  };
  const available = (): PreferenceStorage => {
    if (storage === undefined) throw new EssentialsError('No storage is available.', 'not-supported');
    return storage;
  };

  return {
    state() {
      const containers: Record<string, Record<string, string>> = {};
      for (const key of keysOf(null)) {
        const rest = key.slice(prefix.length);
        const separator = rest.indexOf(':');
        if (separator < 0) continue;
        let container: string;
        try {
          container = decodeURIComponent(rest.slice(0, separator));
        } catch {
          continue;
        }
        const value = storage!.getItem(key);
        if (value !== null) (containers[container] ??= {})[rest.slice(separator + 1)] = value;
      }
      return containers;
    },
    methods: {
      set(container: unknown, key: unknown, value: unknown) {
        available().setItem(storageKey(text(container, 'container'), text(key, 'key')), text(value, 'value'));
      },
      remove(container: unknown, key: unknown) {
        available().removeItem(storageKey(text(container, 'container'), text(key, 'key')));
      },
      clear(container: unknown) {
        const store = available();
        for (const key of keysOf(text(container, 'container'))) store.removeItem(key);
      },
    },
  };
}

interface NetworkInformationLike extends EventTarget {
  readonly type?: string;
  readonly saveData?: boolean;
}

/** `IConnectivity` (service `connectivity`): `navigator.onLine` and, where the browser has it, the Network Information API. */
export function connectivityService(target: (Pick<EventTarget, 'addEventListener' | 'removeEventListener'>) | undefined = globalThis.window): EssentialsService {
  const navigatorLike = globalThis.navigator as (Navigator & { connection?: NetworkInformationLike }) | undefined;
  const read = () => {
    const online = navigatorLike?.onLine ?? true;
    const connection = navigatorLike?.connection;
    const profile = ({ wifi: 'WiFi', cellular: 'Cellular', ethernet: 'Ethernet', bluetooth: 'Bluetooth' } as Record<string, string>)[connection?.type ?? ''];
    return {
      networkAccess: !online ? 'None' : connection?.saveData ? 'ConstrainedInternet' : 'Internet',
      profiles: online ? [profile ?? 'Unknown'] : [],
    };
  };

  return {
    state: read,
    events: {
      changed(emit) {
        const handler = () => emit(read());
        target?.addEventListener('online', handler);
        target?.addEventListener('offline', handler);
        navigatorLike?.connection?.addEventListener('change', handler);
        return () => {
          target?.removeEventListener('online', handler);
          target?.removeEventListener('offline', handler);
          navigatorLike?.connection?.removeEventListener('change', handler);
        };
      },
    },
  };
}
