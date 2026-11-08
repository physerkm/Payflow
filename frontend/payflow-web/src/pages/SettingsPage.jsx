import { useEffect, useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Alert, Button, Card, Grid, Stack, TextField } from '@mui/material';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { miscApi, tenantApi } from '../api/endpoints';
import { applyFieldErrors, errorMessage } from '../api/client';
import { KeyValue, PageHeader, Section, SelectField, ServerTable, useServerTable } from '../components/Common';
import { QueryState } from '../components/DataState';
import { formatDateTime, SUPPORTED_CURRENCIES } from '../lib/format';

const schema = z.object({
  name: z.string().trim().min(1, 'Name is required').max(120),
  defaultCurrency: z.string(),
  timeZone: z.string().min(1, 'Time zone is required'),
});

function TenantSettingsForm({ tenantDetails }) {
  const queryClient = useQueryClient();
  const { switchTenant } = useAuth();
  const [result, setResult] = useState(null);
  const { register, control, handleSubmit, reset, setError, formState: { errors, isSubmitting, isDirty } } = useForm({ resolver: zodResolver(schema) });

  useEffect(() => {
    reset({ name: tenantDetails.name, defaultCurrency: tenantDetails.defaultCurrency, timeZone: tenantDetails.timeZone });
  }, [tenantDetails, reset]);

  const onSubmit = async (values) => {
    setResult(null);
    try {
      await tenantApi.update(values);
      await switchTenant(tenantDetails.id); // refresh the session's tenant info (name, time zone)
      await queryClient.invalidateQueries({ queryKey: ['tenant'] });
      setResult({ ok: true });
    } catch (e) {
      if (!applyFieldErrors(e, setError)) setResult({ error: e });
    }
  };

  return (
    <Stack component="form" spacing={2} onSubmit={handleSubmit(onSubmit)} noValidate>
      {result?.ok && <Alert severity="success">Settings saved.</Alert>}
      {result?.error && <Alert severity="error">{errorMessage(result.error)}</Alert>}
      <TextField label="Company name" {...register('name')} error={!!errors.name} helperText={errors.name?.message} />
      <Stack direction="row" spacing={2}>
        <SelectField control={control} name="defaultCurrency" label="Default currency" options={SUPPORTED_CURRENCIES} helperText="Used for new products" />
        <TextField label="Time zone (IANA)" {...register('timeZone')} error={!!errors.timeZone} helperText={errors.timeZone?.message ?? 'e.g. Europe/Istanbul - used for reports'} />
      </Stack>
      <Button type="submit" variant="contained" disabled={!isDirty || isSubmitting} sx={{ alignSelf: 'flex-start' }}>Save settings</Button>
    </Stack>
  );
}

export default function SettingsPage() {
  const { tenant, can } = useAuth();
  const details = useQuery({ queryKey: useTenantKey('tenant-details'), queryFn: tenantApi.current });
  const table = useServerTable({ defaultSort: null });
  const auditParams = { page: table.params.page, pageSize: table.params.pageSize };
  const audit = useQuery({ queryKey: useTenantKey('audit', auditParams), queryFn: () => miscApi.auditLogs(auditParams), enabled: can('audit.read'), placeholderData: (prev) => prev });

  const columns = useMemo(() => [
    { field: 'occurredAt', headerName: 'When', width: 180, sortable: false, valueFormatter: (value) => formatDateTime(value, tenant.timeZone) },
    { field: 'action', headerName: 'Action', width: 200, sortable: false },
    { field: 'actorEmail', headerName: 'Actor', width: 220, sortable: false, valueFormatter: (value) => value ?? 'system' },
    { field: 'resourceType', headerName: 'Resource', width: 120, sortable: false },
    { field: 'metadata', headerName: 'Details', flex: 1, minWidth: 220, sortable: false },
    { field: 'correlationId', headerName: 'Correlation id', width: 200, sortable: false },
  ], [tenant.timeZone]);

  return (
    <>
      <PageHeader title="Settings" subtitle="Company settings and the audit trail of security- and finance-relevant actions." />
      <Grid container spacing={2}>
        <Grid size={{ xs: 12, md: 7 }}>
          <Section title="Company">
            <QueryState query={details}>{(data) => <TenantSettingsForm tenantDetails={data} />}</QueryState>
          </Section>
        </Grid>
        <Grid size={{ xs: 12, md: 5 }}>
          <Section title="Identifiers">
            <QueryState query={details}>
              {(data) => (
                <Stack spacing={2}>
                  <KeyValue label="Tenant id">{data.id}</KeyValue>
                  <KeyValue label="Slug">{data.slug}</KeyValue>
                  <KeyValue label="Status">{data.status}</KeyValue>
                  <KeyValue label="Created">{formatDateTime(data.createdAt, tenant.timeZone)}</KeyValue>
                </Stack>
              )}
            </QueryState>
          </Section>
        </Grid>
        {can('audit.read') && (
          <Grid size={12}>
            <Card>
              <Section title="Audit log" sx={{ border: 0 }}>
                <ServerTable query={audit} columns={columns} table={table} height={480} emptyTitle="No audit entries" />
              </Section>
            </Card>
          </Grid>
        )}
      </Grid>
    </>
  );
}
