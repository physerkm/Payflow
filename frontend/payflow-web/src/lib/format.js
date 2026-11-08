// Money is always handled as integer minor units (e.g. TRY 1500,75 -> 150075). Floating point is used
// only for display formatting, never for arithmetic or for values sent to the API.

const MINOR_UNITS = { JPY: 0, KRW: 0, KWD: 3, BHD: 3 };

export const SUPPORTED_CURRENCIES = ['TRY', 'USD', 'EUR', 'GBP', 'CHF', 'SEK', 'NOK', 'DKK', 'CAD', 'AUD', 'JPY', 'KRW', 'KWD', 'BHD'];

export const locale = typeof navigator !== 'undefined' && navigator.language ? navigator.language : 'en-US';

export function minorUnits(currency) {
  return MINOR_UNITS[currency] ?? 2;
}

export function formatMoney(amountMinor, currency, options = {}) {
  if (amountMinor === null || amountMinor === undefined || !currency) return '—';
  const digits = minorUnits(currency);
  // Compact values (chart axes) drop trailing zeros: "₺20 B", not "₺20,00 B".
  return new Intl.NumberFormat(options.locale ?? locale, {
    style: 'currency',
    currency,
    minimumFractionDigits: options.compact ? 0 : digits,
    maximumFractionDigits: options.compact ? 1 : digits,
    notation: options.compact ? 'compact' : 'standard',
  }).format(Number(amountMinor) / 10 ** digits);
}

/**
 * Parses a user-entered amount ("1500,75", "1500.75", "1500") into integer minor units without floating point.
 * Returns null when the input is not a valid amount for the currency.
 */
export function toMinorUnits(input, currency) {
  const digits = minorUnits(currency);
  const text = String(input ?? '').trim().replace(/\s/g, '');
  const pattern = digits === 0 ? /^\d+$/ : new RegExp(`^\\d+([.,]\\d{1,${digits}})?$`);
  if (!pattern.test(text)) return null;
  const [whole, fraction = ''] = text.split(/[.,]/);
  const value = Number(whole) * 10 ** digits + Number(fraction.padEnd(digits, '0') || 0);
  return Number.isSafeInteger(value) ? value : null;
}

/** Inverse of toMinorUnits, for pre-filling edit forms. */
export function fromMinorUnits(amountMinor, currency) {
  const digits = minorUnits(currency);
  if (digits === 0) return String(amountMinor);
  const sign = amountMinor < 0 ? '-' : '';
  const abs = Math.abs(amountMinor);
  const whole = Math.floor(abs / 10 ** digits);
  const fraction = String(abs % 10 ** digits).padStart(digits, '0');
  return `${sign}${whole}.${fraction}`;
}

export function formatDateTime(value, timeZone) {
  if (!value) return '—';
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short', timeZone: timeZone || undefined }).format(new Date(value));
}

export function formatDate(value, timeZone) {
  if (!value) return '—';
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: timeZone || undefined }).format(new Date(value));
}

export function formatPercent(ratio) {
  if (ratio === null || ratio === undefined) return '—';
  return new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 1 }).format(ratio);
}

export function formatNumber(value, compact = false) {
  return new Intl.NumberFormat(locale, { notation: compact ? 'compact' : 'standard' }).format(value ?? 0);
}

/** "PendingPayment" -> "Pending payment" */
export function humanize(value) {
  if (!value) return '';
  const spaced = String(value).replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

export function newIdempotencyKey() {
  return typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}
