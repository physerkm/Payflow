import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, MenuItem, Stack, Table, TableBody, TableCell, TableHead,
  TableRow, TextField, Tooltip, Typography,
} from '@mui/material';
import PersonAddAlt1OutlinedIcon from '@mui/icons-material/PersonAddAlt1Outlined';
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { tenantApi } from '../api/endpoints';
import { applyFieldErrors, errorMessage } from '../api/client';
import { ConfirmDialog, PageHeader, Section, SelectField } from '../components/Common';
import { QueryState } from '../components/DataState';
import StatusChip from '../components/StatusChip';
import { formatDate, humanize } from '../lib/format';

const ROLES = ['Owner', 'Admin', 'Finance', 'Operator', 'ReadOnly'];
const ROLE_HELP = {
  Owner: 'Everything, including granting Owner',
  Admin: 'Members, settings, all operations',
  Finance: 'Payments, refunds, reports',
  Operator: 'Customers, products, orders, payments',
  ReadOnly: 'Read-only access and reports',
};

const inviteSchema = z.object({ email: z.string().trim().email('Enter a valid email'), role: z.enum(ROLES) });

function InviteDialog({ open, onClose, actorRole }) {
  const queryClient = useQueryClient();
  const [error, setError] = useState(null);
  const { register, control, handleSubmit, reset, setError: setFieldError, formState: { errors, isSubmitting } } = useForm({
    resolver: zodResolver(inviteSchema),
    defaultValues: { email: '', role: 'Operator' },
  });
  const roles = ROLES.filter((r) => r !== 'Owner' || actorRole === 'Owner');

  const onSubmit = async (values) => {
    setError(null);
    try {
      await tenantApi.invite(values);
      await queryClient.invalidateQueries({ queryKey: ['tenant'] });
      reset();
      onClose();
    } catch (e) {
      if (!applyFieldErrors(e, setFieldError)) setError(e);
    }
  };

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="xs">
      <form onSubmit={handleSubmit(onSubmit)} noValidate>
        <DialogTitle>Invite a member</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            {error && <Alert severity="error">{errorMessage(error)}</Alert>}
            <TextField label="Email" type="email" autoFocus {...register('email')} error={!!errors.email} helperText={errors.email?.message ?? 'They accept the invitation after signing in with this email'} />
            <SelectField control={control} name="role" label="Role" options={roles.map((r) => ({ value: r, label: `${humanize(r)} — ${ROLE_HELP[r]}` }))} />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="contained" disabled={isSubmitting}>Send invitation</Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}

