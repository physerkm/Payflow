import { http } from './client';
import { newIdempotencyKey } from '../lib/format';

const data = (promise) => promise.then((response) => response.data);
const withKey = (key) => ({ headers: { 'Idempotency-Key': key ?? newIdempotencyKey() } });

/** Drops empty values so only real filters reach the query string. */
export function cleanParams(params) {
  return Object.fromEntries(Object.entries(params ?? {}).filter(([, v]) => v !== undefined && v !== null && v !== ''));
}

export const authApi = {
  login: (body) => data(http.post('/auth/login', body)),
  register: (body) => data(http.post('/auth/register', body)),
  logout: () => http.post('/auth/logout'),
  me: () => data(http.get('/auth/me')),
  memberships: () => data(http.get('/auth/memberships')),
  activateTenant: (tenantId) => data(http.post('/auth/activate-tenant', { tenantId })),
  invitations: () => data(http.get('/auth/invitations')),
  acceptInvitation: (id) => data(http.post(`/auth/invitations/${id}/accept`)),
};

export const tenantApi = {
  create: (body) => data(http.post('/tenants', body)),
  current: () => data(http.get('/tenants/current')),
  update: (body) => data(http.patch('/tenants/current', body)),
  members: () => data(http.get('/tenants/current/members')),
  invitations: () => data(http.get('/tenants/current/invitations')),
  invite: (body) => data(http.post('/tenants/current/invitations', body)),
  revokeInvitation: (id) => http.delete(`/tenants/current/invitations/${id}`),
  updateMember: (id, body) => data(http.patch(`/tenants/current/members/${id}`, body)),
  removeMember: (id) => http.delete(`/tenants/current/members/${id}`),
};

export const customerApi = {
  list: (params) => data(http.get('/customers', { params: cleanParams(params) })),
  get: (id) => data(http.get(`/customers/${id}`)),
  create: (body, key) => data(http.post('/customers', body, withKey(key))),
  update: (id, body) => data(http.put(`/customers/${id}`, body)),
  deactivate: (id) => http.delete(`/customers/${id}`),
};

export const productApi = {
  list: (params) => data(http.get('/products', { params: cleanParams(params) })),
  get: (id) => data(http.get(`/products/${id}`)),
  create: (body, key) => data(http.post('/products', body, withKey(key))),
  update: (id, body) => data(http.put(`/products/${id}`, body)),
  setStatus: (id, status) => data(http.patch(`/products/${id}/status`, { status })),
};

export const orderApi = {
  list: (params) => data(http.get('/orders', { params: cleanParams(params) })),
  get: (id) => data(http.get(`/orders/${id}`)),
  create: (body, key) => data(http.post('/orders', body, withKey(key))),
  cancel: (id) => data(http.patch(`/orders/${id}/cancel`)),
  payments: (id) => data(http.get(`/orders/${id}/payments`)),
  history: (id) => data(http.get(`/orders/${id}/history`)),
  pay: (id, paymentMethodToken, key) => data(http.post(`/orders/${id}/payments`, { paymentMethodToken }, withKey(key))),
};

export const paymentApi = {
  list: (params) => data(http.get('/payments', { params: cleanParams(params) })),
  get: (id) => data(http.get(`/payments/${id}`)),
  attempts: (id) => data(http.get(`/payments/${id}/attempts`)),
  retry: (id, paymentMethodToken, key) => data(http.post(`/payments/${id}/retry`, { paymentMethodToken }, withKey(key))),
  refunds: (id) => data(http.get(`/payments/${id}/refunds`)),
  refund: (id, body, key) => data(http.post(`/payments/${id}/refunds`, body, withKey(key))),
  allRefunds: (params) => data(http.get('/refunds', { params: cleanParams(params) })),
};

export const reportApi = {
  overview: (params) => data(http.get('/reports/overview', { params: cleanParams(params) })),
  revenue: (params) => data(http.get('/reports/revenue', { params: cleanParams(params) })),
  payments: (params) => data(http.get('/reports/payments', { params: cleanParams(params) })),
  refunds: (params) => data(http.get('/reports/refunds', { params: cleanParams(params) })),
  orders: (params) => data(http.get('/reports/orders', { params: cleanParams(params) })),
};

export const miscApi = {
  notifications: (limit = 20) => data(http.get('/notifications', { params: { limit } })),
  auditLogs: (params) => data(http.get('/audit-logs', { params: cleanParams(params) })),
};

export const assistantApi = {
  status: () => data(http.get('/assistant/status')),
  ask: (question) => data(http.post('/assistant/ask', { question }, { timeout: 120000 })),
};

export const platformApi = {
  tenants: (params) => data(http.get('/platform/tenants', { params: cleanParams(params) })),
  setStatus: (id, status, reason) => data(http.patch(`/platform/tenants/${id}/status`, { status, reason })),
};
