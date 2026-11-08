import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Grid, MenuItem, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { reportApi } from '../api/endpoints';
import { PageHeader, Section } from '../components/Common';
import { QueryState } from '../components/DataState';
import { DailyBarChart, RangeFilter, RevenueChart, rangeToParams, useVizColors } from '../components/Charts';
import StatusChip from '../components/StatusChip';
import { formatMoney, formatNumber, formatPercent, humanize } from '../lib/format';

export default function ReportsPage() {
  const { tenant } = useAuth();
  const colors = useVizColors();
  const [range, setRange] = useState('30d');
  const params = useMemo(() => rangeToParams(range), [range]);
  const overview = useQuery({ queryKey: useTenantKey('reports', 'overview', range), queryFn: () => reportApi.overview(params) });
  const revenue = useQuery({ queryKey: useTenantKey('reports', 'revenue', range), queryFn: () => reportApi.revenue(params) });
  const payments = useQuery({ queryKey: useTenantKey('reports', 'payments', range), queryFn: () => reportApi.payments(params) });
  const refunds = useQuery({ queryKey: useTenantKey('reports', 'refunds', range), queryFn: () => reportApi.refunds(params) });
  const orders = useQuery({ queryKey: useTenantKey('reports', 'orders', range), queryFn: () => reportApi.orders(params) });

  const currencies = overview.data?.currencies.map((c) => c.currency) ?? [];
  const [currencyChoice, setCurrency] = useState(null);
  const currency = currencyChoice ?? (currencies.includes(tenant.defaultCurrency) ? tenant.defaultCurrency : currencies[0] ?? tenant.defaultCurrency);

  return (
    <>
      <PageHeader
        title="Reports"
        subtitle={`Days are grouped in ${tenant.timeZone}. Currencies are reported separately - no exchange-rate conversion.`}
        actions={
          <Stack direction="row" spacing={1} alignItems="center">
            <TextField select size="small" value={currency} onChange={(e) => setCurrency(e.target.value)} sx={{ width: 110 }} aria-label="Currency">
              {(currencies.length ? currencies : [currency]).map((c) => <MenuItem key={c} value={c}>{c}</MenuItem>)}
            </TextField>
            <RangeFilter value={range} onChange={setRange} />
          </Stack>
        }
      />
      <Grid container spacing={2}>
        <Grid size={12}>
          <Section title="Summary by currency">
            <QueryState query={overview} isEmpty={(d) => d.currencies.length === 0} empty={<Typography variant="body2" color="text.secondary">No financial activity in this period.</Typography>}>
              {(data) => (
                <Table size="small" aria-label="Financial summary by currency">
                  <TableHead>
                    <TableRow>
                      <TableCell>Currency</TableCell>
                      <TableCell align="right">Successful</TableCell>
                      <TableCell align="right">Failed</TableCell>
                      <TableCell align="right">Success rate</TableCell>
                      <TableCell align="right">Gross</TableCell>
                      <TableCell align="right">Refunded</TableCell>
                      <TableCell align="right">Net collected</TableCell>
                      <TableCell align="right">Avg. order</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {data.currencies.map((c) => (
                      <TableRow key={c.currency}>
                        <TableCell><strong>{c.currency}</strong></TableCell>
                        <TableCell align="right">{formatNumber(c.successfulPayments)}</TableCell>
                        <TableCell align="right">{formatNumber(c.failedPayments)}</TableCell>
                        <TableCell align="right">{formatPercent(c.paymentSuccessRate)}</TableCell>
                        <TableCell align="right">{formatMoney(c.grossPaymentAmountMinor, c.currency)}</TableCell>
                        <TableCell align="right">{formatMoney(c.refundedAmountMinor, c.currency)}</TableCell>
                        <TableCell align="right"><strong>{formatMoney(c.netCollectedAmountMinor, c.currency)}</strong></TableCell>
                        <TableCell align="right">{formatMoney(c.averageOrderValueMinor, c.currency)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </QueryState>
          </Section>
        </Grid>
        <Grid size={{ xs: 12, lg: 7 }}>
          <Section title={`Daily revenue (${currency})`}>
            <QueryState query={revenue}>{(data) => <RevenueChart points={data.points} currency={currency} timeZone={tenant.timeZone} />}</QueryState>
          </Section>
        </Grid>
        <Grid size={{ xs: 12, lg: 5 }}>
          <Section title="Payment outcomes per day">
            <QueryState query={payments}>
              {(data) => (
                <>
                  <Typography variant="body2" color="text.secondary">
                    Success rate {formatPercent(data.successRate)} · {formatNumber(data.successful)} successful · {formatNumber(data.failed)} failed
                  </Typography>
                  <DailyBarChart
                    stacked
                    timeZone={tenant.timeZone}
                    labels={data.daily.map((d) => d.date)}
                    series={[
                      { id: 'ok', label: 'Succeeded', data: data.daily.map((d) => d.successful), color: colors.status.good },
                      { id: 'failed', label: 'Failed', data: data.daily.map((d) => d.failed), color: colors.status.critical },
                    ]}
                  />
                </>
              )}
            </QueryState>
          </Section>
        </Grid>
        <Grid size={{ xs: 12, md: 6 }}>
          <Section title="Failure reasons">
            <QueryState query={payments} isEmpty={(d) => d.failureCodes.length === 0} empty={<Typography variant="body2" color="text.secondary">No failed payments in this period.</Typography>}>
              {(data) => (
                <Table size="small">
                  <TableHead><TableRow><TableCell>Reason</TableCell><TableCell align="right">Payments</TableCell></TableRow></TableHead>
                  <TableBody>
                    {data.failureCodes.map((f) => (
                      <TableRow key={f.failureCode}><TableCell>{humanize(f.failureCode)}</TableCell><TableCell align="right">{formatNumber(f.count)}</TableCell></TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </QueryState>
          </Section>
        </Grid>
        <Grid size={{ xs: 12, md: 6 }}>
          <Section title="Refunds by currency">
            <QueryState query={refunds} isEmpty={(d) => d.byCurrency.length === 0} empty={<Typography variant="body2" color="text.secondary">No refunds in this period.</Typography>}>
              {(data) => (
                <Table size="small">
                  <TableHead>
                    <TableRow><TableCell>Currency</TableCell><TableCell align="right">Succeeded</TableCell><TableCell align="right">In progress</TableCell><TableCell align="right">Failed</TableCell></TableRow>
                  </TableHead>
                  <TableBody>
                    {data.byCurrency.map((r) => (
                      <TableRow key={r.currency}>
                        <TableCell>{r.currency}</TableCell>
                        <TableCell align="right">{formatMoney(r.succeededAmountMinor, r.currency)} ({r.succeededCount})</TableCell>
                        <TableCell align="right">{formatMoney(r.pendingAmountMinor, r.currency)} ({r.pendingCount})</TableCell>
                        <TableCell align="right">{formatNumber(r.failedCount)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </QueryState>
          </Section>
        </Grid>
        <Grid size={12}>
          <Section title={`Orders created per day (${currency})`}>
            <QueryState query={orders}>
              {(data) => {
                const rows = data.daily.filter((d) => d.currency === currency);
                return (
                  <Grid container spacing={2}>
                    <Grid size={{ xs: 12, md: 8 }}>
                      {rows.length === 0 ? <Typography variant="body2" color="text.secondary">No orders in {currency} for this period.</Typography> : (
                        <DailyBarChart timeZone={tenant.timeZone} labels={rows.map((d) => d.date)} series={[{ id: 'orders', label: 'Orders', data: rows.map((d) => d.count), color: colors.series[0] }]} />
                      )}
                    </Grid>
                    <Grid size={{ xs: 12, md: 4 }}>
                      <Table size="small" aria-label="Orders by status">
                        <TableHead><TableRow><TableCell>Status</TableCell><TableCell align="right">Orders</TableCell></TableRow></TableHead>
                        <TableBody>
                          {data.byStatus.map((s) => (
                            <TableRow key={s.status}><TableCell><StatusChip status={s.status} /></TableCell><TableCell align="right">{formatNumber(s.count)}</TableCell></TableRow>
                          ))}
                        </TableBody>
                      </Table>
                    </Grid>
                  </Grid>
                );
              }}
            </QueryState>
          </Section>
        </Grid>
      </Grid>
    </>
  );
}
