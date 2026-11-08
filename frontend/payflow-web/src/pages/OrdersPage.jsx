import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Autocomplete, Button, Card, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, MenuItem, Stack, TextField, Typography,
} from '@mui/material';
import AddIcon from '@mui/icons-material/Add';
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { customerApi, orderApi, productApi } from '../api/endpoints';
import { errorMessage } from '../api/client';
import { PageHeader, ServerTable, useServerTable } from '../components/Common';
import StatusChip from '../components/StatusChip';
import { formatDateTime, formatMoney, newIdempotencyKey } from '../lib/format';
import { useDebounced } from '../lib/hooks';

const ORDER_STATUSES = ['Draft', 'PendingPayment', 'PaymentFailed', 'Paid', 'Cancelled'];

function useLookup(key, fetcher, search) {
  const debounced = useDebounced(search);
  return useQuery({ queryKey: useTenantKey(key, 'lookup', debounced), queryFn: () => fetcher({ search: debounced, status: 'Active', pageSize: 20 }), placeholderData: (prev) => prev });
}

export function CreateOrderDialog({ open, onClose, onCreated }) {
  const queryClient = useQueryClient();
  const idempotencyKey = useRef(newIdempotencyKey());
  const [customer, setCustomer] = useState(null);
  const [lines, setLines] = useState([{ product: null, quantity: 1 }]);
  const [notes, setNotes] = useState('');
  const [customerSearch, setCustomerSearch] = useState('');
  const [productSearch, setProductSearch] = useState('');
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);
  const customers = useLookup('customers', customerApi.list, customerSearch);
  const products = useLookup('products', productApi.list, productSearch);

  useEffect(() => {
    if (open) {
      idempotencyKey.current = newIdempotencyKey();
      setCustomer(null);
      setLines([{ product: null, quantity: 1 }]);
      setNotes('');
      setError(null);
    }
  }, [open]);

  const chosen = lines.filter((l) => l.product);
  const currencies = [...new Set(chosen.map((l) => l.product.currency))];
  const preview = currencies.length === 1 ? chosen.reduce((sum, l) => sum + l.product.priceMinor * (Number(l.quantity) || 0), 0) : null;
  const valid = customer && chosen.length > 0 && currencies.length === 1 && chosen.every((l) => Number.isInteger(Number(l.quantity)) && Number(l.quantity) > 0);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      // Only ids and quantities are sent: the server reads prices from the catalog and computes the total.
      const order = await orderApi.create(
        { customerId: customer.id, items: chosen.map((l) => ({ productId: l.product.id, quantity: Number(l.quantity) })), notes: notes || null },
        idempotencyKey.current,
      );
      await queryClient.invalidateQueries({ queryKey: ['tenant'] });
      onCreated(order);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const update = (index, patch) => setLines((current) => current.map((l, i) => (i === index ? { ...l, ...patch } : l)));

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="md">
      <DialogTitle>New order</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error && <Alert severity="error">{errorMessage(error)}</Alert>}
          <Autocomplete
            options={customers.data?.items ?? []}
            value={customer}
            onChange={(_, value) => setCustomer(value)}
            onInputChange={(_, value) => setCustomerSearch(value)}
            getOptionLabel={(c) => c.name + (c.email ? ` · ${c.email}` : '')}
            isOptionEqualToValue={(a, b) => a.id === b.id}
            filterOptions={(x) => x}
            loading={customers.isFetching}
            renderInput={(params) => <TextField {...params} label="Customer" />}
          />
          {lines.map((line, index) => (
            <Stack key={index} direction="row" spacing={1} alignItems="center">
              <Autocomplete
                sx={{ flex: 1 }}
                options={products.data?.items ?? []}
                value={line.product}
                onChange={(_, value) => update(index, { product: value })}
                onInputChange={(_, value) => setProductSearch(value)}
                getOptionLabel={(p) => `${p.sku} · ${p.name} · ${formatMoney(p.priceMinor, p.currency)}`}
                isOptionEqualToValue={(a, b) => a.id === b.id}
                filterOptions={(x) => x}
                renderInput={(params) => <TextField {...params} label={`Product ${index + 1}`} />}
              />
              <TextField label="Qty" type="number" value={line.quantity} onChange={(e) => update(index, { quantity: e.target.value })} sx={{ width: 100 }} slotProps={{ htmlInput: { min: 1, max: 10000 } }} />
              <IconButton aria-label="Remove line" onClick={() => setLines((current) => current.filter((_, i) => i !== index))} disabled={lines.length === 1}>
                <DeleteOutlineIcon />
              </IconButton>
            </Stack>
          ))}
          <Button startIcon={<AddIcon />} onClick={() => setLines((current) => [...current, { product: null, quantity: 1 }])} sx={{ alignSelf: 'flex-start' }}>Add line</Button>
          {currencies.length > 1 && <Alert severity="warning">All items of an order must use the same currency.</Alert>}
          <TextField label="Notes (optional)" value={notes} onChange={(e) => setNotes(e.target.value)} multiline minRows={2} />
          <Typography variant="body2" color="text.secondary">
            Estimated total: <strong>{preview !== null ? formatMoney(preview, currencies[0]) : '—'}</strong> (final total is calculated by the server from current catalog prices)
          </Typography>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="contained" onClick={submit} disabled={!valid || busy}>{busy ? 'Creating…' : 'Create order'}</Button>
      </DialogActions>
    </Dialog>
  );
}

