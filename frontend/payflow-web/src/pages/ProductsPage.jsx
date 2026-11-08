import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Alert, Button, Card, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, MenuItem, Stack, Switch, TextField, Tooltip } from '@mui/material';
import AddIcon from '@mui/icons-material/Add';
import EditOutlinedIcon from '@mui/icons-material/EditOutlined';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { productApi } from '../api/endpoints';
import { applyFieldErrors, errorMessage } from '../api/client';
import { PageHeader, SelectField, ServerTable, useServerTable } from '../components/Common';
import StatusChip from '../components/StatusChip';
import { formatMoney, fromMinorUnits, newIdempotencyKey, SUPPORTED_CURRENCIES, toMinorUnits } from '../lib/format';
import { useDebounced } from '../lib/hooks';

const schema = z
  .object({
    sku: z.string().trim().regex(/^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$/, '2-64 letters, digits, ".", "-" or "_"'),
    name: z.string().trim().min(1, 'Name is required').max(200),
    description: z.string().max(2000).optional(),
    currency: z.string(),
    price: z.string().trim().min(1, 'Price is required'),
  })
  .superRefine((values, ctx) => {
    const minor = toMinorUnits(values.price, values.currency);
    if (minor === null || minor <= 0) ctx.addIssue({ code: 'custom', path: ['price'], message: `Enter a positive amount in ${values.currency}` });
  });

function ProductDialog({ open, product, defaultCurrency, onClose }) {
  const queryClient = useQueryClient();
  const idempotencyKey = useRef(newIdempotencyKey());
  const [error, setError] = useState(null);
  const { register, control, handleSubmit, reset, setError: setFieldError, formState: { errors, isSubmitting } } = useForm({ resolver: zodResolver(schema) });

  useEffect(() => {
    if (open) {
      idempotencyKey.current = newIdempotencyKey();
      setError(null);
      reset({
        sku: product?.sku ?? '',
        name: product?.name ?? '',
        description: product?.description ?? '',
        currency: product?.currency ?? defaultCurrency,
        price: product ? fromMinorUnits(product.priceMinor, product.currency) : '',
      });
    }
  }, [open, product, defaultCurrency, reset]);

  const onSubmit = async (values) => {
    // Amounts are converted to integer minor units here; no floating point reaches the API.
    const body = { name: values.name, description: values.description || null, priceMinor: toMinorUnits(values.price, values.currency), currency: values.currency };
    try {
      if (product) await productApi.update(product.id, body);
      else await productApi.create({ ...body, sku: values.sku }, idempotencyKey.current);
      await queryClient.invalidateQueries({ queryKey: ['tenant'] });
      onClose();
    } catch (e) {
      if (!applyFieldErrors(e, (field, err) => setFieldError(field === 'priceMinor' ? 'price' : field, err))) setError(e);
    }
  };

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm">
      <form onSubmit={handleSubmit(onSubmit)} noValidate>
        <DialogTitle>{product ? `Edit ${product.sku}` : 'New product'}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            {error && <Alert severity="error">{errorMessage(error)}</Alert>}
            {product && <Alert severity="info">Existing orders keep the price they were created with.</Alert>}
            <TextField label="SKU" disabled={Boolean(product)} {...register('sku')} error={!!errors.sku} helperText={errors.sku?.message ?? 'Unique within your company'} />
            <TextField label="Name" {...register('name')} error={!!errors.name} helperText={errors.name?.message} />
            <TextField label="Description" multiline minRows={2} {...register('description')} />
            <Stack direction="row" spacing={2}>
              <TextField label="Price" inputMode="decimal" {...register('price')} error={!!errors.price} helperText={errors.price?.message ?? 'e.g. 1500.75'} />
              <SelectField control={control} name="currency" label="Currency" options={SUPPORTED_CURRENCIES} sx={{ width: 140 }} />
            </Stack>
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="contained" disabled={isSubmitting}>Save</Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}

export default function ProductsPage() {
  const { can, tenant } = useAuth();
  const queryClient = useQueryClient();
  const table = useServerTable();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const debouncedSearch = useDebounced(search);
  const [editing, setEditing] = useState(undefined);
  const params = { ...table.params, search: debouncedSearch, status };
  const query = useQuery({ queryKey: useTenantKey('products', params), queryFn: () => productApi.list(params), placeholderData: (prev) => prev });
  const toggle = useMutation({
    mutationFn: ({ id, active }) => productApi.setStatus(id, active ? 'Active' : 'Inactive'),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['tenant'] }),
  });

  const writable = can('products.write');
  const columns = useMemo(() => [
    { field: 'sku', headerName: 'SKU', width: 140 },
    { field: 'name', headerName: 'Name', flex: 1, minWidth: 180 },
    { field: 'priceMinor', headerName: 'Price', width: 150, align: 'right', headerAlign: 'right', sortable: false, valueFormatter: (value, row) => formatMoney(value, row.currency) },
    {
      field: 'status', headerName: 'Status', width: 170, sortable: false,
      renderCell: ({ row }) => writable ? (
        <Stack direction="row" alignItems="center" spacing={0.5}>
          <Switch size="small" checked={row.status === 'Active'} onClick={(e) => e.stopPropagation()} onChange={(e) => toggle.mutate({ id: row.id, active: e.target.checked })} inputProps={{ 'aria-label': `Toggle ${row.sku}` }} />
          <StatusChip status={row.status} />
        </Stack>
      ) : <StatusChip status={row.status} />,
    },
    ...(writable ? [{
      field: 'actions', headerName: '', width: 60, sortable: false,
      renderCell: ({ row }) => <Tooltip title="Edit"><IconButton size="small" onClick={() => setEditing(row)} aria-label={`Edit ${row.sku}`}><EditOutlinedIcon fontSize="small" /></IconButton></Tooltip>,
    }] : []),
  ], [writable, toggle]);

  return (
    <>
      <PageHeader
        title="Products"
        subtitle="SKU, price and currency of what you sell. Prices are stored in minor units."
        actions={writable && <Button variant="contained" startIcon={<AddIcon />} onClick={() => setEditing(null)}>New product</Button>}
      />
      <Card>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ p: 2 }}>
          <TextField label="Search name or SKU" value={search} onChange={(e) => { setSearch(e.target.value); table.resetPage(); }} sx={{ maxWidth: 360 }} />
          <TextField select label="Status" value={status} onChange={(e) => { setStatus(e.target.value); table.resetPage(); }} sx={{ width: 180 }}>
            <MenuItem value="">All</MenuItem>
            <MenuItem value="Active">Active</MenuItem>
            <MenuItem value="Inactive">Inactive</MenuItem>
          </TextField>
        </Stack>
        <ServerTable query={query} columns={columns} table={table} emptyTitle="No products yet" emptyDescription={writable ? 'Add a product with a SKU and price.' : undefined} />
      </Card>
      <ProductDialog open={editing !== undefined} product={editing} defaultCurrency={tenant.defaultCurrency} onClose={() => setEditing(undefined)} />
    </>
  );
}
