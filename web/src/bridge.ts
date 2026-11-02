import { invoke, isTauri } from '@tauri-apps/api/core';
import { listen, type UnlistenFn } from '@tauri-apps/api/event';

interface HostEvent {
  type: string;
  payload?: unknown;
}

const listeners = new Map<string, Set<(payload: any) => void>>();
let registration: Promise<void> | undefined;
let unlistenHost: UnlistenFn | undefined;
export const isDesktop = isTauri();

function registerHostEvents(): Promise<void> {
  if (!registration) {
    registration = listen<HostEvent>('host-event', event => {
      const message = event.payload;
      if (!message || typeof message.type !== 'string') return;
      for (const handler of listeners.get(message.type) || []) handler(message.payload);
    }).then(unlisten => { unlistenHost = unlisten; }).catch(cause => {
      registration = undefined;
      throw cause;
    });
  }
  return registration;
}

window.addEventListener('unload', () => unlistenHost?.());

export async function call<T = void>(type: string, payload: unknown = {}): Promise<T> {
  if (!isDesktop) throw new Error('请打开 Weirdo 桌面应用以连接本机终端。');
  try {
    // Every request shares this barrier, including bootstrap and terminal replay.
    // The host can emit output as soon as host_request is invoked.
    await registerHostEvents();
    return await invoke<T>('host_request', { kind: type, payload });
  } catch (cause) {
    throw cause instanceof Error ? cause : new Error(String(cause));
  }
}

export function on<T>(type: string, handler: (payload: T) => void): () => void {
  const handlers = listeners.get(type) || new Set();
  handlers.add(handler);
  listeners.set(type, handlers);
  return () => handlers.delete(handler);
}
