import { describe, expect, it } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import OrderHistory from './OrderHistory';

const entries = [
  { occurredAt: '2026-11-08T10:00:00Z', type: 'order.created', amountMinor: 150075, currency: 'TRY' },
  { occurredAt: '2026-11-08T10:00:01Z', type: 'payment.initiated', amountMinor: 150075, currency: 'TRY', paymentId: 'p1' },
  { occurredAt: '2026-11-08T10:00:02Z', type: 'payment.attempt', status: 'Failed', code: 'provider_error', attemptNumber: 1, trigger: 'Initial', paymentId: 'p1' },
  { occurredAt: '2026-11-08T10:00:05Z', type: 'payment.attempt', status: 'Succeeded', attemptNumber: 2, trigger: 'AutomaticRetry', paymentId: 'p1' },
  { occurredAt: '2026-11-08T10:05:00Z', type: 'refund.requested', amountMinor: 50000, currency: 'TRY', note: 'damaged', paymentId: 'p1' },
];

describe('OrderHistory', () => {
  it('renders one list item per event, in the given order, with readable outcomes', () => {
    render(<MemoryRouter><OrderHistory entries={entries} timeZone="UTC" /></MemoryRouter>);

    const items = within(screen.getByRole('list', { name: 'Order history' })).getAllByRole('listitem');
    expect(items).toHaveLength(5);
    expect(items[0]).toHaveTextContent('Order created');
    expect(items[2]).toHaveTextContent('Provider attempt 1');
    expect(items[2]).toHaveTextContent('Provider error');
    expect(within(items[2]).getByText('Failed')).toBeInTheDocument();
    expect(items[3]).toHaveTextContent('Automatic retry');
    expect(items[4]).toHaveTextContent('Reason: damaged');
    expect(screen.getByRole('link', { name: 'View payment' })).toHaveAttribute('href', '/payments/p1');
  });
});
