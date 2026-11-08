import { describe, expect, it } from 'vitest';
import { formatMoney, fromMinorUnits, humanize, minorUnits, toMinorUnits } from './format';

describe('money helpers (integer minor units, no floating point arithmetic)', () => {
  it('knows ISO 4217 minor units', () => {
    expect(minorUnits('TRY')).toBe(2);
    expect(minorUnits('JPY')).toBe(0);
    expect(minorUnits('KWD')).toBe(3);
  });

  it.each([
    ['1500,75', 'TRY', 150075],
    ['1500.75', 'TRY', 150075],
    ['1500', 'TRY', 150000],
    ['0.1', 'TRY', 10],
    ['1500', 'JPY', 1500],
    ['1.234', 'KWD', 1234],
  ])('parses %s %s to %i minor units', (input, currency, expected) => {
    expect(toMinorUnits(input, currency)).toBe(expected);
  });

  it.each([['1.999', 'TRY'], ['-5', 'TRY'], ['abc', 'TRY'], ['1.5', 'JPY'], ['', 'TRY']])('rejects %s %s', (input, currency) => {
    expect(toMinorUnits(input, currency)).toBeNull();
  });

  it('avoids the classic floating point trap', () => {
    // 0.1 + 0.2 !== 0.3 in floating point; minor units keep amounts exact.
    expect(toMinorUnits('0.1', 'TRY') + toMinorUnits('0.2', 'TRY')).toBe(toMinorUnits('0.3', 'TRY'));
  });

  it('round-trips through the edit form representation', () => {
    expect(fromMinorUnits(150075, 'TRY')).toBe('1500.75');
    expect(fromMinorUnits(5, 'TRY')).toBe('0.05');
    expect(toMinorUnits(fromMinorUnits(987654321, 'TRY'), 'TRY')).toBe(987654321);
  });

  it('formats with the currency', () => {
    expect(formatMoney(150075, 'TRY', { locale: 'en-US' }).replace(/\s/g, ' ')).toBe('TRY 1,500.75');
    expect(formatMoney(null, 'TRY')).toBe('—');
  });

  it('formats compact amounts without trailing zeros', () => {
    expect(formatMoney(2_000_000, 'USD', { locale: 'en-US', compact: true })).toBe('$20K');
    expect(formatMoney(1_550_000, 'USD', { locale: 'en-US', compact: true })).toBe('$15.5K');
  });
});

describe('humanize', () => {
  it('turns enum values into labels', () => {
    expect(humanize('PendingPayment')).toBe('Pending payment');
    expect(humanize('card_declined')).toBe('Card declined');
  });
});
