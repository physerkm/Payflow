import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Alert, Button, Card, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, MenuItem, Stack, TextField, Tooltip } from '@mui/material';
import AddIcon from '@mui/icons-material/Add';
import EditOutlinedIcon from '@mui/icons-material/EditOutlined';
import PersonOffOutlinedIcon from '@mui/icons-material/PersonOffOutlined';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { customerApi } from '../api/endpoints';
import { applyFieldErrors, errorMessage } from '../api/client';
import { ConfirmDialog, PageHeader, ServerTable, useServerTable } from '../components/Common';
import StatusChip from '../components/StatusChip';
import { formatDate, newIdempotencyKey } from '../lib/format';
import { useDebounced } from '../lib/hooks';

const schema = z.object({
  name: z.string().trim().min(1, 'Name is required').max(200),
  email: z.union([z.literal(''), z.string().trim().email('Enter a valid email')]),
  phone: z.union([z.literal(''), z.string().regex(/^\+?[0-9 ()-]{5,32}$/, 'Enter a valid phone number')]),
});

function CustomerDialog({ open, customer, onClose }) {
  const queryClient = useQueryClient();
  const idempotencyKey = useRef(newIdempotencyKey());
  const [error, setError] = useState(null);
  const { register, handleSubmit, reset, setError: setFieldError, formState: { errors, isSubmitting } } = useForm({ resolver: zodResolver(schema) });

  useEffect(() => {
    if (open) {
      idempotencyKey.current = newIdempotencyKey(); // one key per dialog: double-submits are deduplicated
      setError(null);
      reset({ name: customer?.name ?? '', email: customer?.email ?? '', phone: customer?.phone ?? '' });
    }
  }, [open, customer, reset]);

  const onSubmit = async (values) => {
    const body = { name: values.name, email: values.email || null, phone: values.phone || null };
    try {
      if (customer) await customerApi.update(customer.id, body);
      else await customerApi.create(body, idempotencyKey.current);
      await queryClient.invalidateQueries({ queryKey: ['tenant'] });
      onClose();
    } catch (e) {
      if (!applyFieldErrors(e, setFieldError)) setError(e);
    }
  };

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm">
      <form onSubmit={handleSubmit(onSubmit)} noValidate>
        <DialogTitle>{customer ? 'Edit customer' : 'New customer'}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            {error && <Alert severity="error">{errorMessage(error)}</Alert>}
            <TextField label="Name" autoFocus {...register('name')} error={!!errors.name} helperText={errors.name?.message} />
            <TextField label="Email" type="email" {...register('email')} error={!!errors.email} helperText={errors.email?.message} />
            <TextField label="Phone" {...register('phone')} error={!!errors.phone} helperText={errors.phone?.message} />
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

export default function CustomersPage() {
  const { can, tenant } = useAuth();
  const queryClient = useQueryClient();
  const table = useServerTable();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const debouncedSearch = useDebounced(search);
  const [editing, setEditing] = useState(undefined);
  const [deactivating, setDeactivating] = useState(null);
  const params = { ...table.params, search: debouncedSearch, status };
  const query = useQuery({ queryKey: useTenantKey('customers', params), queryFn: () => customerApi.list(params), placeholderData: (prev) => prev });

  const deactivate = useMutation({
    mutationFn: (id) => customerApi.deactivate(id),
    onSuccess: () => {
      setDeactivating(null);
      queryClient.invalidateQueries({ queryKey: ['tenant'] });
    },
  });

  const writable = can('customers.write');
  const columns = useMemo(() => [
    { field: 'name', headerName: 'Name', flex: 1.2, minWidth: 180 },
    { field: 'email', headerName: 'Email', flex: 1.2, minWidth: 200 },
    { field: 'phone', headerName: 'Phone', flex: 0.8, minWidth: 140, sortable: false },
    { field: 'status', headerName: 'Status', width: 130, sortable: false, renderCell: ({ value }) => <StatusChip status={value} /> },
    { field: 'createdAt', headerName: 'Created', width: 140, valueFormatter: (value) => formatDate(value, tenant.timeZone) },
    ...(writable ? [{
      field: 'actions', headerName: '', width: 100, sortable: false, align: 'right',
      renderCell: ({ row }) => (
        <>
          <Tooltip title="Edit"><IconButton size="small" onClick={(e) => { e.stopPropagation(); setEditing(row); }} aria-label={`Edit ${row.name}`}><EditOutlinedIcon fontSize="small" /></IconButton></Tooltip>
          {row.status === 'Active' && (
            <Tooltip title="Deactivate"><IconButton size="small" onClick={(e) => { e.stopPropagation(); setDeactivating(row); }} aria-label={`Deactivate ${row.name}`}><PersonOffOutlinedIcon fontSize="small" /></IconButton></Tooltip>
          )}
        </>
      ),
    }] : []),
  ], [writable, tenant.timeZone]);

  return (
    <>
      <PageHeader
        title="Customers"
        subtitle="Customers of your company. Deactivated customers keep their order history."
        actions={writable && <Button variant="contained" startIcon={<AddIcon />} onClick={() => setEditing(null)}>New customer</Button>}
      />
      <Card>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ p: 2 }}>
          <TextField label="Search name or email" value={search} onChange={(e) => { setSearch(e.target.value); table.resetPage(); }} sx={{ maxWidth: 360 }} />
          <TextField select label="Status" value={status} onChange={(e) => { setStatus(e.target.value); table.resetPage(); }} sx={{ width: 180 }}>
            <MenuItem value="">All</MenuItem>
            <MenuItem value="Active">Active</MenuItem>
            <MenuItem value="Inactive">Inactive</MenuItem>
          </TextField>
        </Stack>
        <ServerTable query={query} columns={columns} table={table} emptyTitle="No customers yet" emptyDescription={writable ? 'Add your first customer to start taking orders.' : undefined} />
      </Card>
      <CustomerDialog open={editing !== undefined} customer={editing} onClose={() => setEditing(undefined)} />
      <ConfirmDialog
        open={Boolean(deactivating)}
        title="Deactivate customer?"
        message={`${deactivating?.name} will no longer be selectable for new orders. Existing orders and payments are kept.`}
        confirmLabel="Deactivate"
        confirmColor="error"
        busy={deactivate.isPending}
        onConfirm={() => deactivate.mutate(deactivating.id)}
        onClose={() => setDeactivating(null)}
      />
    </>
  );
}
