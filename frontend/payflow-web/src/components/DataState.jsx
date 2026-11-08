import { Alert, AlertTitle, Box, Button, CircularProgress, Skeleton, Stack, Typography } from '@mui/material';
import InboxOutlinedIcon from '@mui/icons-material/InboxOutlined';
import { errorMessage } from '../api/client';

export function FullPageLoader() {
  return (
    <Box sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center' }} role="status" aria-label="Loading">
      <CircularProgress />
    </Box>
  );
}

export function LoadingState({ rows = 3 }) {
  return (
    <Stack spacing={1.5} role="status" aria-label="Loading">
      {Array.from({ length: rows }).map((_, i) => (
        <Skeleton key={i} variant="rounded" height={48} />
      ))}
    </Stack>
  );
}

export function ErrorState({ error, onRetry }) {
  return (
    <Alert
      severity="error"
      action={onRetry ? <Button color="inherit" size="small" onClick={onRetry}>Retry</Button> : undefined}
    >
      <AlertTitle>Could not load data</AlertTitle>
      {errorMessage(error)}
      {error?.correlationId && (
        <Typography variant="caption" display="block" sx={{ mt: 0.5, opacity: 0.8 }}>
          Correlation id: {error.correlationId}
        </Typography>
      )}
    </Alert>
  );
}

export function EmptyState({ title, description, action }) {
  return (
    <Stack alignItems="center" spacing={1} sx={{ py: 6, px: 2, textAlign: 'center' }}>
      <InboxOutlinedIcon sx={{ fontSize: 40, color: 'text.disabled' }} />
      <Typography variant="subtitle1" fontWeight={600}>{title}</Typography>
      {description && <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 420 }}>{description}</Typography>}
      {action}
    </Stack>
  );
}

/** Renders the loading / error / empty / success states of a TanStack query uniformly. */
export function QueryState({ query, isEmpty, empty, children, rows }) {
  if (query.isPending) return <LoadingState rows={rows} />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => query.refetch()} />;
  if (isEmpty?.(query.data)) return empty ?? <EmptyState title="Nothing here yet" />;
  return children(query.data);
}
