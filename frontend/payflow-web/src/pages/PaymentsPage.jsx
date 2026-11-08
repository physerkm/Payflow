import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { Card, MenuItem, Stack, TextField } from '@mui/material';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { paymentApi } from '../api/endpoints';
import { PageHeader, ServerTable, useServerTable } from '../components/Common';
import StatusChip from '../components/StatusChip';
import { formatDateTime, formatMoney, humanize } from '../lib/format';

const PAYMENT_STATUSES = ['Pending', 'Unknown', 'Succeeded', 'PartiallyRefunded', 'Refunded', 'Failed', 'Cancelled'];
const REFUND_STATUSES = ['Pending', 'Succeeded', 'Failed'];

export default function PaymentsPage() {
  const { tenant } = useAuth();
  const navigate = useNavigate();
  const table = useServerTable({ sortFieldMap: { amountMinor: 'amount' } });
  const [status, setStatus] = useState('');
  const params = { ...table.params, status };
  const query = useQuery({ queryKey: useTenantKey('payments', params), queryFn: () => paymentApi.list(params), placeholderData: (prev) => prev, refetchInterval: 10_000 });

  const columns = useMemo(() => [
    { field: 'orderNumber', headerName: 'Order', width: 130, sortable: false },
    { field: 'status', headerName: 'Status', width: 190, renderCell: ({ value }) => <StatusChip status={value} /> },
    { field: 'amountMinor', headerName: 'Amount', width: 150, align: 'right', headerAlign: 'right', valueFormatter: (value, row) => formatMoney(value, row.currency) },
    { field: 'refundedAmountMinor', headerName: 'Refunded', width: 140, align: 'right', headerAlign: 'right', sortable: false, valueFormatter: (value, row) => formatMoney(value, row.currency) },
    { field: 'attemptCount', headerName: 'Attempts', width: 100, align: 'right', headerAlign: 'right', sortable: false },
    { field: 'failureCode', headerName: 'Failure', flex: 1, minWidth: 140, sortable: false, valueFormatter: (value) => (value ? humanize(value) : '') },
    { field: 'createdAt', headerName: 'Created', width: 180, valueFormatter: (value) => formatDateTime(value, tenant.timeZone) },
  ], [tenant.timeZone]);

  return (
    <>
      <PageHeader title="Payments" subtitle="Every payment and its current state. Open one to see provider attempts and refunds." />
      <Card>
        <Stack direction="row" spacing={2} sx={{ p: 2 }}>
          <TextField select label="Status" value={status} onChange={(e) => { setStatus(e.target.value); table.resetPage(); }} sx={{ width: 220 }}>
            <MenuItem value="">All</MenuItem>
            {PAYMENT_STATUSES.map((s) => <MenuItem key={s} value={s}><StatusChip status={s} /></MenuItem>)}
          </TextField>
        </Stack>
        <ServerTable query={query} columns={columns} table={table} onRowClick={(row) => navigate(`/payments/${row.id}`)} emptyTitle="No payments" emptyDescription="Payments appear here after you take payment for an order." />
      </Card>
    </>
  );
}

export function RefundsPageContent() {
  const { tenant } = useAuth();
  const navigate = useNavigate();
  const table = useServerTable({ defaultSort: null });
  const [status, setStatus] = useState('');
  const params = { page: table.params.page, pageSize: table.params.pageSize, status };
  const query = useQuery({ queryKey: useTenantKey('refunds', params), queryFn: () => paymentApi.allRefunds(params), placeholderData: (prev) => prev });

  const columns = useMemo(() => [
    { field: 'orderNumber', headerName: 'Order', width: 130, sortable: false },
    { field: 'status', headerName: 'Status', width: 150, sortable: false, renderCell: ({ value }) => <StatusChip status={value} /> },
    { field: 'amountMinor', headerName: 'Amount', width: 150, align: 'right', headerAlign: 'right', sortable: false, valueFormatter: (value, row) => formatMoney(value, row.currency) },
    { field: 'reason', headerName: 'Reason', flex: 1, minWidth: 160, sortable: false },
    { field: 'failureCode', headerName: 'Failure', width: 160, sortable: false, valueFormatter: (value) => (value ? humanize(value) : '') },
    { field: 'createdAt', headerName: 'Requested', width: 180, sortable: false, valueFormatter: (value) => formatDateTime(value, tenant.timeZone) },
  ], [tenant.timeZone]);

  return (
    <>
      <PageHeader title="Refunds" subtitle="Refund requests are executed asynchronously through the provider." />
      <Card>
        <Stack direction="row" spacing={2} sx={{ p: 2 }}>
          <TextField select label="Status" value={status} onChange={(e) => { setStatus(e.target.value); table.resetPage(); }} sx={{ width: 200 }}>
            <MenuItem value="">All</MenuItem>
            {REFUND_STATUSES.map((s) => <MenuItem key={s} value={s}><StatusChip status={s} /></MenuItem>)}
          </TextField>
        </Stack>
        <ServerTable query={query} columns={columns} table={table} onRowClick={(row) => navigate(`/payments/${row.paymentId}`)} emptyTitle="No refunds" />
      </Card>
    </>
  );
}
