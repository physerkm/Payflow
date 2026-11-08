import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import StatusChip from './StatusChip';

describe('StatusChip', () => {
  it('always renders a text label (status is never conveyed by color alone)', () => {
    render(<StatusChip status="PartiallyRefunded" />);
    expect(screen.getByText('Partially refunded')).toBeInTheDocument();
  });

  it('falls back gracefully for unknown statuses', () => {
    render(<StatusChip status="SomethingNew" />);
    expect(screen.getByText('Something new')).toBeInTheDocument();
  });
});
