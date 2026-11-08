import { useState } from 'react';
import { Link as RouterLink } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Card, Chip, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle, MenuItem, Stack,
  Table, TableBody, TableCell, TableHead, TableRow, TablePagination, TextField, Typography,
} from '@mui/material';
import { authApi, platformApi } from '../api/endpoints';
import { errorMessage } from '../api/client';
import { useAuth } from '../auth/AuthProvider';
import { QueryState } from '../components/DataState';
import StatusChip from '../components/StatusChip';
import { formatDate, humanize } from '../lib/format';
import { useDebounced } from '../lib/hooks';

/** Platform staff view: tenants as a whole. No access to any tenant's customers, orders or payments. */
export default function PlatformPage() {
  const { tenant, logout } = useAuth();
  const queryClient = useQueryClient();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(0);
  const [changing, setChanging] = useState(null);
  const [reason, setReason] = useState('');
  const debounced = useDebounced(search);
  const me = useQuery({ queryKey: ['me'], queryFn: authApi.me });
  const params = { search: debounced, status, page: page + 1, pageSize: 20 };
  const tenants = useQuery({ queryKey: ['platform', 'tenants', params], queryFn: () => platformApi.tenants(params), placeholderData: (prev) => prev, retry: false });

  const update = useMutation({
    mutationFn: () => platformApi.setStatus(changing.id, changing.status === 'Active' ? 'Suspended' : 'Active', reason),
    onSuccess: () => {
      setChanging(null);
      setReason('');
      queryClient.invalidateQueries({ queryKey: ['platform'] });
    },
  });

  const role = me.data?.platformRole;
  const isAdmin = role === 'PlatformAdmin';

  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default', p: { xs: 2, md: 6 } }}>
      <Stack spacing={3} sx={{ maxWidth: 1100, mx: 'auto' }}>
        <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" spacing={2}>
          <Box>
            <Stack direction="row" spacing={1} alignItems="center">
              <Typography variant="h5" component="h1">Platform administration</Typography>
              {role && <Chip size="small" color="secondary" label={humanize(role)} />}
            </Stack>
            <Typography variant="body2" color="text.secondary">
              Tenant status and size only. Platform roles never grant access to a tenant&apos;s business data; every change is audited.
            </Typography>
          </Box>
          <Stack direction="row" spacing={1}>
            <Button component={RouterLink} to={tenant ? '/' : '/tenants'}>Back</Button>
            <Button onClick={logout}>Sign out</Button>
          </Stack>
        </Stack>

        <Card>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ p: 2 }}>
            <TextField label="Search name or slug" value={search} onChange={(e) => { setSearch(e.target.value); setPage(0); }} sx={{ maxWidth: 320 }} />
            <TextField select label="Status" value={status} onChange={(e) => { setStatus(e.target.value); setPage(0); }} sx={{ width: 180 }}>
              <MenuItem value="">All</MenuItem>
              <MenuItem value="Active">Active</MenuItem>
              <MenuItem value="Suspended">Suspended</MenuItem>
            </TextField>
          </Stack>
          <Box sx={{ px: 2, pb: 2 }}>
            <QueryState query={tenants} isEmpty={(d) => d.items.length === 0}>
              {(data) => (
                <>
                  <Table size="small" aria-label="Tenants">
                    <TableHead>
                      <TableRow>
                        <TableCell>Tenant</TableCell>
                        <TableCell>Slug</TableCell>
                        <TableCell>Status</TableCell>
                        <TableCell align="right">Active members</TableCell>
                        <TableCell>Currency</TableCell>
                        <TableCell>Created</TableCell>
                        {isAdmin && <TableCell align="right" />}
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {data.items.map((t) => (
                        <TableRow key={t.id}>
                          <TableCell>{t.name}</TableCell>
                          <TableCell>{t.slug}</TableCell>
                          <TableCell><StatusChip status={t.status} /></TableCell>
                          <TableCell align="right">{t.activeMembers}</TableCell>
                          <TableCell>{t.defaultCurrency}</TableCell>
                          <TableCell>{formatDate(t.createdAt)}</TableCell>
                          {isAdmin && (
                            <TableCell align="right">
                              <Button size="small" color={t.status === 'Active' ? 'error' : 'primary'} onClick={() => setChanging(t)}>
                                {t.status === 'Active' ? 'Suspend' : 'Reactivate'}
                              </Button>
                            </TableCell>
                          )}
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                  <TablePagination component="div" count={data.totalCount} page={page} onPageChange={(_, p) => setPage(p)} rowsPerPage={20} rowsPerPageOptions={[20]} />
                </>
              )}
            </QueryState>
          </Box>
        </Card>
      </Stack>

      <Dialog open={Boolean(changing)} onClose={() => setChanging(null)} maxWidth="xs" fullWidth>
        <DialogTitle>{changing?.status === 'Active' ? `Suspend ${changing?.name}?` : `Reactivate ${changing?.name}?`}</DialogTitle>
        <DialogContent>
          <Stack spacing={2}>
            <DialogContentText>
              {changing?.status === 'Active'
                ? 'All members lose access on their next request. Payments in flight continue to be processed by the worker.'
                : 'Members regain access immediately.'}
            </DialogContentText>
            {update.isError && <Alert severity="error">{errorMessage(update.error)}</Alert>}
            <TextField label="Reason (audited)" value={reason} onChange={(e) => setReason(e.target.value)} autoFocus />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setChanging(null)}>Cancel</Button>
          <Button variant="contained" color={changing?.status === 'Active' ? 'error' : 'primary'} disabled={!reason.trim() || update.isPending} onClick={() => update.mutate()}>
            Confirm
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
