import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { configureSessionHandlers, lastTenant, refreshSession, tokenStore } from '../api/client';
import { authApi } from '../api/endpoints';

const AuthContext = createContext(null);

const ANONYMOUS = { status: 'anonymous', user: null, tenant: null, expiresAt: null };

/**
 * Session state for the SPA. On load the session is restored through the refresh cookie; the access
 * token is kept in memory and proactively renewed shortly before it expires.
 */
export function AuthProvider({ children }) {
  const queryClient = useQueryClient();
  const [session, setSession] = useState({ ...ANONYMOUS, status: 'loading' });
  const renewTimer = useRef(null);

  const scheduleRenewal = useCallback((expiresAt) => {
    clearTimeout(renewTimer.current);
    if (!expiresAt) return;
    const delay = Math.max(5_000, new Date(expiresAt).getTime() - Date.now() - 60_000);
    renewTimer.current = setTimeout(() => {
      refreshSession().catch(() => setSession(ANONYMOUS));
    }, delay);
  }, []);

  const apply = useCallback(
    (data) => {
      tokenStore.set(data.accessToken);
      if (data.tenant) lastTenant.set(data.tenant.tenantId);
      setSession({ status: 'authenticated', user: data.user, tenant: data.tenant ?? null, expiresAt: data.expiresAt });
      scheduleRenewal(data.expiresAt);
    },
    [scheduleRenewal],
  );

  const reset = useCallback(() => {
    clearTimeout(renewTimer.current);
    tokenStore.clear();
    queryClient.clear();
    setSession(ANONYMOUS);
  }, [queryClient]);

  useEffect(() => {
    configureSessionHandlers({
      onExpired: reset,
      onRefreshed: (data) => {
        setSession((current) => ({
          status: 'authenticated',
          user: data.user,
          tenant: data.tenant ?? current.tenant,
          expiresAt: data.expiresAt,
        }));
        scheduleRenewal(data.expiresAt);
      },
    });

    let cancelled = false;
    refreshSession()
      .then((data) => !cancelled && apply(data))
      .catch(() => !cancelled && setSession(ANONYMOUS));
    return () => {
      cancelled = true;
      clearTimeout(renewTimer.current);
    };
  }, [apply, reset, scheduleRenewal]);

  const activateLastTenant = useCallback(async () => {
    const previous = lastTenant.get();
    if (!previous) return null;
    try {
      const data = await authApi.activateTenant(previous);
      apply(data);
      return data.tenant;
    } catch {
      lastTenant.set(null);
      return null;
    }
  }, [apply]);

  /** Opens the remembered company; with none remembered, opens the user's only company. */
  const activateDefaultTenant = useCallback(async () => {
    const previous = await activateLastTenant();
    if (previous) return previous;
    try {
      const memberships = await authApi.memberships();
      const active = memberships.filter((m) => m.status === 'Active' && m.tenantStatus === 'Active');
      if (active.length !== 1) return null;
      const data = await authApi.activateTenant(active[0].tenantId);
      apply(data);
      return data.tenant;
    } catch {
      return null;
    }
  }, [apply, activateLastTenant]);

  const login = useCallback(
    async (credentials) => {
      apply(await authApi.login(credentials));
      return activateDefaultTenant();
    },
    [apply, activateDefaultTenant],
  );

  const register = useCallback(async (body) => apply(await authApi.register(body)), [apply]);

  const logout = useCallback(async () => {
    try {
      await authApi.logout();
    } finally {
      reset();
    }
  }, [reset]);

  /** Switching tenant gets a new token bound to the verified membership and drops all cached tenant data. */
  const switchTenant = useCallback(
    async (tenantId) => {
      const data = await authApi.activateTenant(tenantId);
      queryClient.clear();
      apply(data);
      return data.tenant;
    },
    [apply, queryClient],
  );

  const value = useMemo(
    () => ({
      ...session,
      isAuthenticated: session.status === 'authenticated',
      login,
      register,
      logout,
      switchTenant,
      can: (permission) => Boolean(session.tenant?.permissions?.includes(permission)),
    }),
    [session, login, register, logout, switchTenant],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth must be used inside <AuthProvider>');
  return context;
}

/** Query keys are prefixed with the active tenant so cached data can never cross tenants. */
export function useTenantKey(...parts) {
  const { tenant } = useAuth();
  return ['tenant', tenant?.tenantId ?? 'none', ...parts];
}