export default function TeamPage() {
  const { user, tenant } = useAuth();
  const queryClient = useQueryClient();
  const [inviting, setInviting] = useState(false);
  const [removing, setRemoving] = useState(null);
  const members = useQuery({ queryKey: useTenantKey('members'), queryFn: tenantApi.members });
  const invitations = useQuery({ queryKey: useTenantKey('invitations'), queryFn: tenantApi.invitations });
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['tenant'] });

  const update = useMutation({ mutationFn: ({ id, body }) => tenantApi.updateMember(id, body), onSettled: invalidate });
  const remove = useMutation({ mutationFn: (id) => tenantApi.removeMember(id), onSuccess: () => setRemoving(null), onSettled: invalidate });
  const revoke = useMutation({ mutationFn: (id) => tenantApi.revokeInvitation(id), onSettled: invalidate });
  const mutationError = update.error ?? remove.error ?? revoke.error;

  return (
    <>
      <PageHeader
        title="Team"
        subtitle="Roles are checked on the server for every request; changes apply immediately."
        actions={<Button variant="contained" startIcon={<PersonAddAlt1OutlinedIcon />} onClick={() => setInviting(true)}>Invite member</Button>}
      />
      {mutationError && <Alert severity="error" sx={{ mb: 2 }} onClose={() => { update.reset(); remove.reset(); revoke.reset(); }}>{errorMessage(mutationError)}</Alert>}
      <Stack spacing={2}>
        <Section title="Members">
          <QueryState query={members}>
            {(list) => (
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Name</TableCell>
                    <TableCell>Email</TableCell>
                    <TableCell>Role</TableCell>
                    <TableCell>Status</TableCell>
                    <TableCell>Since</TableCell>
                    <TableCell align="right" />
                  </TableRow>
                </TableHead>
                <TableBody>
                  {list.map((m) => {
                    const self = m.userId === user.id;
                    const lockedOwner = m.role === 'Owner' && tenant.role !== 'Owner';
                    return (
                      <TableRow key={m.membershipId}>
                        <TableCell>{m.displayName}{self && <Typography component="span" variant="caption" color="text.secondary"> (you)</Typography>}</TableCell>
                        <TableCell>{m.email}</TableCell>
                        <TableCell>
                          <TextField
                            select
                            size="small"
                            value={m.role}
                            disabled={lockedOwner || update.isPending}
                            onChange={(e) => update.mutate({ id: m.membershipId, body: { role: e.target.value } })}
                            sx={{ minWidth: 140 }}
                            slotProps={{ htmlInput: { 'aria-label': `Role of ${m.email}` } }}
                          >
                            {ROLES.filter((r) => r !== 'Owner' || tenant.role === 'Owner' || m.role === 'Owner').map((r) => <MenuItem key={r} value={r}>{humanize(r)}</MenuItem>)}
                          </TextField>
                        </TableCell>
                        <TableCell>
                          <Button
                            size="small"
                            disabled={lockedOwner || self || update.isPending}
                            onClick={() => update.mutate({ id: m.membershipId, body: { status: m.status === 'Active' ? 'Disabled' : 'Active' } })}
                            startIcon={<StatusChip status={m.status} />}
                          >
                            {m.status === 'Active' ? 'Disable' : 'Enable'}
                          </Button>
                        </TableCell>
                        <TableCell>{formatDate(m.createdAt, tenant.timeZone)}</TableCell>
                        <TableCell align="right">
                          <Tooltip title="Remove from tenant">
                            <span>
                              <IconButton size="small" disabled={lockedOwner || self} onClick={() => setRemoving(m)} aria-label={`Remove ${m.email}`}><DeleteOutlineIcon fontSize="small" /></IconButton>
                            </span>
                          </Tooltip>
                        </TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
            )}
          </QueryState>
        </Section>
        <Section title="Invitations">
          <QueryState query={invitations} isEmpty={(d) => d.length === 0} empty={<Typography variant="body2" color="text.secondary">No invitations sent.</Typography>}>
            {(list) => (
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>Email</TableCell>
                    <TableCell>Role</TableCell>
                    <TableCell>Status</TableCell>
                    <TableCell>Expires</TableCell>
                    <TableCell align="right" />
                  </TableRow>
                </TableHead>
                <TableBody>
                  {list.map((i) => (
                    <TableRow key={i.id}>
                      <TableCell>{i.email}</TableCell>
                      <TableCell>{humanize(i.role)}</TableCell>
                      <TableCell><StatusChip status={i.status} /></TableCell>
                      <TableCell>{formatDate(i.expiresAt, tenant.timeZone)}</TableCell>
                      <TableCell align="right">{i.status === 'Pending' && <Button size="small" color="error" onClick={() => revoke.mutate(i.id)}>Revoke</Button>}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </QueryState>
        </Section>
      </Stack>
      <InviteDialog open={inviting} onClose={() => setInviting(false)} actorRole={tenant.role} />
      <ConfirmDialog
        open={Boolean(removing)}
        title="Remove member?"
        message={`${removing?.email} will immediately lose access to ${tenant.name}.`}
        confirmLabel="Remove"
        confirmColor="error"
        busy={remove.isPending}
        onConfirm={() => remove.mutate(removing.membershipId)}
        onClose={() => setRemoving(null)}
      />
    </>
  );
}