export default function OrdersPage() {
  const { can, tenant } = useAuth();
  const navigate = useNavigate();
  const table = useServerTable({ sortFieldMap: { totalMinor: 'total' } });
  const [status, setStatus] = useState('');
  const [search, setSearch] = useState('');
  const [creating, setCreating] = useState(false);
  const debouncedSearch = useDebounced(search);
  const params = { ...table.params, status, search: debouncedSearch };
  const query = useQuery({ queryKey: useTenantKey('orders', params), queryFn: () => orderApi.list(params), placeholderData: (prev) => prev });

  const columns = useMemo(() => [
    { field: 'orderNumber', headerName: 'Order', width: 130 },
    { field: 'customerName', headerName: 'Customer', flex: 1, minWidth: 180, sortable: false },
    { field: 'status', headerName: 'Status', width: 170, renderCell: ({ value }) => <StatusChip status={value} /> },
    { field: 'itemCount', headerName: 'Items', width: 80, sortable: false, align: 'right', headerAlign: 'right' },
    { field: 'totalMinor', headerName: 'Total', width: 150, align: 'right', headerAlign: 'right', sortable: true, valueFormatter: (value, row) => formatMoney(value, row.currency) },
    { field: 'createdAt', headerName: 'Created', width: 180, valueFormatter: (value) => formatDateTime(value, tenant.timeZone) },
  ], [tenant.timeZone]);

  return (
    <>
      <PageHeader
        title="Orders"
        subtitle="Purchase intents of your customers with server-side price snapshots."
        actions={can('orders.write') && <Button variant="contained" startIcon={<AddIcon />} onClick={() => setCreating(true)}>New order</Button>}
      />
      <Card>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ p: 2 }}>
          <TextField label="Order number" placeholder="SO-000123" value={search} onChange={(e) => { setSearch(e.target.value); table.resetPage(); }} sx={{ maxWidth: 260 }} />
          <TextField select label="Status" value={status} onChange={(e) => { setStatus(e.target.value); table.resetPage(); }} sx={{ width: 200 }}>
            <MenuItem value="">All</MenuItem>
            {ORDER_STATUSES.map((s) => <MenuItem key={s} value={s}><StatusChip status={s} /></MenuItem>)}
          </TextField>
        </Stack>
        <ServerTable query={query} columns={columns} table={table} onRowClick={(row) => navigate(`/orders/${row.id}`)} emptyTitle="No orders found" />
      </Card>
      <CreateOrderDialog open={creating} onClose={() => setCreating(false)} onCreated={(order) => navigate(`/orders/${order.id}`)} />
    </>
  );
}
