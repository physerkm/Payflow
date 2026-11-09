import { useState } from 'react';
import { Link as RouterLink, useNavigate, useParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Breadcrumbs, Button, Grid, Link, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography,
} from '@mui/material';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { orderApi } from '../api/endpoints';
import { errorMessage } from '../api/client';
import { ConfirmDialog, KeyValue, PageHeader, Section } from '../components/Common';
import { QueryState } from '../components/DataState';
import StatusChip from '../components/StatusChip';
import OrderHistory from '../components/OrderHistory';
import { PayDialog } from '../components/PaymentDialogs';
import { formatDateTime, formatMoney } from '../lib/format';

export default function OrderDetailPage() {
  const { orderId } = useParams();
  const { can, tenant } = useAuth();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [paying, setPaying] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const orderQuery = useQuery({ queryKey: useTenantKey('orders', orderId), queryFn: () => orderApi.get(orderId) });
  const paymentsQuery = useQuery({ queryKey: useTenantKey('orders', orderId, 'payments'), queryFn: () => orderApi.payments(orderId), enabled: can('payments.read') });
  const historyQuery = useQuery({ queryKey: useTenantKey('orders', orderId, 'history'), queryFn: () => orderApi.history(orderId) });

  const cancel = useMutation({
    mutationFn: () => orderApi.cancel(orderId),
    onSuccess: () => {
      setCancelling(false);
      queryClient.invalidateQueries({ queryKey: ['tenant'] });
    },
  });

  return (
    <QueryState query={orderQuery} rows={6}>
      {(order) => {
        const payable = ['Draft', 'PaymentFailed'].includes(order.status) && can('payments.write');
        const cancellable = ['Draft', 'PendingPayment', 'PaymentFailed'].includes(order.status) && can('orders.write');
        return (
          <>
            <Breadcrumbs sx={{ mb: 1 }}>
              <Link component={RouterLink} to="/orders" underline="hover" color="inherit">Orders</Link>
              <Typography color="text.primary">{order.orderNumber}</Typography>
            </Breadcrumbs>
            <PageHeader
              title={<Stack direction="row" spacing={1.5} alignItems="center"><span>{order.orderNumber}</span><StatusChip status={order.status} size="medium" /></Stack>}
              subtitle={`Created ${formatDateTime(order.createdAt, tenant.timeZone)}`}
              actions={
                <>
                  {cancellable && <Button color="error" variant="outlined" onClick={() => setCancelling(true)}>Cancel order</Button>}
                  {payable && <Button variant="contained" onClick={() => setPaying(true)}>{order.status === 'PaymentFailed' ? 'Pay again' : 'Take payment'}</Button>}
                </>
              }
            />
            {cancel.isError && <Alert severity="error" sx={{ mb: 2 }}>{errorMessage(cancel.error)}</Alert>}
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 8 }}>
                <Section title="Items">
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell>SKU</TableCell>
                        <TableCell>Product</TableCell>
                        <TableCell align="right">Qty</TableCell>
                        <TableCell align="right">Unit price</TableCell>
                        <TableCell align="right">Line total</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {order.items.map((item) => (
                        <TableRow key={item.id}>
                          <TableCell>{item.sku}</TableCell>
                          <TableCell>{item.name}</TableCell>
                          <TableCell align="right">{item.quantity}</TableCell>
                          <TableCell align="right">{formatMoney(item.unitPriceMinor, item.currency)}</TableCell>
                          <TableCell align="right">{formatMoney(item.lineTotalMinor, item.currency)}</TableCell>
                        </TableRow>
                      ))}
                      <TableRow>
                        <TableCell colSpan={4} align="right"><strong>Total</strong></TableCell>
                        <TableCell align="right"><strong>{formatMoney(order.totalMinor, order.currency)}</strong></TableCell>
                      </TableRow>
                    </TableBody>
                  </Table>
                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                    Name, SKU and price are snapshots taken when the order was created; later catalog changes do not affect this order.
                  </Typography>
                </Section>
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <Section title="Details">
                  <Stack spacing={2}>
                    <KeyValue label="Customer">{order.customerName}</KeyValue>
                    <KeyValue label="Total">{formatMoney(order.totalMinor, order.currency)}</KeyValue>
                    <KeyValue label="Paid at">{order.paidAt && formatDateTime(order.paidAt, tenant.timeZone)}</KeyValue>
                    <KeyValue label="Cancelled at">{order.cancelledAt && formatDateTime(order.cancelledAt, tenant.timeZone)}</KeyValue>
                    <KeyValue label="Notes">{order.notes}</KeyValue>
                  </Stack>
                </Section>
              </Grid>
              {can('payments.read') && (
                <Grid size={12}>
                  <Section title="Payments">
                    <QueryState query={paymentsQuery} isEmpty={(d) => d.items.length === 0} empty={<Typography variant="body2" color="text.secondary">No payments yet.</Typography>}>
                      {(payments) => (
                        <Table size="small">
                          <TableHead>
                            <TableRow>
                              <TableCell>Status</TableCell>
                              <TableCell align="right">Amount</TableCell>
                              <TableCell align="right">Refunded</TableCell>
                              <TableCell align="right">Attempts</TableCell>
                              <TableCell>Created</TableCell>
                            </TableRow>
                          </TableHead>
                          <TableBody>
                            {payments.items.map((p) => (
                              <TableRow key={p.id} hover sx={{ cursor: 'pointer' }} onClick={() => navigate(`/payments/${p.id}`)}>
                                <TableCell><StatusChip status={p.status} /></TableCell>
                                <TableCell align="right">{formatMoney(p.amountMinor, p.currency)}</TableCell>
                                <TableCell align="right">{formatMoney(p.refundedAmountMinor, p.currency)}</TableCell>
                                <TableCell align="right">{p.attemptCount}</TableCell>
                                <TableCell>{formatDateTime(p.createdAt, tenant.timeZone)}</TableCell>
                              </TableRow>
                            ))}
                          </TableBody>
                        </Table>
                      )}
                    </QueryState>
                  </Section>
                </Grid>
              )}
              <Grid size={12}>
                <Section title="History">
                  <QueryState query={historyQuery} rows={3}>
                    {(entries) => <OrderHistory entries={entries} timeZone={tenant.timeZone} />}
                  </QueryState>
                </Section>
              </Grid>
            </Grid>
            <PayDialog
              open={paying}
              amountLabel={formatMoney(order.totalMinor, order.currency)}
              onClose={() => setPaying(false)}
              onSubmit={async (token, key) => {
                const accepted = await orderApi.pay(order.id, token, key);
                await queryClient.invalidateQueries({ queryKey: ['tenant'] });
                navigate(`/payments/${accepted.paymentId}`);
              }}
            />
            <ConfirmDialog
              open={cancelling}
              title={`Cancel ${order.orderNumber}?`}
              message="A payment that has not been sent to the provider yet is cancelled too. Paid orders cannot be cancelled - use a refund instead."
              confirmLabel="Cancel order"
              confirmColor="error"
              busy={cancel.isPending}
              onConfirm={() => cancel.mutate()}
              onClose={() => setCancelling(false)}
            />
          </>
        );
      }}
    </QueryState>
  );
}
