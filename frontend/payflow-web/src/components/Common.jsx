import { useState } from 'react';
import { Controller } from 'react-hook-form';
import {
  Box, Button, Card, CardContent, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle, MenuItem, Stack, TextField, Typography,
} from '@mui/material';
import { DataGrid } from '@mui/x-data-grid';
import { ErrorState, EmptyState } from './DataState';

export function PageHeader({ title, subtitle, actions }) {
  return (
    <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" alignItems={{ xs: 'flex-start', sm: 'center' }} spacing={2} sx={{ mb: 3 }}>
      <Box>
        <Typography variant="h5" component="h1">{title}</Typography>
        {subtitle && <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>{subtitle}</Typography>}
      </Box>
      {actions && <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>{actions}</Stack>}
    </Stack>
  );
}

export function Section({ title, action, children, sx }) {
  return (
    <Card sx={sx}>
      <CardContent>
        {(title || action) && (
          <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 2 }}>
            {title && <Typography variant="subtitle1" fontWeight={600} component="h2">{title}</Typography>}
            {action}
          </Stack>
        )}
        {children}
      </CardContent>
    </Card>
  );
}

export function KeyValue({ label, children }) {
  return (
    <Box>
      <Typography variant="caption" color="text.secondary" component="div">{label}</Typography>
      <Typography variant="body2" component="div" sx={{ mt: 0.25, fontWeight: 500 }}>{children ?? '—'}</Typography>
    </Box>
  );
}

export function ConfirmDialog({ open, title, message, confirmLabel = 'Confirm', confirmColor = 'primary', busy, onConfirm, onClose }) {
  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} maxWidth="xs" fullWidth>
      <DialogTitle>{title}</DialogTitle>
      <DialogContent><DialogContentText>{message}</DialogContentText></DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="contained" color={confirmColor} onClick={onConfirm} disabled={busy}>{confirmLabel}</Button>
      </DialogActions>
    </Dialog>
  );
}

/** Translates the DataGrid's 0-based pagination and sort model into the API's page/pageSize/sort params. */
export function useServerTable({ defaultSort = { field: 'createdAt', sort: 'desc' }, pageSize = 20, sortFieldMap = {} } = {}) {
  const [paginationModel, setPaginationModel] = useState({ page: 0, pageSize });
  const [sortModel, setSortModel] = useState(defaultSort ? [defaultSort] : []);
  const sort = sortModel[0] ? `${sortFieldMap[sortModel[0].field] ?? sortModel[0].field}:${sortModel[0].sort}` : undefined;
  return {
    params: { page: paginationModel.page + 1, pageSize: paginationModel.pageSize, sort },
    gridProps: {
      paginationMode: 'server',
      sortingMode: 'server',
      paginationModel,
      onPaginationModelChange: setPaginationModel,
      sortModel,
      onSortModelChange: setSortModel,
      pageSizeOptions: [10, 20, 50, 100],
    },
    resetPage: () => setPaginationModel((m) => ({ ...m, page: 0 })),
  };
}

export function ServerTable({ query, columns, table, onRowClick, emptyTitle, emptyDescription, height = 560 }) {
  if (query.isError) return <ErrorState error={query.error} onRetry={() => query.refetch()} />;
  return (
    <Box sx={{ height, width: '100%' }}>
      <DataGrid
        rows={query.data?.items ?? []}
        rowCount={query.data?.totalCount ?? 0}
        columns={columns}
        loading={query.isFetching}
        disableRowSelectionOnClick
        disableColumnFilter
        onRowClick={onRowClick ? (params) => onRowClick(params.row) : undefined}
        sx={{ border: 0, '& .MuiDataGrid-row': { cursor: onRowClick ? 'pointer' : 'default' } }}
        slots={{ noRowsOverlay: () => <EmptyState title={emptyTitle ?? 'No records'} description={emptyDescription} /> }}
        {...table.gridProps}
      />
    </Box>
  );
}

/** MUI select bound to react-hook-form through a Controller (keeps the displayed and form values in sync). */
export function SelectField({ control, name, label, options, helperText, ...props }) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <TextField select label={label} {...field} error={!!fieldState.error} helperText={fieldState.error?.message ?? helperText} {...props}>
          {options.map((option) => {
            const value = typeof option === 'string' ? option : option.value;
            const text = typeof option === 'string' ? option : option.label;
            return <MenuItem key={value} value={value}>{text}</MenuItem>;
          })}
        </TextField>
      )}
    />
  );
}
