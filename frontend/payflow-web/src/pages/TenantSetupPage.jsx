import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import {
  Alert, Box, Button, Card, CardContent, Divider, Grid, List, ListItemButton, ListItemText, Stack, TextField, Typography,
} from '@mui/material';
import { useAuth } from '../auth/AuthProvider';
import { authApi, tenantApi } from '../api/endpoints';
import { applyFieldErrors, errorMessage } from '../api/client';
import { humanize, SUPPORTED_CURRENCIES } from '../lib/format';
import StatusChip from '../components/StatusChip';
import { SelectField } from '../components/Common';

const schema = z.object({
  name: z.string().trim().min(1, 'Company name is required').max(120),
  slug: z.string().regex(/^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$/, '3-50 lowercase letters, digits or hyphens'),
  defaultCurrency: z.string(),
  timeZone: z.string().min(1),
});

const TIME_ZONES = ['Europe/Istanbul', 'Europe/London', 'Europe/Berlin', 'America/New_York', 'Asia/Dubai', 'UTC'];

const slugify = (value) => value.toLowerCase().normalize('NFKD').replace(/[̀-ͯ]/g, '').replace(/ı/g, 'i').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 50);

export default function TenantSetupPage() {
  const { user, switchTenant, logout } = useAuth();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [error, setError] = useState(null);
  const memberships = useQuery({ queryKey: ['memberships'], queryFn: authApi.memberships });
  const invitations = useQuery({ queryKey: ['my-invitations'], queryFn: authApi.invitations });
  const me = useQuery({ queryKey: ['me'], queryFn: authApi.me });

  const { register, control, handleSubmit, setValue, setError: setFieldError, formState: { errors, isSubmitting, dirtyFields } } = useForm({
    resolver: zodResolver(schema),
    defaultValues: { name: '', slug: '', defaultCurrency: 'TRY', timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'Europe/Istanbul' },
  });

  const open = async (tenantId) => {
    setError(null);
    try {
      await switchTenant(tenantId);
      navigate('/', { replace: true });
    } catch (e) {
      setError(e);
    }
  };

  const accept = useMutation({
    mutationFn: authApi.acceptInvitation,
    onSuccess: async (invitation) => {
      await queryClient.invalidateQueries({ queryKey: ['memberships'] });
      await queryClient.invalidateQueries({ queryKey: ['my-invitations'] });
      await open(invitation.tenantId);
    },
    onError: setError,
  });

  const onCreate = async (values) => {
    setError(null);
    try {
      const tenant = await tenantApi.create(values);
      await open(tenant.id);
    } catch (e) {
      if (!applyFieldErrors(e, setFieldError)) setError(e);
    }
  };

  const active = (memberships.data ?? []).filter((m) => m.status === 'Active' && m.tenantStatus === 'Active');
  const zones = TIME_ZONES.includes(Intl.DateTimeFormat().resolvedOptions().timeZone) ? TIME_ZONES : [Intl.DateTimeFormat().resolvedOptions().timeZone, ...TIME_ZONES];

  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default', p: { xs: 2, md: 6 } }}>
      <Stack spacing={3} sx={{ maxWidth: 960, mx: 'auto' }}>
        <Stack direction="row" justifyContent="space-between" alignItems="center">
          <Box>
            <Typography variant="h5" component="h1">Welcome, {user.displayName}</Typography>
            <Typography variant="body2" color="text.secondary">Choose a company to work in, accept an invitation or create a new one.</Typography>
          </Box>
          <Stack direction="row" spacing={1}>
            {me.data?.platformRole && <Button variant="outlined" onClick={() => navigate('/platform')}>Platform admin</Button>}
            <Button onClick={logout}>Sign out</Button>
          </Stack>
        </Stack>
        {error && <Alert severity="error">{errorMessage(error)}</Alert>}
        <Grid container spacing={3}>
          <Grid size={{ xs: 12, md: 6 }}>
            <Card sx={{ height: '100%' }}>
              <CardContent>
                <Typography variant="subtitle1" fontWeight={600} component="h2">Your companies</Typography>
                {memberships.isPending && <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>Loading…</Typography>}
                {memberships.isSuccess && active.length === 0 && (
                  <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>You are not a member of any company yet.</Typography>
                )}
                <List>
                  {active.map((m) => (
                    <ListItemButton key={m.tenantId} onClick={() => open(m.tenantId)} sx={{ borderRadius: 2 }}>
                      <ListItemText primary={m.tenantName} secondary={`${humanize(m.role)} · ${m.tenantSlug}`} />
                    </ListItemButton>
                  ))}
                </List>
                {(invitations.data ?? []).length > 0 && (
                  <>
                    <Divider sx={{ my: 2 }} />
                    <Typography variant="subtitle2" gutterBottom>Pending invitations</Typography>
                    {invitations.data.map((inv) => (
                      <Stack key={inv.id} direction="row" alignItems="center" justifyContent="space-between" sx={{ py: 1 }}>
                        <Box>
                          <Typography variant="body2" fontWeight={600}>{inv.tenantName}</Typography>
                          <StatusChip status={inv.role} />
                        </Box>
                        <Button size="small" variant="outlined" onClick={() => accept.mutate(inv.id)} disabled={accept.isPending}>Accept</Button>
                      </Stack>
                    ))}
                  </>
                )}
              </CardContent>
            </Card>
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <Card>
              <CardContent>
                <Typography variant="subtitle1" fontWeight={600} component="h2" gutterBottom>Create a company</Typography>
                <Stack component="form" spacing={2} onSubmit={handleSubmit(onCreate)} noValidate>
                  <TextField
                    label="Company name"
                    {...register('name', { onChange: (e) => !dirtyFields.slug && setValue('slug', slugify(e.target.value)) })}
                    error={!!errors.name}
                    helperText={errors.name?.message}
                  />
                  <TextField label="Slug" {...register('slug')} error={!!errors.slug} helperText={errors.slug?.message ?? 'Unique identifier, e.g. acme-ltd'} />
                  <Stack direction="row" spacing={2}>
                    <SelectField control={control} name="defaultCurrency" label="Default currency" options={SUPPORTED_CURRENCIES} />
                    <SelectField control={control} name="timeZone" label="Time zone" options={zones} />
                  </Stack>
                  <Button type="submit" variant="contained" disabled={isSubmitting}>{isSubmitting ? 'Creating…' : 'Create company'}</Button>
                </Stack>
              </CardContent>
            </Card>
          </Grid>
        </Grid>
      </Stack>
    </Box>
  );
}
