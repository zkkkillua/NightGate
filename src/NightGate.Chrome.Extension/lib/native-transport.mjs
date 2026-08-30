import {
  decodeNativeResponse,
  encodeNativeRequest,
  parsePolicy,
} from './codec.mjs';

export const NATIVE_HOST_NAME = 'com.nightgate.host';

const PROFILE_TOKEN = /^[A-Za-z0-9_-]{43}$/;
const DEFAULT_TIMEOUT_MS = 8_000;

function failOpenError(message, cause) {
  const error = new Error(message, cause === undefined ? undefined : { cause });
  error.failOpen = true;
  return error;
}

function withTimeout(operation, timeoutMs) {
  let timeoutId;
  const timeout = new Promise((_, reject) => {
    timeoutId = setTimeout(
      () => reject(failOpenError('Native messaging request timeout')),
      timeoutMs,
    );
  });
  return Promise.race([operation, timeout]).finally(() => clearTimeout(timeoutId));
}

export function createNativeTransport(chromeApi, options = {}) {
  const sendNativeMessage = chromeApi?.runtime?.sendNativeMessage;
  const profileToken = options.profileToken;
  const requestIdFactory = options.requestIdFactory
    ?? (() => globalThis.crypto.randomUUID());
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  if (typeof sendNativeMessage !== 'function'
      || typeof profileToken !== 'string' || !PROFILE_TOKEN.test(profileToken)
      || typeof requestIdFactory !== 'function'
      || !Number.isFinite(timeoutMs) || !Number.isInteger(timeoutMs)
      || timeoutMs < 1 || timeoutMs > 30_000) {
    throw new TypeError('invalid native transport dependencies');
  }

  const request = async (type, payload) => {
    const requestId = requestIdFactory();
    const encoded = encodeNativeRequest({ type, requestId, profileToken, payload });
    const message = JSON.parse(encoded);
    let response;
    try {
      response = await withTimeout(
        Promise.resolve().then(() => sendNativeMessage.call(
          chromeApi.runtime,
          NATIVE_HOST_NAME,
          message,
        )),
        timeoutMs,
      );
    } catch (error) {
      if (error?.failOpen === true || error instanceof TypeError) throw error;
      throw failOpenError('Native messaging host unavailable', error);
    }

    return decodeNativeResponse(JSON.stringify(response), {
      requestType: type,
      requestId,
      profileToken,
    });
  };

  return Object.freeze({
    async getPolicy(context = {}) {
      if (context.profileToken !== profileToken
          || !Number.isSafeInteger(context.minimumRevision)
          || context.minimumRevision < -1) {
        throw new TypeError('invalid policy request context');
      }
      const response = await request('getPolicy', {});
      return parsePolicy(response.payload, {
        minimumRevision: context.minimumRevision,
      });
    },

    async send(type, payload) {
      const response = await request(type, payload);
      return response.payload.accepted;
    },
  });
}
