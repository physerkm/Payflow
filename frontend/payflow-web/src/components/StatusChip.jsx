import { Chip } from '@mui/material';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline';
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline';
import HourglassEmptyIcon from '@mui/icons-material/HourglassEmpty';
import HelpOutlineIcon from '@mui/icons-material/HelpOutline';
import UndoIcon from '@mui/icons-material/Undo';
import BlockIcon from '@mui/icons-material/Block';
import EditNoteIcon from '@mui/icons-material/EditNote';
import { humanize } from '../lib/format';

// Status is never conveyed by color alone: every chip carries an icon and a text label.
const STATUS = {
  Succeeded: ['success', CheckCircleOutlineIcon],
  Paid: ['success', CheckCircleOutlineIcon],
  Active: ['success', CheckCircleOutlineIcon],
  Processed: ['success', CheckCircleOutlineIcon],
  Accepted: ['success', CheckCircleOutlineIcon],
  Failed: ['error', ErrorOutlineIcon],
  PaymentFailed: ['error', ErrorOutlineIcon],
  Declined: ['error', ErrorOutlineIcon],
  Pending: ['warning', HourglassEmptyIcon],
  PendingPayment: ['warning', HourglassEmptyIcon],
  Queued: ['warning', HourglassEmptyIcon],
  Processing: ['warning', HourglassEmptyIcon],
  ProviderPending: ['warning', HourglassEmptyIcon],
  Created: ['warning', HourglassEmptyIcon],
  Unknown: ['warning', HelpOutlineIcon],
  PartiallyRefunded: ['info', UndoIcon],
  Refunded: ['info', UndoIcon],
  Cancelled: ['default', BlockIcon],
  Inactive: ['default', BlockIcon],
  Disabled: ['default', BlockIcon],
  Suspended: ['error', BlockIcon],
  Revoked: ['default', BlockIcon],
  Draft: ['default', EditNoteIcon],
};

export default function StatusChip({ status, size = 'small' }) {
  const [color, Icon] = STATUS[status] ?? ['default', HelpOutlineIcon];
  return <Chip size={size} color={color} variant="outlined" icon={<Icon />} label={humanize(status)} />;
}
