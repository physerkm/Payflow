import { Box, Card, CardContent, Stack, ToggleButton, ToggleButtonGroup, Tooltip, Typography, useTheme } from '@mui/material';
import { LineChart } from '@mui/x-charts/LineChart';
import { BarChart } from '@mui/x-charts/BarChart';
import { vizPalette } from '../theme';
import { formatDate, formatMoney, formatNumber, formatPercent } from '../lib/format';

export function useVizColors() {
  const theme = useTheme();
  return vizPalette[theme.palette.mode === 'dark' ? 'dark' : 'light'];
}

/** Recessive hairline grid and axes; 2px lines; area as a ~10% wash. */
function chartSx(colors) {
  return {
    '& .MuiChartsGrid-line': { stroke: colors.grid, strokeWidth: 1 },
    '& .MuiChartsAxis-line, & .MuiChartsAxis-tick': { stroke: colors.axis },
    '& .MuiChartsAxis-tickLabel': { fill: colors.muted },
    '& .MuiLineElement-root': { strokeWidth: 2, strokeLinejoin: 'round', strokeLinecap: 'round' },
    '& .MuiAreaElement-root': { fillOpacity: 0.1 },
  };
}

export const RANGE_PRESETS = [
  { value: '7d', label: '7 days', days: 7 },
  { value: '30d', label: '30 days', days: 30 },
  { value: '90d', label: '90 days', days: 90 },
  { value: 'mtd', label: 'Month to date' },
];

export function rangeToParams(preset) {
  const to = new Date();
  if (preset === 'mtd') {
    return { from: new Date(to.getFullYear(), to.getMonth(), 1).toISOString(), to: to.toISOString() };
  }
  const days = RANGE_PRESETS.find((p) => p.value === preset)?.days ?? 30;
  return { from: new Date(to.getTime() - days * 86_400_000).toISOString(), to: to.toISOString() };
}

export function RangeFilter({ value, onChange }) {
  return (
    <ToggleButtonGroup size="small" exclusive value={value} onChange={(_, v) => v && onChange(v)} aria-label="Date range">
      {RANGE_PRESETS.map((p) => <ToggleButton key={p.value} value={p.value}>{p.label}</ToggleButton>)}
    </ToggleButtonGroup>
  );
}

/** Stat tile: label, value, optional helper line. `hero` renders the single headline figure of the view. */
export function StatTile({ label, value, helper, hero = false }) {
  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Typography variant="body2" color="text.secondary">{label}</Typography>
        <Typography sx={{ mt: 0.5, fontWeight: 600, fontSize: hero ? 48 : 26, lineHeight: 1.15, letterSpacing: '-0.02em' }}>{value}</Typography>
        {helper && <Typography variant="caption" color="text.secondary">{helper}</Typography>}
      </CardContent>
    </Card>
  );
}

/** Daily gross vs refunded amounts for one currency (two series on one money axis). */
export function RevenueChart({ points, currency, timeZone, height = 300 }) {
  const colors = useVizColors();
  const rows = points.filter((p) => p.currency === currency);
  if (rows.length === 0) {
    return <Typography variant="body2" color="text.secondary" sx={{ py: 8, textAlign: 'center' }}>No revenue in {currency} for this period.</Typography>;
  }
  const money = (v) => formatMoney(v, currency);
  return (
    <LineChart
      height={height}
      grid={{ horizontal: true }}
      xAxis={[{ scaleType: 'point', data: rows.map((p) => p.date), valueFormatter: (d) => formatDate(`${d}T12:00:00Z`, timeZone), tickNumber: 6 }]}
      yAxis={[{ valueFormatter: (v) => formatMoney(v, currency, { compact: true }), width: 72 }]}
      series={[
        { id: 'gross', label: 'Gross collected', data: rows.map((p) => p.grossMinor), color: colors.series[0], showMark: false, area: true, valueFormatter: money },
        { id: 'refunded', label: 'Refunded', data: rows.map((p) => p.refundedMinor), color: colors.series[1], showMark: false, valueFormatter: money },
      ]}
      sx={chartSx(colors)}
    />
  );
}

export function DailyBarChart({ labels, series, timeZone, height = 280, valueFormatter = (v) => formatNumber(v), stacked = false }) {
  const colors = useVizColors();
  return (
    <BarChart
      height={height}
      grid={{ horizontal: true }}
      borderRadius={4}
      xAxis={[{ scaleType: 'band', data: labels, valueFormatter: (d) => formatDate(`${d}T12:00:00Z`, timeZone), categoryGapRatio: 0.35, tickNumber: 6 }]}
      yAxis={[{ valueFormatter: (v) => formatNumber(v, true), width: 48, tickMinStep: 1 }]}
      series={series.map((s) => ({ ...s, stack: stacked ? 'total' : undefined, valueFormatter }))}
      hideLegend={series.length < 2}
      sx={{ ...chartSx(colors), '& .MuiBarElement-root': { stroke: 'var(--surface, transparent)', strokeWidth: stacked ? 2 : 0 } }}
    />
  );
}

/**
 * One whole split into parts (e.g. payment outcomes). Each block carries its share inside when it fits;
 * the legend names every part with its count, and hover gives name, count and share.
 */
const channel = (v) => { const c = parseInt(v, 16) / 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; };
const luminance = (hex) => 0.2126 * channel(hex.slice(1, 3)) + 0.7152 * channel(hex.slice(3, 5)) + 0.0722 * channel(hex.slice(5, 7));

/** White or near-black label, whichever contrasts more with the segment color (WCAG 1.4.3). */
export function readableInk(background) {
  const l = luminance(background);
  return (1.05 / (l + 0.05)) >= ((l + 0.05) / (luminance('#0b0b0b') + 0.05)) ? '#ffffff' : '#0b0b0b';
}

export function SegmentedBar({ parts, ariaLabel }) {
  const total = parts.reduce((sum, p) => sum + p.value, 0);
  if (total === 0) {
    return <Typography variant="body2" color="text.secondary">No payments in this period.</Typography>;
  }
  return (
    <Stack spacing={1.5}>
      <Box role="img" aria-label={`${ariaLabel}: ${parts.map((p) => `${p.label} ${p.value}`).join(', ')}`} sx={{ display: 'flex', gap: '2px', height: 28 }}>
        {parts.filter((p) => p.value > 0).map((p) => {
          const share = p.value / total;
          return (
            <Tooltip key={p.label} title={`${p.label}: ${formatNumber(p.value)} (${formatPercent(share)})`} arrow>
              <Box
                tabIndex={0}
                sx={{
                  flex: `${p.value} 0 0`, bgcolor: p.color, borderRadius: '4px', minWidth: 6, display: 'flex', alignItems: 'center', px: 1,
                  color: p.ink ?? readableInk(p.color), fontSize: 12, fontWeight: 600, overflow: 'hidden', whiteSpace: 'nowrap',
                }}
              >
                {share >= 0.12 ? formatPercent(share) : ''}
              </Box>
            </Tooltip>
          );
        })}
      </Box>
      <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
        {parts.map((p) => (
          <Stack key={p.label} direction="row" spacing={0.75} alignItems="center">
            <Box sx={{ width: 10, height: 10, borderRadius: '3px', bgcolor: p.color }} />
            <Typography variant="body2" color="text.secondary">{p.label}</Typography>
            <Typography variant="body2" fontWeight={600}>{formatNumber(p.value)}</Typography>
          </Stack>
        ))}
      </Stack>
    </Stack>
  );
}
