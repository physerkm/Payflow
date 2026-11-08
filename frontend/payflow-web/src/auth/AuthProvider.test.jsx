import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthProvider, useAuth } from './AuthProvider';
import { authApi } from '../api/endpoints';
import { lastTenant } from '../api/client';

vi.mock('../api/client', () => ({
  configureSessionHandlers: vi.fn(),
  refreshSession: vi.fn(() => Promise.reject(new Error('no session'))),
  tokenStore: { set: vi.fn(), clear: vi.fn() },
  lastTenant: { get: vi.fn(() => null), set: vi.fn() },
}));

vi.mock('../api/endpoints', () => ({
  authApi: { login: vi.fn(), memberships: vi.fn(), activateTenant: vi.fn(), logout: vi.fn(), register: vi.fn() },
}));

const user = { id: 'u1', email: 'demo@payflow.local', displayName: 'Demo' };
const session = (tenant = null) => ({ accessToken: 't', expiresAt: null, user, tenant });
const membership = (tenantId, overrides = {}) => ({ tenantId, tenantName: tenantId, status: 'Active', tenantStatus: 'Active', ...overrides });

async function signIn() {
  let auth;
  const Probe = () => {
    auth = useAuth();
    return null;
  };
  render(
    <QueryClientProvider client={new QueryClient()}>
      <AuthProvider><Probe /></AuthProvider>
    </QueryClientProvider>,
  );
  await waitFor(() => expect(auth.status).toBe('anonymous'));
  let tenant;
  await act(async () => {
    tenant = await auth.login({ email: user.email, password: 'x' });
  });
  return tenant;
}

describe('AuthProvider login', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    lastTenant.get.mockReturnValue(null);
    authApi.login.mockResolvedValue(session());
  });

  it('opens the only company automatically when none is remembered', async () => {
    authApi.memberships.mockResolvedValue([membership('acme')]);
    authApi.activateTenant.mockResolvedValue(session({ tenantId: 'acme' }));

    expect(await signIn()).toEqual({ tenantId: 'acme' });
    expect(authApi.activateTenant).toHaveBeenCalledWith('acme');
  });

  it('leaves the choice to the user when they belong to several companies', async () => {
    authApi.memberships.mockResolvedValue([membership('acme'), membership('globex')]);

    expect(await signIn()).toBeNull();
    expect(authApi.activateTenant).not.toHaveBeenCalled();
  });

  it('ignores suspended companies and disabled memberships', async () => {
    authApi.memberships.mockResolvedValue([membership('acme', { tenantStatus: 'Suspended' }), membership('globex', { status: 'Disabled' })]);

    expect(await signIn()).toBeNull();
    expect(authApi.activateTenant).not.toHaveBeenCalled();
  });

  it('prefers the remembered company', async () => {
    lastTenant.get.mockReturnValue('globex');
    authApi.activateTenant.mockResolvedValue(session({ tenantId: 'globex' }));

    expect(await signIn()).toEqual({ tenantId: 'globex' });
    expect(authApi.memberships).not.toHaveBeenCalled();
  });
});
