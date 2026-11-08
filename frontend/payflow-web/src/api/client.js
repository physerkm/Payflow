import axios from 'axios';

// Access token lives in memory only (never localStorage) to limit XSS impact. The refresh token is an
// HttpOnly, SameSite=Strict cookie that JavaScript cannot read; /auth/refresh rotates it.
let accessToken = null;
let refreshInFlight = null;
const handlers = { onExpired: () => {}, onRefreshed: () => {} };

const LAST_TENANT_KEY = 'payflow.lastTenantId';

export const tokenStore = {
  get: () => accessToken,
  set: (token) => {
    accessToken = token;
  },
  clear: () => {
    accessToken = null;
  },
};

// The last tenant id is not a secret (it only pre-selects the tenant; the server re-validates membership).
export const lastTenant = {
  get: () => {
    try {
      return localStorage.getItem(LAST_TENANT_KEY);
    } catch {
      return null;
    }
  },
  set: (id) => {
    try {
      if (id) localStorage.setItem(LAST_TENANT_KEY, id);
      else localStorage.removeItem(LAST_TENANT_KEY);
    } catch {
      /* storage unavailable */
    }
  },
};

export function configureSessionHandlers({ onExpired, onRefreshed }) {
  handlers.onExpired = onExpired;
  handlers.onRefreshed = onRefreshed;
}

export class ApiError extends Error {
  constructor({ status, code, title, detail, errors, correlationId }) {
    super(detail || title || 'Request failed');
    this.status = status;
    this.code = code;
    this.title = title;
    this.errors = errors ?? {};
    this.correlationId = correlationId;
  }
}

function toApiError(error) {
  if (error instanceof ApiError) return error;
  const response = error.response;
  if (!response) {
    return new ApiError({ status: 0, code: 'network_error', title: 'Network error', detail: 'The server could not be reached. Check your connection.' });
  }
  const data = response.data && typeof response.data === 'object' ? response.data : {};
  return new ApiError({
    status: response.status,
    code: data.code ?? `http_${response.status}`,
    title: data.title,
    detail: data.detail ?? data.title ?? response.statusText,
    errors: data.errors,
    correlationId: data.correlationId ?? response.headers?.['x-correlation-id'],
  });
}

export const http = axios.create({
  baseURL: '/api/v1',
  withCredentials: true,
  timeout: 20000,
  headers: { 'Content-Type': 'application/json' },
});

http.interceptors.request.use((config) => {
  if (accessToken) config.headers.Authorization = `Bearer ${accessToken}`;
  return config;
});

const CREDENTIAL_ENDPOINTS = ['/auth/login', '/auth/register', '/auth/refresh', '/auth/logout'];

http.interceptors.response.use(
  (response) => response,
  async (error) => {
    const original = error.config;
    const isCredentialCall = CREDENTIAL_ENDPOINTS.some((path) => original?.url?.startsWith(path));
    if (error.response?.status === 401 && original && !original._retried && !isCredentialCall) {
      original._retried = true;
      try {
        await refreshSession();
        original.headers.Authorization = `Bearer ${accessToken}`;
        return http(original);
      } catch {
        handlers.onExpired();
      }
    }
    return Promise.reject(toApiError(error));
  },
);

/** Single-flight refresh: concurrent 401s share one rotation request. */
export function refreshSession(tenantId = lastTenant.get()) {
  if (!refreshInFlight) {
    refreshInFlight = axios
      .post('/api/v1/auth/refresh', tenantId ? { tenantId } : {}, { withCredentials: true, headers: { 'Content-Type': 'application/json' } })
      .then((response) => {
        accessToken = response.data.accessToken;
        handlers.onRefreshed(response.data);
        return response.data;
      })
      .catch((error) => {
        accessToken = null;
        throw toApiError(error);
      })
      .finally(() => {
        refreshInFlight = null;
      });
  }
  return refreshInFlight;
}

/** Applies server-side field errors (problem details "errors") to a react-hook-form instance. */
export function applyFieldErrors(error, setError) {
  if (!(error instanceof ApiError) || !error.errors) return false;
  let applied = false;
  for (const [field, messages] of Object.entries(error.errors)) {
    if (field && messages?.length) {
      setError(field, { type: 'server', message: messages[0] });
      applied = true;
    }
  }
  return applied;
}

export function errorMessage(error) {
  if (!error) return '';
  if (error instanceof ApiError) {
    const firstField = Object.values(error.errors ?? {})[0]?.[0];
    return firstField ?? error.message;
  }
  return error.message ?? 'Something went wrong.';
}
