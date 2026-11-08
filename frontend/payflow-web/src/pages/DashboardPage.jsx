import { useMemo, useState } from 'react';
import { Link as RouterLink } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Grid, List, ListItem, ListItemText, MenuItem, Stack, TextField, Typography } from '@mui/material';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { miscApi, reportApi } from '../api/endpoints';
import { PageHeader, Section } from '../components/Common';
import { ErrorState, LoadingState } from '../components/DataState';
import { RangeFilter, RevenueChart, SegmentedBar, StatTile, rangeToParams, useVizColors } from '../components/Charts';
import { formatDateTime, formatMoney, formatNumber, formatPercent } from '../lib/format';

export default function DashboardPage() {
  const { tenant, can } = useAuth();
  const colors = useVizColors();
  const [range, setRange] = useState('30d');
  const params = useMemo(() => rangeToParams(range), [range]);
  const canReport = can('reports.read');

  const overview = useQuery({ queryKey: useTenantKey('reports', 'overview', range), queryFn: () => reportApi.overview(params), enabled: canReport });
  const revenue = useQuery({ queryKey: useTenantKey('reports', 'revenue', range), queryFn: () => reportApi.revenue(params), enabled: canReport });
  const payments = useQuery({ queryKey: useTenantKey('reports', 'payments', range), queryFn: () => reportApi.payments(params), enabled: canReport });
  const activity = useQuery({ queryKey: useTenantKey('notifications'), queryFn: () => miscApi.notifications(15) });

  const currencies = overview.data?.currencies ?? [];
  const [currencyChoice, setCurrency] = useState(null);
  const currency = currencyChoice ?? (currencies.some((c) => c.currency === tenant.defaultCurrency) ? tenant.defaultCurrency : currencies[0]?.currency ?? tenant.defaultCurrency);
  const metrics = currencies.find((c) => c.currency === currency);

  const statusCount = (name) => payments.data?.byStatus.find((s) => s.status === name)?.count ?? 0;

  return (
    <>
      <PageHeader
        title="Dashboard"
        subtitle={`${tenant.name} · amounts are shown per currency and never converted`}
        actions={
          canReport && (
            <Stack direction="row" spacing={1} alignItems="center">
              {currencies.length > 1 && (
                <TextField select size="small" value={currency} onChange={(e) => setCurrency(e.target.value)} sx={{ width: 110 }} aria-label="Currency">
                  {currencies.map((c) => <MenuItem key={c.currency} value={c.currency}>{c.currency}</MenuItem>)}
                </TextField>
              )}
              <RangeFilter value={range} onChange={setRange} />
            </Stack>
          )
        }
      />

      {!canReport && <Alert severity="info" sx={{ mb: 3 }}>Your role does not include financial reports. Use the navigation to work with orders and customers.</Alert>}

      {canReport && overview.isError && <ErrorState error={overview.error} onRetry={overview.refetch} />}
      {canReport && overview.isPending && <LoadingState rows={2} />}
      {canReport && overview.isSuccess && (
        <Grid container spacing={2} sx={{ mb: 2 }}>
          <Grid size={{ xs: 12, md: 4 }}>
            <StatTile hero label={`Net collected (${currency})`} value={formatMoney(metrics?.netCollectedAmountMinor ?? 0, currency)} helper="Gross successful payments minus succeeded refunds" />
          </Grid>
          <Grid size={{ xs: 12, md: 8 }}>
            <Grid container spacing={2}>
              <Grid size={{ xs: 6, md: 4 }}><StatTile label="Gross payments" value={formatMoney(metrics?.grossPaymentAmountMinor ?? 0, currency)} helper={`${formatNumber(metrics?.successfulPayments ?? 0)} successful`} /></Grid>
              <Grid size={{ xs: 6, md: 4 }}><StatTile label="Refunded" value={formatMoney(metrics?.refundedAmountMinor ?? 0, currency)} /></Grid>
              <Grid size={{ xs: 6, md: 4 }}><StatTile label="Payment success rate" value={formatPercent(metrics?.paymentSuccessRate)} helper="Pending payments excluded" /></Grid>
              <Grid size={{ xs: 6, md: 4 }}><StatTile label="Average order value" value={formatMoney(metrics?.averageOrderValueMinor ?? 0, currency)} helper={`${formatNumber(metrics?.paidOrders ?? 0)} paid orders`} /></Grid>
              <Grid size={{ xs: 6, md: 4 }}><StatTile label="Orders created" value={formatNumber(overview.data.ordersCreated)} /></Grid>
              <Grid size={{ xs: 6, md: 4 }}><StatTile label="Awaiting outcome" value={formatNumber(overview.data.pendingPayments + overview.data.unknownPayments)} helper={`${overview.data.unknownPayments} under reconciliation`} /></Grid>
            </Grid>
          </Grid>
        </Grid>
      )}

      <Grid container spacing={2}>
        {canReport && (
          <Grid size={{ xs: 12, lg: 8 }}>
            <Section title={`Revenue (${currency})`}>
              {revenue.isPending ? <LoadingState rows={4} /> : revenue.isError ? <ErrorState error={revenue.error} onRetry={revenue.refetch} /> : (
                <RevenueChart points={revenue.data.points} currency={currency} timeZone={tenant.timeZone} />
              )}
            </Section>
          </Grid>
        )}
        {canReport && (
          <Grid size={{ xs: 12, lg: 4 }}>
            {/* Counts cover every currency, unlike the per-currency tiles above; say so when it matters. */}
            <Section title={currencies.length > 1 ? 'Payment outcomes (all currencies)' : 'Payment outcomes'}>
              {payments.isPending ? <LoadingState rows={2} /> : payments.isError ? <ErrorState error={payments.error} /> : (
                <SegmentedBar
                  ariaLabel="Payment outcomes"
                  parts={[
                    { label: 'Succeeded', value: statusCount('Succeeded') + statusCount('PartiallyRefunded') + statusCount('Refunded'), color: colors.status.good },
                    { label: 'Failed', value: statusCount('Failed'), color: colors.status.critical },
                    { label: 'In progress', value: statusCount('Pending') + statusCount('Unknown') + statusCount('Created'), color: colors.status.warning, ink: '#0b0b0b' },
                  ]}
                />
              )}
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 2 }}>
                Success rate = successful / (successful + failed). Pending and unknown payments are excluded.
              </Typography>
            </Section>
          </Grid>
        )}
        <Grid size={{ xs: 12, lg: canReport ? 12 : 8 }}>
          <Section title="Recent activity" action={can('payments.read') && <Button component={RouterLink} to="/payments" size="small">All payments</Button>}>
            {activity.isPending ? <LoadingState /> : activity.isError ? <ErrorState error={activity.error} /> : activity.data.length === 0 ? (
              <Typography variant="body2" color="text.secondary">No activity yet. Create an order and start a payment to see events here.</Typography>
            ) : (
              <List dense disablePadding>
                {activity.data.map((n) => (
                  <ListItem key={n.id} disableGutters divider>
                    <ListItemText primary={n.title} secondary={n.message} />
                    <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'nowrap', ml: 2 }}>{formatDateTime(n.createdAt, tenant.timeZone)}</Typography>
                  </ListItem>
                ))}
              </List>
            )}
          </Section>
        </Grid>
      </Grid>
    </>
  );
}
