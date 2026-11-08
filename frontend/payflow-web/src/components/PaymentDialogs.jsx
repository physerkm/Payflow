import { useEffect, useRef, useState } from 'react';
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, Radio, RadioGroup, Stack, TextField, Typography,
} from '@mui/material';
import { errorMessage } from '../api/client';
import { formatMoney, fromMinorUnits, newIdempotencyKey, toMinorUnits } from '../lib/format';

// Test tokens of the simulated provider. PayFlow never accepts card numbers.
export const TEST_TOKENS = [
  { value: 'pm_test_success', label: 'Success', hint: 'Payment succeeds' },
  { value: 'pm_test_decline', label: 'Decline', hint: 'Definitive decline, no automatic retry' },
  { value: 'pm_test_timeout', label: 'Timeout', hint: 'Unknown outcome → resolved by reconciliation' },
  { value: 'pm_test_pending', label: 'Pending', hint: 'Confirmed later by a signed webhook' },
  { value: 'pm_test_error', label: 'Transient error', hint: 'First attempt fails, automatic retry succeeds' },
  { value: 'pm_test_error_persistent', label: 'Persistent error', hint: 'Fails after the retry budget' },
];

/**
 * One idempotency key per dialog opening: a double click or a network retry re-sends the same key, so the
 * server returns the original result instead of charging twice.
 */
export function PayDialog({ open, title = 'Start payment', amountLabel, submitLabel = 'Pay', onSubmit, onClose }) {
  const key = useRef(newIdempotencyKey());
  const [token, setToken] = useState('pm_test_success');
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (open) {
      key.current = newIdempotencyKey();
      setError(null);
      setToken('pm_test_success');
    }
  }, [open]);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      await onSubmit(token, key.current);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="sm">
      <DialogTitle>{title}</DialogTitle>
      <DialogContent>
        <Stack spacing={2}>
          {error && <Alert severity="error">{errorMessage(error)}</Alert>}
          {amountLabel && <Typography>Amount: <strong>{amountLabel}</strong> <Typography component="span" variant="body2" color="text.secondary">(determined by the server)</Typography></Typography>}
          <Typography variant="body2" color="text.secondary">Choose a test payment method of the simulated provider:</Typography>
          <RadioGroup value={token} onChange={(e) => setToken(e.target.value)}>
            {TEST_TOKENS.map((t) => (
              <FormControlLabel
                key={t.value}
                value={t.value}
                control={<Radio size="small" />}
                label={<span><strong>{t.label}</strong> <Typography component="span" variant="body2" color="text.secondary">— {t.hint}</Typography></span>}
              />
            ))}
          </RadioGroup>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="contained" onClick={submit} disabled={busy}>{busy ? 'Submitting…' : submitLabel}</Button>
      </DialogActions>
    </Dialog>
  );
}

export function RefundDialog({ open, payment, onSubmit, onClose }) {
  const key = useRef(newIdempotencyKey());
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const [error, setError] = useState(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (open && payment) {
      key.current = newIdempotencyKey();
      setAmount(fromMinorUnits(payment.refundableAmountMinor, payment.currency));
      setReason('');
      setError(null);
    }
  }, [open, payment]);

  if (!payment) return null;
  const minor = toMinorUnits(amount, payment.currency);
  const invalid = minor === null || minor <= 0 || minor > payment.refundableAmountMinor;

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      await onSubmit({ amountMinor: minor, reason: reason || null }, key.current);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} fullWidth maxWidth="xs">
      <DialogTitle>Refund payment</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error && <Alert severity="error">{errorMessage(error)}</Alert>}
          <Typography variant="body2" color="text.secondary">Refundable: {formatMoney(payment.refundableAmountMinor, payment.currency)}</Typography>
          <TextField
            label={`Amount (${payment.currency})`}
            value={amount}
            onChange={(e) => setAmount(e.target.value)}
            inputMode="decimal"
            error={amount !== '' && invalid}
            helperText={amount !== '' && invalid ? 'Enter a positive amount up to the refundable amount' : 'Partial refunds are allowed'}
          />
          <TextField label="Reason (optional)" value={reason} onChange={(e) => setReason(e.target.value)} />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="contained" color="warning" onClick={submit} disabled={invalid || busy}>{busy ? 'Submitting…' : 'Refund'}</Button>
      </DialogActions>
    </Dialog>
  );
}
