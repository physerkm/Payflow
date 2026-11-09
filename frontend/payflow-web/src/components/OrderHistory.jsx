import { Link as RouterLink } from 'react-router';
import { Box, Link, Stack, Typography } from '@mui/material';
import StatusChip from './StatusChip';
import { formatMoney, formatTimestamp, humanize } from '../lib/format';

const money = (e) => (e.amountMinor != null && e.currency ? formatMoney(e.amountMinor, e.currency) : null);
const code = (e) => (e.code ? humanize(e.code) : null);

/** Title, detail line and outcome chip for each history event type. */
const DESCRIBE = {
  'order.created': (e) => ({ title: 'Order created', detail: [money(e), e.note && `Note: ${e.note}`] }),
  'payment.initiated': (e) => ({ title: 'Payment started', detail: [money(e)] }),
  'payment.attempt': (e) => ({
    title: `Provider attempt ${e.attemptNumber}`,
    detail: [humanize(e.trigger), code(e)],
    status: e.status,
  }),
  'payment.succeeded': (e) => ({ title: 'Payment succeeded', detail: [money(e)], status: 'Succeeded' }),
  'payment.failed': (e) => ({ title: 'Payment failed', detail: [code(e)], status: 'Failed' }),
  'payment.cancelled': () => ({ title: 'Payment cancelled', status: 'Cancelled' }),
  'order.paid': () => ({ title: 'Order marked as paid', status: 'Paid' }),
  'refund.requested': (e) => ({ title: 'Refund requested', detail: [money(e), e.note && `Reason: ${e.note}`] }),
  'refund.succeeded': (e) => ({ title: 'Refund completed', detail: [money(e)], status: 'Succeeded' }),
  'refund.failed': (e) => ({ title: 'Refund failed', detail: [money(e), code(e)], status: 'Failed' }),
  'order.cancelled': () => ({ title: 'Order cancelled', status: 'Cancelled' }),
};

export default function OrderHistory({ entries, timeZone }) {
  return (
    <Box component="ol" aria-label="Order history" sx={{ listStyle: 'none', m: 0, p: 0 }}>
      {entries.map((e, index) => {
        const { title, detail = [], status } = (DESCRIBE[e.type] ?? (() => ({ title: humanize(e.type) })))(e);
        const last = index === entries.length - 1;
        return (
          <Box component="li" key={`${e.type}-${e.occurredAt}-${index}`} sx={{ display: 'flex', gap: 2 }}>
            {/* Rail: a dot per event joined by a line; purely decorative. */}
            <Box aria-hidden sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', pt: 0.75 }}>
              <Box sx={{ width: 10, height: 10, borderRadius: '50%', bgcolor: 'primary.main', flexShrink: 0 }} />
              {!last && <Box sx={{ width: 2, flexGrow: 1, bgcolor: 'divider', my: 0.5 }} />}
            </Box>
            <Box sx={{ pb: last ? 0 : 2, minWidth: 0, flexGrow: 1 }}>
              <Stack direction="row" spacing={1} alignItems="center" useFlexGap flexWrap="wrap">
                <Typography variant="body2" fontWeight={600}>{title}</Typography>
                {status && <StatusChip status={status} />}
              </Stack>
              <Typography variant="caption" color="text.secondary" component="div">
                <time dateTime={e.occurredAt}>{formatTimestamp(e.occurredAt, timeZone)}</time>
                {detail.filter(Boolean).map((d) => ` · ${d}`)}
                {e.type === 'payment.initiated' && e.paymentId && (
                  <> · <Link component={RouterLink} to={`/payments/${e.paymentId}`}>View payment</Link></>
                )}
              </Typography>
            </Box>
          </Box>
        );
      })}
    </Box>
  );
}
