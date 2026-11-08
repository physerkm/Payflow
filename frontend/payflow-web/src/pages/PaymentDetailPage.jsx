import { useRef, useState } from 'react';
import { Link as RouterLink, useParams } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Breadcrumbs, Button, Grid, LinearProgress, Link, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography,
} from '@mui/material';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { paymentApi } from '../api/endpoints';
import { KeyValue, PageHeader, Section } from '../components/Common';
import { QueryState } from '../components/DataState';
import StatusChip from '../components/StatusChip';
import { PayDialog, RefundDialog } from '../components/PaymentDialogs';
import { formatDateTime, formatMoney, humanize } from '../lib/format';

const POLL_INTERVAL_MS = 1500;
const POLL_LIMIT_MS = 90_000;

export default function PaymentDetailPage() {
  const { paymentId } = useParams();
  const { can, tenant } = useAuth();
  const queryClient = useQueryClient();
  const [refunding, setRefunding] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const pollStarted = useRef(Date.now());

  // Controlled polling: only while the outcome is not final, and for a bounded time.
  const keepPolling = (data) => data && (!data.isFinal || data.pendingRefundAmountMinor > 0) && Date.now() - pollStarted.current < POLL_LIMIT_MS;
  const paymentQuery = useQuery({
    queryKey: useTenantKey('payments', paymentId),
    queryFn: () => paymentApi.get(paymentId),
    refetchInterval: (query) => (keepPolling(query.state.data) ? POLL_INTERVAL_MS : false),
  });
  const polling = keepPolling(paymentQuery.data);
  const attemptsQuery = useQuery({
    queryKey: useTenantKey('payments', paymentId, 'attempts', paymentQuery.data?.updatedAt),
    queryFn: () => paymentApi.attempts(paymentId),
    enabled: paymentQuery.isSuccess,
  });
  const refundsQuery = useQuery({
    queryKey: useTenantKey('payments', paymentId, 'refunds', paymentQuery.data?.updatedAt),
    queryFn: () => paymentApi.refunds(paymentId),
    enabled: paymentQuery.isSuccess,
  });

  const refresh = async () => {
    pollStarted.current = Date.now();
    await queryClient.invalidateQueries({ queryKey: ['tenant'] });
  };

  return (
    <QueryState query={paymentQuery} rows={6}>
      {(payment) => (
        <>
          <Breadcrumbs sx={{ mb: 1 }}>
            <Link component={RouterLink} to="/payments" underline="hover" color="inherit">Payments</Link>
            <Typography color="text.primary">{payment.id.slice(0, 8)}</Typography>
          </Breadcrumbs>
          <PageHeader
            title={<Stack direction="row" spacing={1.5} alignItems="center"><span>{formatMoney(payment.amountMinor, payment.currency)}</span><StatusChip status={payment.status} size="medium" /></Stack>}
            subtitle={<>Order <Link component={RouterLink} to={`/orders/${payment.orderId}`}>{payment.orderNumber}</Link> · provider {payment.provider}</>}
            actions={
              <>
                {payment.status === 'Failed' && can('payments.write') && <Button variant="outlined" onClick={() => setRetrying(true)}>Retry payment</Button>}
                {payment.refundableAmountMinor > 0 && ['Succeeded', 'PartiallyRefunded'].includes(payment.status) && can('refunds.create') && (
                  <Button variant="contained" color="warning" onClick={() => setRefunding(true)}>Refund</Button>
                )}
              </>
            }
          />
          {polling && (
            <Alert severity="info" sx={{ mb: 2 }} icon={false}>
              <Stack spacing={1}>
                <span>{payment.status === 'Unknown' ? 'The provider did not answer in time. PayFlow is reconciling the payment with the provider - it will not charge again blindly.' : 'Waiting for the provider. This page updates automatically.'}</span>
                <LinearProgress />
              </Stack>
            </Alert>
          )}
          {payment.failureCode && <Alert severity="error" sx={{ mb: 2 }}>Failure reason: <strong>{humanize(payment.failureCode)}</strong></Alert>}
          <Grid container spacing={2}>
            <Grid size={{ xs: 12, md: 4 }}>
              <Section title="Summary">
                <Stack spacing={2}>
                  <KeyValue label="Amount">{formatMoney(payment.amountMinor, payment.currency)}</KeyValue>
                  <KeyValue label="Refunded">{formatMoney(payment.refundedAmountMinor, payment.currency)}</KeyValue>
                  <KeyValue label="Refund in progress">{formatMoney(payment.pendingRefundAmountMinor, payment.currency)}</KeyValue>
                  <KeyValue label="Refundable">{formatMoney(payment.refundableAmountMinor, payment.currency)}</KeyValue>
                  <KeyValue label="Created">{formatDateTime(payment.createdAt, tenant.timeZone)}</KeyValue>
                  <KeyValue label="Succeeded at">{payment.succeededAt && formatDateTime(payment.succeededAt, tenant.timeZone)}</KeyValue>
                </Stack>
              </Section>
            </Grid>
            <Grid size={{ xs: 12, md: 8 }}>
              <Stack spacing={2}>
                <Section title="Provider attempts">
                  <QueryState query={attemptsQuery}>
                    {(attempts) => (
                      <Table size="small">
                        <TableHead>
                          <TableRow>
                            <TableCell>#</TableCell>
                            <TableCell>Trigger</TableCell>
                            <TableCell>Status</TableCell>
                            <TableCell>Provider reference</TableCell>
                            <TableCell>Failure</TableCell>
                            <TableCell>Scheduled</TableCell>
                          </TableRow>
                        </TableHead>
                        <TableBody>
                          {attempts.map((a) => (
                            <TableRow key={a.id}>
                              <TableCell>{a.attemptNumber}</TableCell>
                              <TableCell>{humanize(a.trigger)}</TableCell>
                              <TableCell><StatusChip status={a.status} /></TableCell>
                              <TableCell sx={{ fontFamily: 'monospace', fontSize: 12 }}>{a.providerReference ?? '—'}</TableCell>
                              <TableCell>{a.failureCode ? humanize(a.failureCode) : '—'}</TableCell>
                              <TableCell>{formatDateTime(a.scheduledAt, tenant.timeZone)}</TableCell>
                            </TableRow>
                          ))}
                        </TableBody>
                      </Table>
                    )}
                  </QueryState>
                </Section>
                <Section title="Refunds">
                  <QueryState query={refundsQuery} isEmpty={(d) => d.length === 0} empty={<Typography variant="body2" color="text.secondary">No refunds.</Typography>}>
                    {(refunds) => (
                      <Table size="small">
                        <TableHead>
                          <TableRow>
                            <TableCell>Status</TableCell>
                            <TableCell align="right">Amount</TableCell>
                            <TableCell>Reason</TableCell>
                            <TableCell>Requested</TableCell>
                          </TableRow>
                        </TableHead>
                        <TableBody>
                          {refunds.map((r) => (
                            <TableRow key={r.id}>
                              <TableCell><StatusChip status={r.status} /></TableCell>
                              <TableCell align="right">{formatMoney(r.amountMinor, r.currency)}</TableCell>
                              <TableCell>{r.reason ?? '—'}</TableCell>
                              <TableCell>{formatDateTime(r.createdAt, tenant.timeZone)}</TableCell>
                            </TableRow>
                          ))}
                        </TableBody>
                      </Table>
                    )}
                  </QueryState>
                </Section>
              </Stack>
            </Grid>
          </Grid>
          <RefundDialog
            open={refunding}
            payment={payment}
            onClose={() => setRefunding(false)}
            onSubmit={async (body, key) => {
              await paymentApi.refund(payment.id, body, key);
              setRefunding(false);
              await refresh();
            }}
          />
          <PayDialog
            open={retrying}
            title="Retry payment"
            submitLabel="Retry"
            amountLabel={formatMoney(payment.amountMinor, payment.currency)}
            onClose={() => setRetrying(false)}
            onSubmit={async (token, key) => {
              await paymentApi.retry(payment.id, token, key);
              setRetrying(false);
              await refresh();
            }}
          />
        </>
      )}
    </QueryState>
  );
}
