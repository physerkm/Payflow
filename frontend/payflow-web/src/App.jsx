import { useMemo, useState } from 'react';
import { ColorModeContext } from './colorMode';
import { createBrowserRouter, Navigate, Outlet, RouterProvider, useLocation } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CssBaseline, ThemeProvider, useMediaQuery } from '@mui/material';
import { AuthProvider, useAuth } from './auth/AuthProvider';
import { createAppTheme } from './theme';
import { FullPageLoader } from './components/DataState';
import AppLayout from './components/AppLayout';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import TenantSetupPage from './pages/TenantSetupPage';
import DashboardPage from './pages/DashboardPage';
import CustomersPage from './pages/CustomersPage';
import ProductsPage from './pages/ProductsPage';
import OrdersPage from './pages/OrdersPage';
import OrderDetailPage from './pages/OrderDetailPage';
import PaymentsPage from './pages/PaymentsPage';
import PaymentDetailPage from './pages/PaymentDetailPage';
import RefundsPage from './pages/RefundsPage';
import TeamPage from './pages/TeamPage';
import ReportsPage from './pages/ReportsPage';
import SettingsPage from './pages/SettingsPage';
import NotFoundPage from './pages/NotFoundPage';
import AssistantPage from './pages/AssistantPage';
import PlatformPage from './pages/PlatformPage';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 15_000,
      refetchOnWindowFocus: false,
      retry: (count, error) => count < 2 && (error?.status ?? 500) >= 500,
    },
    mutations: { retry: false },
  },
});

function RequireAuth() {
  const { status } = useAuth();
  const location = useLocation();
  if (status === 'loading') return <FullPageLoader />;
  if (status !== 'authenticated') return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  return <Outlet />;
}

function RequireTenant() {
  const { tenant } = useAuth();
  if (!tenant) return <Navigate to="/tenants" replace />;
  return <AppLayout />;
}

function GuestOnly({ children }) {
  const { status } = useAuth();
  if (status === 'loading') return <FullPageLoader />;
  return status === 'authenticated' ? <Navigate to="/" replace /> : children;
}

function Root() {
  return (
    <AuthProvider>
      <Outlet />
    </AuthProvider>
  );
}

const router = createBrowserRouter([
  {
    element: <Root />,
    children: [
      { path: '/login', element: <GuestOnly><LoginPage /></GuestOnly> },
      { path: '/register', element: <GuestOnly><RegisterPage /></GuestOnly> },
      {
        element: <RequireAuth />,
        children: [
          { path: '/tenants', element: <TenantSetupPage /> },
          { path: '/platform', element: <PlatformPage /> },
          {
            element: <RequireTenant />,
            children: [
              { index: true, element: <DashboardPage /> },
              { path: 'customers', element: <CustomersPage /> },
              { path: 'products', element: <ProductsPage /> },
              { path: 'orders', element: <OrdersPage /> },
              { path: 'orders/:orderId', element: <OrderDetailPage /> },
              { path: 'payments', element: <PaymentsPage /> },
              { path: 'payments/:paymentId', element: <PaymentDetailPage /> },
              { path: 'refunds', element: <RefundsPage /> },
              { path: 'team', element: <TeamPage /> },
              { path: 'reports', element: <ReportsPage /> },
              { path: 'settings', element: <SettingsPage /> },
              { path: 'assistant', element: <AssistantPage /> },
            ],
          },
        ],
      },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);

export default function App() {
  const prefersDark = useMediaQuery('(prefers-color-scheme: dark)');
  const [override, setOverride] = useState(() => {
    try {
      return localStorage.getItem('payflow.colorMode');
    } catch {
      return null;
    }
  });
  const mode = override ?? (prefersDark ? 'dark' : 'light');
  const theme = useMemo(() => createAppTheme(mode), [mode]);
  const colorMode = useMemo(
    () => ({
      mode,
      toggle: () => {
        const next = mode === 'dark' ? 'light' : 'dark';
        setOverride(next);
        try {
          localStorage.setItem('payflow.colorMode', next);
        } catch {
          /* ignore */
        }
      },
    }),
    [mode],
  );

  return (
    <ColorModeContext.Provider value={colorMode}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        <QueryClientProvider client={queryClient}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </ThemeProvider>
    </ColorModeContext.Provider>
  );
}
